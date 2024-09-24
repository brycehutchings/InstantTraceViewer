using System;
using System.Numerics;
using System.Collections.Generic;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal class LogViewerWindow : IDisposable
    {
        private readonly ITraceSource _traceSource;
        private static HashSet<int> _selectedRowIndices = new HashSet<int>();
        private static int? _lastSelectedIndex;
        private string _findBuffer = string.Empty;

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
            if (ImGui.Shortcut(ImGuiKey.F | ImGuiKey.ModCtrl))
            {
                ImGui.SetKeyboardFocusHere();
            }

            int? setScrollIndex = null;
            if (ImGui.InputTextWithHint("", "Find...", ref _findBuffer, 1024, ImGuiInputTextFlags.EnterReturnsTrue) && !string.IsNullOrEmpty(_findBuffer))
            {
                // Focus goes somewhere else when enter is pressed but we want to keep focus so the user can keep pressing enter to go to the next match.
                ImGui.SetKeyboardFocusHere(-1);

                _traceSource.ReadUnderLock(traceRecords =>
                {
                    int startIndex = _lastSelectedIndex.HasValue ? _lastSelectedIndex.Value + 1 : 0;
                    for (int i = startIndex; i < traceRecords.Count; i++)
                    {
                        if (traceRecords[i].Message.Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase))
                        {
                            setScrollIndex = i;
                            _lastSelectedIndex = i;
                            _selectedRowIndices.Clear();
                            _selectedRowIndices.Add(i);
                            break;
                        }
                    }
                });
            }

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

                ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2)); // Tighten spacing

                int recordCount = 0;
                TraceLevel? lastLevel = null;
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                _traceSource.ReadUnderLock(traceRecords =>
                {
                    recordCount = traceRecords.Count;
                    clipper.Begin(traceRecords.Count);
                    while (clipper.Step())
                    {
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            // Don't bother to scroll to the selected index if it's already in view.
                            if (i == setScrollIndex)
                            {
                                setScrollIndex = null;
                            }

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

                            // Create an empty selectable that spans the full row to enable row selection.
                            bool isSelected = _selectedRowIndices.Contains(i);
                            if (ImGui.Selectable($"##TableRow_{i}", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                            {
                                if (ImGui.GetIO().KeyShift)
                                {
                                    if (_lastSelectedIndex.HasValue)
                                    {
                                        _selectedRowIndices.Clear();
                                        for (int j = System.Math.Min(i, _lastSelectedIndex.Value); j <= System.Math.Max(i, _lastSelectedIndex.Value); j++)
                                        {
                                            _selectedRowIndices.Add(j);
                                        }
                                    }
                                }
                                else if (ImGui.GetIO().KeyCtrl)
                                {
                                    if (isSelected)
                                    {
                                        _selectedRowIndices.Remove(i);
                                    }
                                    else
                                    {
                                        _selectedRowIndices.Add(i);
                                    }

                                    _lastSelectedIndex = i;
                                }
                                else
                                {
                                    _selectedRowIndices.Clear();
                                    _selectedRowIndices.Add(i);
                                    _lastSelectedIndex = i;
                                }
                            }
                            ImGui.SameLine();
                            ImGui.TextUnformatted(traceRecords[i].Timestamp.ToString("HH:mm:ss.ffffff"));

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

                            var singleLineMessage = traceRecords[i].Message.Replace("\n", " ").Replace("\r", " ");
                            ImGui.TextUnformatted(singleLineMessage);
                        }
                    }
                    clipper.End();
                });

                if (lastLevel.HasValue)
                {
                    ImGui.PopStyleColor(); // ImGuiCol_Text
                }

                ImGui.PopStyleVar(); // CellPadding

                if (setScrollIndex.HasValue)
                {
                    ImGui.SetScrollY(setScrollIndex.Value / (float)recordCount * ImGui.GetScrollMaxY());
                }
                else if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                {
                    // Auto scroll - AKA keep the table scrolled to the bottom as new messages come in, but only if the table is already
                    // scrolled to the bottom.
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
