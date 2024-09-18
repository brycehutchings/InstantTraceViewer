using System.Numerics;
using System.Collections.Generic;
using ImGuiNET;
using System;

namespace InstantTraceViewerUI
{
    internal class LogViewerWindow : IDisposable
    {
        private readonly ITraceSource _traceSource;
        private static HashSet<int> selectedRowIndices = new HashSet<int>();
        private static int? lastSelectedIndex;

        private bool _isDisposed;

        public LogViewerWindow(ITraceSource traceSource)
        {
            _traceSource = traceSource;
        }

        public bool IsClosed => _isDisposed;

        public void DrawWindow()
        {
            if (IsClosed)
            {
                return;
            }

            bool opened = true;
            if (ImGui.Begin(_traceSource.DisplayName, ref opened))
            {
                DrawWindowContents();
            }

            ImGui.End();

            if (!opened)
            {
                Dispose();
            }
        }

        private unsafe void DrawWindowContents()
        {
            if (ImGui.BeginTable("DebugPanelLogger",
                  8 /* columns */,
                  ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                      ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                      ImGuiTableFlags.Hideable))
            {
                ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                ImGui.TableSetupColumn("Pid", ImGuiTableColumnFlags.WidthFixed, 40.0f);
                ImGui.TableSetupColumn("Tid", ImGuiTableColumnFlags.WidthFixed, 40.0f);
                ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("OpCode", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Provider", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableHeadersRow();

                ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(1, 1)); // Tighten spacing

                TraceLevel? lastLevel = null;
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                _traceSource.ReadUnderLock(traceRecords =>
                {
                    clipper.Begin(traceRecords.Count);
                    while (clipper.Step())
                    {
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            ImGui.TableNextRow();

                            // Update StyleColor if level changed.
                            if (lastLevel != traceRecords[i].Level)
                            {
                                if (lastLevel.HasValue)
                                {
                                    ImGui.PopStyleColor(); // ImGuiCol_Text changed.
                                }
                                ImGui.PushStyleColor(ImGuiCol.Text, LevelToColor(traceRecords[i].Level));
                                lastLevel = traceRecords[i].Level;
                            }

                            ImGui.TableNextColumn();
                            bool isSelected = selectedRowIndices.Contains(i);
                            if (ImGui.Selectable(traceRecords[i].Timestamp.ToString("HH:mm:ss.ffffff"), isSelected, ImGuiSelectableFlags.SpanAllColumns))
                            {
                                if (ImGui.GetIO().KeyShift)
                                {
                                    if (lastSelectedIndex.HasValue)
                                    {
                                        selectedRowIndices.Clear();
                                        for (int j = System.Math.Min(i, lastSelectedIndex.Value); j <= System.Math.Max(i, lastSelectedIndex.Value); j++)
                                        {
                                            selectedRowIndices.Add(j);
                                        }
                                    }
                                }
                                else if (ImGui.GetIO().KeyCtrl)
                                {
                                    if (isSelected)
                                    {
                                        selectedRowIndices.Remove(i);
                                    }
                                    else
                                    {
                                        selectedRowIndices.Add(i);
                                    }

                                    lastSelectedIndex = i;
                                }
                                else
                                {
                                    selectedRowIndices.Clear();
                                    selectedRowIndices.Add(i);
                                    lastSelectedIndex = i;
                                }
                            }

                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(_traceSource.GetProcessName(traceRecords[i].ProcessId));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(_traceSource.GetThreadName(traceRecords[i].ThreadId));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(traceRecords[i].Level.ToString());
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(_traceSource.GetOpCodeName(traceRecords[i].OpCode));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(traceRecords[i].ProviderName);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(traceRecords[i].Name);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(traceRecords[i].Message);
                        }
                    }
                    clipper.End();
                });

                if (lastLevel.HasValue)
                {
                    ImGui.PopStyleColor(); // ImGuiCol_Text
                }

                ImGui.PopStyleVar(); // CellPadding

                // Auto scroll - AKA keep the table scrolled to the bottom as new messages come in, but only if the table is already
                // scrolled to the bottom.
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                {
                    ImGui.SetScrollHereY(1.0f);
                }

                ImGui.EndTable();
            }

        }

        private static Vector4 LevelToColor(TraceLevel level)
        {
            return level == TraceLevel.Verbose ? new Vector4(0.75f, 0.75f, 0.75f, 1.0f)     // Gray
                   : level == TraceLevel.Warning ? new Vector4(1.0f, 0.65f, 0.0f, 1.0f)     // Orange
                   : level == TraceLevel.Error ? new Vector4(0.75f, 0.0f, 0.0f, 1.0f)       // Red
                   : level == TraceLevel.Critical ? new Vector4(0.60f, 0.0f, 0.0f, 1.0f)    // Dark Red
                                                   : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);   // White
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _traceSource.Dispose();
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
