using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal interface IUiCommands
    {
        void AddLogViewerWindow(LogViewerWindow logViewerWindow);
    }

    internal class MainWindow : IDisposable, IUiCommands
    {
        private Etw.OpenActiveSession _openActiveSession = new();
        private List<LogViewerWindow> _logViewerWindows = new();
        private List<LogViewerWindow> _pendingNewLogViewWindows = new();
        private bool _showOpenActiveSession;
        private bool _isDisposed;

        public MainWindow(string[] args)
        {
            if (args.Length == 1 && Path.Exists(args[0]))
            {
                if (Path.GetExtension(args[0]) == ".etl")
                {
                    var etlSession = Etw.EtwTraceSource.CreateEtlSession(args[0]);
                    _logViewerWindows.Add(new LogViewerWindow(etlSession));
                }
                else if (Path.GetExtension(args[0]) == ".wprp")
                {
                    var wprp = Etw.Wprp.Load(args[0]);
                    var realTimeSession = Etw.EtwTraceSource.CreateRealTimeSession(wprp.Profiles[0].ConvertToSessionProfile());
                    _logViewerWindows.Add(new LogViewerWindow(realTimeSession));
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
                        Settings.FontType font = Settings.Font;
                        if (ImGui.BeginMenu("Font"))
                        {
                            if (ImGui.MenuItem("Droid Sans", "", font == Settings.FontType.DroidSans))
                            {
                                Settings.Font = Settings.FontType.DroidSans;
                            }
                            else if (ImGui.MenuItem("Cascadia Mono (fixed)", "", font == Settings.FontType.CascadiaMono))
                            {
                                Settings.Font = Settings.FontType.CascadiaMono;
                            }
                            else if (ImGui.MenuItem("Proggy Clean (13px fixed)", "", font == Settings.FontType.ProggyClean))
                            {
                                Settings.Font = Settings.FontType.ProggyClean;
                            }
                            ImGui.EndMenu();
                        }

                        ImGui.BeginDisabled(Settings.Font == Settings.FontType.ProggyClean);
                        if (ImGui.BeginMenu("Font size"))
                        {
                            foreach (int fontSize in new[] { 12, 14, 16, 18, 20, 22, 24 })
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
                if (ImGui.BeginMenu("ETW"))
                {
                    if (ImGui.MenuItem("Open .WPRP (real-time) ..."))
                    {
                        // TODO: This blocks the render thread
                        string file = OpenFile("Windows Performance Recorder Profile (*.wprp)|*.wprp",
                            Settings.WprpOpenLocation,
                            (s) => Settings.WprpOpenLocation = s);
                        if (!string.IsNullOrEmpty(file))
                        {
                            Settings.AddRecentlyOpenedWprp(file);

                            var wprp = Etw.Wprp.Load(file);

                            // TODO: Show selector window of profiles and their providers. Allow user to uncheck things first.
                            var selectedProfile = wprp.Profiles[0];
                            var realTimeSession = Etw.EtwTraceSource.CreateRealTimeSession(selectedProfile.ConvertToSessionProfile());

                            _logViewerWindows.Add(new LogViewerWindow(realTimeSession));
                        }
                    }

                    if (ImGui.MenuItem("Open .ETL ..."))
                    {
                        // TODO: This blocks the render thread
                        string file = OpenFile("ETL Trace File (*.etl)|*.etl",
                            Settings.EtlOpenLocation,
                            (s) => Settings.EtlOpenLocation = s);
                        if (!string.IsNullOrEmpty(file))
                        {
                            var etlSession = Etw.EtwTraceSource.CreateEtlSession(file);
                            _logViewerWindows.Add(new LogViewerWindow(etlSession));
                        }
                    }

                    if (ImGui.MenuItem("Open active session ..."))
                    {
                        _showOpenActiveSession = true;
                    }

                    IReadOnlyList<string> wprpMru = Settings.GetRecentlyOpenedWprp();
                    if (wprpMru.Count > 0)
                    {
                        ImGui.Separator();
                        if (ImGui.BeginMenu("Recently .WPRP files"))
                        {
                            foreach (var file in wprpMru)
                            {
                                if (ImGui.MenuItem(file))
                                {
                                    Settings.AddRecentlyOpenedWprp(file);

                                    // TODO: Show selector window of profiles and their providers. Allow user to uncheck things first.
                                    var wprp = Etw.Wprp.Load(file);
                                    var realTimeSession = Etw.EtwTraceSource.CreateRealTimeSession(wprp.Profiles[0].ConvertToSessionProfile());

                                    _logViewerWindows.Add(new LogViewerWindow(realTimeSession));
                                }
                            }

                            ImGui.EndMenu();
                        }
                    }

                    ImGui.EndMenu();
                }
            }

            ImGui.EndMainMenuBar();
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
