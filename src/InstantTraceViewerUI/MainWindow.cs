using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Windows.Forms;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using ImGuiNET;
using InstantTraceViewer;

namespace InstantTraceViewerUI
{
    internal interface IUiCommands
    {
        void AddLogViewerWindow(LogViewerWindow logViewerWindow);
    }

    internal class MainWindow : IDisposable, IUiCommands
    {
        private readonly AdbClient _adbClient = new AdbClient();
        private IReadOnlyList<DeviceData> _adbDevices = null;
        private Exception _adbDevicesException = null;

        private Etw.OpenActiveSession _openActiveSession = new();
        private List<LogViewerWindow> _logViewerWindows = new();
        private List<LogViewerWindow> _pendingNewLogViewWindows = new();
        private bool _showOpenActiveSession;
        private bool _isDisposed;

        public MainWindow(string[] args)
        {
            if (args.Length == 1 && Path.Exists(args[0]))
            {
                if (Etw.EtwTraceSource.EtlFileExtensions.Contains(Path.GetExtension(args[0]), StringComparer.OrdinalIgnoreCase))
                {
                    var etlSession = Etw.EtwTraceSource.CreateEtlSession(args[0]);
                    _logViewerWindows.Add(new LogViewerWindow(etlSession));
                }
                else if (string.Equals(Path.GetExtension(args[0]), ".wprp", StringComparison.OrdinalIgnoreCase))
                {
                    var wprp = Etw.Wprp.Load(args[0]);
                    var realTimeSession = Etw.EtwTraceSource.CreateRealTimeSession(wprp.Profiles[0].ConvertToSessionProfile());
                    _logViewerWindows.Add(new LogViewerWindow(realTimeSession));
                }
                else if (string.Equals(Path.GetExtension(args[0]), ".csv", StringComparison.OrdinalIgnoreCase))
                {
                    // TODO: We need a --no-header option for CSV files.
                    var csvTableSource = new CsvTableSource(args[0], firstRowIsHeader: true, readInBackground: true);
                    _logViewerWindows.Add(new LogViewerWindow(csvTableSource));
                }
                else if (string.Equals(Path.GetExtension(args[0]), ".tsv", StringComparison.OrdinalIgnoreCase))
                {
                    // TODO: We need a --no-header option for TSV files.
                    var tsvTableSource = new TsvTableSource(args[0], firstRowIsHeader: true, readInBackground: true);
                    _logViewerWindows.Add(new LogViewerWindow(tsvTableSource));
                }
            }
        }

        public bool IsExitRequested { get; private set; }

        public void AddLogViewerWindow(LogViewerWindow logViewerWindow)
        {
            // Can't add to _logViewerWindows since the collection might be enumerated during the add.
            _pendingNewLogViewWindows.Add(logViewerWindow);
        }

        public void Draw()
        {
            uint dockId = ImGui.DockSpaceOverViewport();

            DrawMenuBar();

            // Force the (first) next window to be docked to fill window. Generally this is what people will want, rather than a smaller, floating window.
            ImGui.SetNextWindowDockID(dockId, ImGuiCond.FirstUseEver);

            _logViewerWindows.AddRange(_pendingNewLogViewWindows);
            _pendingNewLogViewWindows.Clear();

            foreach (var logViewerWindow in _logViewerWindows)
            {
                logViewerWindow.DrawWindow(this);
            }

            if (_showOpenActiveSession)
            {
                _openActiveSession.DrawWindow(this, ref _showOpenActiveSession);
            }

            // Clean up any closed windows. A window is determined to be closed during the DrawWindow call so this comes afterwards.
            CleanUpClosedLogViewerWindows();
        }

        private void CleanUpClosedLogViewerWindows()
        {
            var closedWindows = _logViewerWindows.Where(w => w.IsClosed);
            foreach (var win in closedWindows)
            {
                win.Dispose();
            }

            _logViewerWindows.RemoveAll(w => closedWindows.Contains(w));
        }

