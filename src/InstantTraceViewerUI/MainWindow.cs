using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal class MainWindow : IDisposable
    {
        private List<LogViewerWindow> _logViewerWindows = new();
        private bool _isDisposed;

        public MainWindow()
        {
            Etw.Wprp wprp = Etw.Wprp.Load("D:\\repos\\cloud1\\bin\\tools\\Tracing\\trace-configs\\WPR\\MixedRealityLinkGeneral.wprp");
            // Etw.Wprp wprp = Etw.Wprp.Load("D:\\repos\\cloud1\\tools\\Tracing\\Configs\\WPR\\BuildTrace.wprp");
            using var etwTraceSource = Etw.EtwTraceSource.CreateRealTimeSession(wprp.Profiles[0].ConvertToSessionProfile());

            _logViewerWindows.Add(new LogViewerWindow(etwTraceSource));
        }

        public bool IsExitRequested { get; private set; }

        public void Draw()
        {
            DrawMenuBar();

            foreach (var logViewerWindow in _logViewerWindows)
            {
                logViewerWindow.DrawWindow();
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
                if (ImGui.BeginMenu("File"))
                {
                    if (ImGui.MenuItem("Open .wprp (ETW)..."))
                    {
                        // TODO: This blocks the render thread
                        string file = OpenFile("Windows Performance Recorder Profiles (*.wprp)|*.wprp");
                        if (!string.IsNullOrEmpty(file))
                        {
                            var wprp = Etw.Wprp.Load(file);

                            // TODO: Show selector window of profiles and their providers. Allow user to uncheck things first.
                            var selectedProfile = wprp.Profiles[0];
                            var realTimeSession = Etw.EtwTraceSource.CreateRealTimeSession(selectedProfile.ConvertToSessionProfile());

                            _logViewerWindows.Add(new LogViewerWindow(realTimeSession));
                        }
                    }

                    if (ImGui.MenuItem("Exit"))
                    {
                        IsExitRequested = true;
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
