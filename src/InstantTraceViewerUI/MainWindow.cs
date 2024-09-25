using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal class MainWindow : IDisposable
    {
        private Etw.OpenActiveSession _openActiveSession = new();
        private List<LogViewerWindow> _logViewerWindows = new();
        private bool _showOpenActiveSession;
        private bool _isDisposed;

        public MainWindow()
        {
        }

        public bool IsExitRequested { get; private set; }

        public void Draw()
        {
            uint dockId = ImGui.DockSpaceOverViewport();

            DrawMenuBar();

            // Force the (first) next window to be docked to fill window. Generally this is what people will want, rather than a smaller, floating window.
            ImGui.SetNextWindowDockID(dockId, ImGuiCond.FirstUseEver);

            foreach (var logViewerWindow in _logViewerWindows)
            {
                logViewerWindow.DrawWindow();
            }

            if (_showOpenActiveSession)
            {
                _openActiveSession.DrawWindow(ref _showOpenActiveSession, _logViewerWindows);
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
                        // TODO: Font size, padding, etc.
                        ImGui.MenuItem("TODO ;-)");
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
                        string file = OpenFile("Windows Performance Recorder Profile (*.wprp)|*.wprp");
                        if (!string.IsNullOrEmpty(file))
                        {
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
                        string file = OpenFile("ETL Trace File (*.etl)|*.etl");
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

                    ImGui.EndMenu();
                }
            }

            ImGui.EndMainMenuBar();
        }

        private string OpenFile(string filter)
        {
            var dialog = new OpenFileDialog();
            dialog.Filter = filter;
            if (dialog.ShowDialog() == DialogResult.OK)
            {
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