        private void DrawMenuBar()
        {
            if (ImGui.BeginMainMenuBar())
            {
                ImGui.SetNextItemShortcut(ImGuiKey.F | ImGuiKey.ModAlt, ImGuiInputFlags.RouteGlobal);
                if (ImGui.BeginMenu("File"))
                {
                    if (ImGui.BeginMenu("Settings"))
                    {
                        if (ImGui.MenuItem("Associate with .etl extension"))
                        {
                            Settings.AssociateWithEtlExtensions();
                        }

                        ImGuiTheme theme = Settings.Theme;
                        if (ImGui.BeginMenu("Theme"))
                        {
                            if (ImGui.MenuItem("Light", "", theme == ImGuiTheme.Light))
                            {
                                Settings.Theme = ImGuiTheme.Light;
                            }
                            else if (ImGui.MenuItem("Dark", "", theme == ImGuiTheme.Dark))
                            {
                                Settings.Theme = ImGuiTheme.Dark;
                            }
                            ImGui.EndMenu();
                        }

                        FontType font = Settings.Font;
                        if (ImGui.BeginMenu("Font"))
                        {
                            if (ImGui.MenuItem("Segoe UI", "", font == FontType.SegoeUI))
                            {
                                Settings.Font = FontType.SegoeUI;
                            }
                            else if (ImGui.MenuItem("Droid Sans", "", font == FontType.DroidSans))
                            {
                                Settings.Font = FontType.DroidSans;
                            }
                            else if (ImGui.MenuItem("Cascadia Mono (fixed)", "", font == FontType.CascadiaMono))
                            {
                                Settings.Font = FontType.CascadiaMono;
                            }
                            else if (ImGui.MenuItem("Proggy Clean (13px fixed)", "", font == FontType.ProggyClean))
                            {
                                Settings.Font = FontType.ProggyClean;
                            }
                            ImGui.EndMenu();
                        }

                        ImGui.BeginDisabled(Settings.Font == FontType.ProggyClean);
                        if (ImGui.BeginMenu("Font size"))
                        {
                            foreach (int fontSize in new[] { 12, 13, 14, 15, 16, 17, 18, 20, 22, 24 })
                            {
                                if (ImGui.MenuItem(fontSize.ToString(), "", Settings.FontSize == fontSize))
                                {
                                    Settings.FontSize = fontSize;
                                }
                            }
                            ImGui.EndMenu();
                        }
                        ImGui.EndDisabled();

                        ImGui.EndMenu();
                    }

                    ImGui.Separator();

                    if (ImGui.MenuItem("Exit", "ALT+F4"))
                    {
                        IsExitRequested = true;
                    }

                    ImGui.EndMenu();
                }

                ImGui.SetNextItemShortcut(ImGuiKey.E | ImGuiKey.ModAlt, ImGuiInputFlags.RouteGlobal);
                if (ImGui.BeginMenu("Etw"))
                {
                    if (ImGui.MenuItem("Open WPRP file (real-time)..."))
                    {
                        // TODO: This blocks the render thread
                        string file = OpenFile("Windows Performance Recorder Profile Files (*.wprp)|*.wprp",
                            Settings.WprpOpenLocation,
                            (s) => Settings.WprpOpenLocation = s);
                        if (!string.IsNullOrEmpty(file))
                        {
                            Settings.AddRecentlyOpenedWprp(file);

                            try
                            {
                                var wprp = Etw.Wprp.Load(file);

                                // TODO: Show selector window of profiles and their providers. Allow user to uncheck things first.
                                var selectedProfile = wprp.Profiles[0];
                                var realTimeSession = Etw.EtwTraceSource.CreateRealTimeSession(selectedProfile.ConvertToSessionProfile());

                                _logViewerWindows.Add(new LogViewerWindow(realTimeSession));
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Failed to open .WPRP file or start ETW session.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }

                    if (ImGui.MenuItem("Open ETL file..."))
                    {
                        string joinedExts = string.Join(';', Etw.EtwTraceSource.EtlFileExtensions.Select(ext => $"*{ext}"));

                        // TODO: This blocks the render thread
                        string file = OpenFile($"ETL Trace Files ({joinedExts})|{joinedExts}",
                            Settings.EtlOpenLocation,
                            (s) => Settings.EtlOpenLocation = s);
                        if (!string.IsNullOrEmpty(file))
                        {
                            try
                            {
                                var etlSession = Etw.EtwTraceSource.CreateEtlSession(file);
                                _logViewerWindows.Add(new LogViewerWindow(etlSession));
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Failed to open .etl file.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }

                    if (ImGui.MenuItem("Open active session..."))
                    {
                        _showOpenActiveSession = true;
                    }

                    IReadOnlyList<string> wprpMru = Settings.GetRecentlyOpenedWprp();
                    if (wprpMru.Count > 0)
                    {
                        ImGui.Separator();
                        if (ImGui.BeginMenu("Recent WPRP files (real-time)"))
                        {
                            foreach (var file in wprpMru)
                            {
                                if (ImGui.MenuItem(file))
                                {
                                    Settings.AddRecentlyOpenedWprp(file);

                                    try
                                    {
                                        // TODO: Show selector window of profiles and their providers. Allow user to uncheck things first.
                                        var wprp = Etw.Wprp.Load(file);
                                        var realTimeSession = Etw.EtwTraceSource.CreateRealTimeSession(wprp.Profiles[0].ConvertToSessionProfile());

                                        _logViewerWindows.Add(new LogViewerWindow(realTimeSession));
                                    }
                                    catch (Exception ex)
                                    {
                                        MessageBox.Show($"Failed to open .WPRP file.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    }
                                }
                            }

                            ImGui.EndMenu();
                        }
                    }

                    ImGui.EndMenu();
                }


                void LoopHeaderOption(Action<bool, string> callback)
                {
                    foreach (bool withHeader in new[] { true, false })
                    {
                        string headerNote = withHeader ? "header" : "no header";
                        callback(withHeader, headerNote);
                    }
                }

                ImGui.SetNextItemShortcut(ImGuiKey.C | ImGuiKey.ModAlt, ImGuiInputFlags.RouteGlobal);
                if (ImGui.BeginMenu("Csv"))
                {
                    LoopHeaderOption((withHeader, headerNote) =>
                    {
                        if (ImGui.MenuItem($"Open CSV file ({headerNote})..."))
                        {
                            // TODO: This blocks the render thread
                            string file = OpenFile("Comma-Separated Values Files (*.csv)|*.csv",
                                Settings.CsvOpenLocation,
                                (s) => Settings.CsvOpenLocation = s);
                            if (!string.IsNullOrEmpty(file))
                            {
                                try
                                {
                                    var csvTableSource = new CsvTableSource(file, withHeader, readInBackground: true);
                                    _logViewerWindows.Add(new LogViewerWindow(csvTableSource));
                                }
                                catch (Exception ex)
                                {
                                    MessageBox.Show($"Failed to open .CSV file.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }
                            }
                        }
                    });

                    ImGui.Separator();

                    LoopHeaderOption((withHeader, headerNote) =>
                    {
                        // NOTE: When Clipboard.ContainsText(TextDataFormat.CommaSeparatedValue) is true, GetText() returns __TSV__ data but GetData(CommaSeparatedValue) will give __CSV__ data.
                        // Excel probably puts both kinds into the keyboard buffer. Right now we don't query this though because CSV data may be copied into the clipboard from other sources which
                        // don't set this format.
                        if (ImGui.MenuItem($"Read CSV from clipboard ({headerNote})"))
                        {
                            try
                            {
                                string clipboardText = Clipboard.GetText();
                                var csvTableSource = new CsvTableSource("Clipboard CSV", new StringReader(clipboardText), withHeader, readInBackground: true);
                                _logViewerWindows.Add(new LogViewerWindow(csvTableSource));
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Failed to read CSV from clipboard.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    });

                    ImGui.EndMenu();
                }

                ImGui.SetNextItemShortcut(ImGuiKey.T | ImGuiKey.ModAlt, ImGuiInputFlags.RouteGlobal);
                if (ImGui.BeginMenu("Tsv"))
                {
                    LoopHeaderOption((withHeader, headerNote) =>
                    {
                        if (ImGui.MenuItem($"Open TSV file ({headerNote})..."))
                        {
                            // TODO: This blocks the render thread
                            string file = OpenFile("Tab-separated values file (*.tsv)|*.tsv",
                                Settings.TsvOpenLocation,
                                (s) => Settings.TsvOpenLocation = s);
                            if (!string.IsNullOrEmpty(file))
                            {
                                try
                                {
                                    var tsvTableSource = new TsvTableSource(file, withHeader, readInBackground: true);
                                    _logViewerWindows.Add(new LogViewerWindow(tsvTableSource));
                                }
                                catch (Exception ex)
                                {
                                    MessageBox.Show($"Failed to open .TSV file.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }
                            }
                        }
                    });

                    ImGui.Separator();

                    LoopHeaderOption((withHeader, headerNote) =>
                    {
                        // NOTE: When Clipboard.ContainsText(TextDataFormat.CommaSeparatedValue) is true, GetText() returns __TSV__ data but GetData(CommaSeparatedValue) will give __CSV__ data.
                        // Excel probably puts both kinds into the keyboard buffer. Right now we don't query this though because CSV data may be copied into the clipboard from other sources which
                        // don't set this format.
                        if (ImGui.MenuItem($"Read TSV from clipboard ({headerNote})"))
                        {
                            try
                            {
                                string clipboardText = Clipboard.GetText();
                                var tsvTableSource = new TsvTableSource("Clipboard CSV", new StringReader(clipboardText), withHeader, readInBackground: true);
                                _logViewerWindows.Add(new LogViewerWindow(tsvTableSource));
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Failed to read TSV from clipboard.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    });

                    ImGui.EndMenu();
                }

                ImGui.SetNextItemShortcut(ImGuiKey.A | ImGuiKey.ModAlt, ImGuiInputFlags.RouteGlobal);
                if (ImGui.BeginMenu("Android"))
                {
                    if (_adbDevices == null && _adbDevicesException == null)
                    {
                        try
                        {
                            _adbDevices = _adbClient.GetDevices().ToList();
                        }
                        catch (SocketException ex)
                        {
                            _adbDevicesException = ex;
                            _adbDevices = Array.Empty<DeviceData>();
                        }
                    }

                    if (_adbDevicesException != null)
                    {
                        // TODO: See if adb.exe is in the PATH and offer to start the server.
                        ImGui.Text("ADB server not running");
                    }
                    else if (_adbDevices.Count == 0)
                    {
                        ImGui.Text("No devices found");
                    }

                    foreach (var device in _adbDevices)
                    {
                        if (ImGui.BeginMenu($"{device.Name} {device.Model} {device.Serial}"))
                        {
                            if (ImGui.MenuItem("Open logcat"))
                            {
                                var logcat = new Logcat.LogcatTraceSource(_adbClient, device);
                                _logViewerWindows.Add(new LogViewerWindow(logcat));
                            }
                            ImGui.EndMenu();
                        }
                    }

                    ImGui.Separator();

                    if (ImGui.MenuItem("Refresh devices"))
                    {
                        _adbDevices = null;
                        _adbDevicesException = null;
                    }

                    ImGui.EndMenu();
                }

                ImGui.EndMainMenuBar();
            }
        }

        private string OpenFile(string filter, string initialDirectory, Action<string> saveInitialDirectory)
        {
            var dialog = new OpenFileDialog();
            dialog.InitialDirectory = initialDirectory;
            dialog.Filter = filter;
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                saveInitialDirectory(Path.GetDirectoryName(dialog.FileName));

                return dialog.FileName;
            }

            return null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    foreach (var logViewerWindow in _logViewerWindows)
                    {
                        logViewerWindow.Dispose();
                    }
                    _logViewerWindows.Clear();
                    _openActiveSession.Dispose();
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
