using ImGuiNET;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace InstantTraceViewerUI.Etw
{
    internal class OpenActiveSession : IDisposable
    {
        private IReadOnlyList<TraceEventSession> _activeSessions = null;
        private bool disposedValue;

        public void DrawWindow(IUiCommands uiCommands, ref bool showOpenActiveSession)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("Active Sessions (ETW)", ref showOpenActiveSession))
            {
                if (ImGui.Button("Refresh") || _activeSessions == null)
                {
                    DisposeActiveSessions();
                    var names = TraceEventSession.GetActiveSessionNames();
                    _activeSessions = names.Select(TraceEventSession.GetActiveSession).ToList();
                }

                if (!_activeSessions.Any() && !(TraceEventSession.IsElevated() ?? false))
                {
                    ImGui.Text("No active sessions found. Run this application as administrator to see active sessions.");
                }

                if (ImGui.BeginTable("ActiveSessions", 7 /* columns */,
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                    ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable))
                {
                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.4f);
                    ImGui.TableSetupColumn("Filename", ImGuiTableColumnFlags.WidthStretch, 0.6f);
                    ImGui.TableSetupColumn("Realtime", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("InMemoryCircular", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("Circular", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 45.0f);
                    ImGui.TableHeadersRow();

                    var addColumnValue = (string text) =>
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(text);
                    };

                    foreach (var session in _activeSessions)
                    {
                        ImGui.PushID(session.SessionName);

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();

                        bool buttonAdded = false;
                        if (session.IsRealTime)
                        {
                            if (ImGui.Button("Open real-time"))
                            {
                                try
                                {
                                    // Create a separate session object to have its own lifetime for IDisposable.
                                    var logViewerEtwSession = TraceEventSession.GetActiveSession(session.SessionName);

                                    // TraceEventSession doesn't seem to expose the enabled providers, so we will just assume the kernel provider is not enabled so we try to look up process names.
                                    bool kernelProcessThreadProviderEnabled = false;
                                    uiCommands.AddLogViewerWindow(new LogViewerWindow(new Etw.EtwTraceSource(logViewerEtwSession, kernelProcessThreadProviderEnabled, logViewerEtwSession.SessionName)));
                                    showOpenActiveSession = false; // Close this window.
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Failed to open real-time session: {ex}");
                                    // TODO: Show message box
                                }
                            }
                            buttonAdded = true;
                        }
                        if (!string.IsNullOrEmpty(session.FileName))
                        {
                            if (buttonAdded)
                            {
                                // So far I haven't seen a session be both real-time and logged to a file, but it should be possible.
                                ImGui.SameLine();
                            }
                            if (ImGui.Button("Open ETL file"))
                            {
                                try
                                {
                                    session.Flush(); // Flush the session to ensure the ETL file is up-to-date.
                                    uiCommands.AddLogViewerWindow(new LogViewerWindow(Etw.EtwTraceSource.CreateEtlSession(session.FileName)));
                                    showOpenActiveSession = false; // Close this window.
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Failed to open ETL file: {ex}");
                                    // TODO: Show message box
                                }
                            }
                        }

                        addColumnValue(session.SessionName);
                        addColumnValue(session.FileName);
                        addColumnValue(session.IsRealTime.ToString());
                        addColumnValue(session.IsInMemoryCircular.ToString());
                        addColumnValue(session.IsCircular.ToString());
                        addColumnValue($"{session.BufferSizeMB}MB");

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();
        }

        private void DisposeActiveSessions()
        {
            if (_activeSessions != null)
            {
                foreach (var session in _activeSessions)
                {
                    session.Dispose();
                }
                _activeSessions = null;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    DisposeActiveSessions();
                }

                disposedValue = true;
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