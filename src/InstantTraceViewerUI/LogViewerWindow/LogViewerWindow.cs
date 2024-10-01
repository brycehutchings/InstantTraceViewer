using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal class LogViewerWindow : IDisposable
    {
        private static int _nextWindowId = 1;

        private readonly SharedTraceSource _traceSource;
        private readonly int _windowId;

        // ViewerRules determine which rows are visible.
        private ViewerRules _viewerRules = new();

        private readonly List<int> _visibleRows = new();
        private int _nextTraceSourceRowIndex = 0;
        private int _errorCount = 0;
        private int _visibleRowsGenerationId = -1;

        // TODO: Right now these hold the index into _visibleRows but we should probably change it to hold the index into the trace source so that filtering rule changes keep the same underlying thing selected.
        private HashSet<int> _selectedRowIndices = new HashSet<int>();
        private int? _lastSelectedVisibleRowIndex;

        private string _findBuffer = string.Empty;
        private bool _findFoward = true;
        private bool _isDisposed;

        public LogViewerWindow(SharedTraceSource traceSource)
        {
            _traceSource = traceSource;
            _traceSource.AddRef(this);

            _windowId = _nextWindowId++;
        }

        public LogViewerWindow(ITraceSource traceSource)
            : this(new SharedTraceSource(traceSource))
        {
        }

        public bool IsClosed => _isDisposed;

        private void UpdateVisibleRows(int generationId, IReadOnlyList<TraceRecord> traceRecords)
        {
            // TODO: This may be slow if generation id changes and millions of rows need to be reprocessed. At a minimum we should show a busy indicator.
            //       Even better would be a progress bar. To make this work, we could process for XX milliseconds into a separate List and then AddRange with a short lock.
            if (generationId != _visibleRowsGenerationId)
            {
                Debug.WriteLine("Rebuilding visible rows...");
                _visibleRows.Clear();
                _nextTraceSourceRowIndex = 0;
                _errorCount = 0;
            }

            for (int i = _nextTraceSourceRowIndex; i < traceRecords.Count; i++)
            {
                if (_viewerRules.VisibleRules.Count == 0 || _viewerRules.GetVisibleAction(traceRecords[i]) == TraceRecordRuleAction.Include)
                {
                    if (traceRecords[i].Level == TraceLevel.Error)
                    {
                        _errorCount++;
                    }

                    _visibleRows.Add(i);
                }
            }

            if (_nextTraceSourceRowIndex == 0)
            {
                Debug.WriteLine("Done rebuilding visible rows.");
            }

            _visibleRowsGenerationId = generationId;
            _nextTraceSourceRowIndex = traceRecords.Count;
        }

        public void DrawWindow(IUiCommands uiCommands)
        {
            if (IsClosed)
            {
                return;
            }

            _traceSource.TraceSource.ReadUnderLock((generationId, traceRecords) => UpdateVisibleRows(generationId, traceRecords));

            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _selectedRowIndices.Clear();
                _lastSelectedVisibleRowIndex = null;
            }

            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);

            bool opened = true;
            if (ImGui.Begin($"{_traceSource.TraceSource.DisplayName}###LogViewerWindow_{_windowId}", ref opened))
            {
                DrawWindowContents(uiCommands);
            }

            ImGui.End();

            if (!opened)
            {
                Dispose();
            }
        }

        private unsafe void DrawWindowContents(IUiCommands uiCommands)
        {
            int? setScrollIndex = null;
            DrawToolStrip(uiCommands, ref setScrollIndex);

            if (ImGui.BeginTable("DebugPanelLogger", 8 /* columns */,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                ImGuiTableFlags.Hideable))
            {
                ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                ImGui.TableSetupColumn("Process", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Thread", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Provider", ImGuiTableColumnFlags.WidthFixed, 100.0f);
                ImGui.TableSetupColumn("OpCode", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableHeadersRow();

                ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2)); // Tighten spacing

                int recordCount = 0;
                Vector4? lastColor = null;

                var setColor = (Vector4? color) =>
                {
                    if (color != lastColor)
                    {
                        if (lastColor.HasValue)
                        {
                            ImGui.PopStyleColor(); // ImGuiCol_Text changed.
                        }
                        if (color.HasValue)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
                        }
                        lastColor = color;
                    }
                };

                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                _traceSource.TraceSource.ReadUnderLock((generationId, traceRecords) =>
                {
                    UpdateVisibleRows(generationId, traceRecords);

                    recordCount = _visibleRows.Count;
                    clipper.Begin(_visibleRows.Count);
                    while (clipper.Step())
                    {
                        for (int visibleRowIndex = clipper.DisplayStart; visibleRowIndex < clipper.DisplayEnd; visibleRowIndex++)
                        {
                            // Don't bother to scroll to the selected index if it's already in view.
                            if (visibleRowIndex == setScrollIndex)
                            {
                                setScrollIndex = null;
                            }

                            int i = _visibleRows[visibleRowIndex];

                            ImGui.PushID(i);

                            ImGui.TableNextRow();

                            setColor(LevelToColor(traceRecords[i].Level));

                            ImGui.TableNextColumn();

                            // Create an empty selectable that spans the full row to enable row selection.
                            bool isSelected = _selectedRowIndices.Contains(visibleRowIndex);
                            if (ImGui.Selectable($"##TableRow", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                            {
                                if (ImGui.GetIO().KeyShift)
                                {
                                    if (_lastSelectedVisibleRowIndex.HasValue)
                                    {
                                        _selectedRowIndices.Clear();
                                        for (int j = System.Math.Min(visibleRowIndex, _lastSelectedVisibleRowIndex.Value); j <= System.Math.Max(visibleRowIndex, _lastSelectedVisibleRowIndex.Value); j++)
                                        {
                                            _selectedRowIndices.Add(j);
                                        }
                                    }
                                }
                                else if (ImGui.GetIO().KeyCtrl)
                                {
                                    if (isSelected)
                                    {
                                        _selectedRowIndices.Remove(visibleRowIndex);
                                    }
                                    else
                                    {
                                        _selectedRowIndices.Add(visibleRowIndex);
                                    }

                                    _lastSelectedVisibleRowIndex = visibleRowIndex;
                                }
                                else
                                {
                                    _selectedRowIndices.Clear();
                                    _selectedRowIndices.Add(visibleRowIndex);
                                    _lastSelectedVisibleRowIndex = visibleRowIndex;
                                }
                            }

                            int columnCount = 0;
                            var addColumnData = (Func<TraceRecord, string> getDisplayText) =>
                            {
                                if (columnCount == 0)
                                {
                                    // The first column is already started with a special whole-row selectable.
                                    ImGui.SameLine();
                                }
                                else
                                {
                                    ImGui.TableNextColumn();
                                }

                                columnCount++;
                                string displayText = getDisplayText(traceRecords[i]);
                                ImGui.TextUnformatted(displayText);

                                if (ImGui.BeginPopupContextItem($"tableViewPopup{columnCount}"))
                                {
                                    string displayTextTruncated = displayText.Length > 48 ? displayText.Substring(0, 48) + "..." : displayText;

                                    setColor(new Vector4(1, 1, 1, 1)); // TODO: Get color from theme

                                    if (ImGui.MenuItem($"Highlight '{displayTextTruncated}'"))
                                    {
                                        _viewerRules.HighlightRules.Add(new TraceRecordHighlightRule(
                                            Rule: new TraceRecordRule { IsMatch = record => getDisplayText(record) == displayText },
                                            Color: new Vector4(1.0f, 1.0f, 0.0f, 1.0f)));
                                        _visibleRowsGenerationId = -1;
                                    }
                                    if (ImGui.MenuItem($"Include '{displayTextTruncated}'"))
                                    {
                                        _viewerRules.VisibleRules.Add(new TraceRecordVisibleRule(
                                            Rule: new TraceRecordRule { IsMatch = record => getDisplayText(record) == displayText },
                                            Action: TraceRecordRuleAction.Include));
                                        _visibleRowsGenerationId = -1;
                                    }
                                    if (ImGui.MenuItem($"Exclude '{displayTextTruncated}'"))
                                    {
                                        _viewerRules.VisibleRules.Add(new TraceRecordVisibleRule(
                                            Rule: new TraceRecordRule { IsMatch = record => getDisplayText(record) == displayText },
                                            Action: TraceRecordRuleAction.Exclude));
                                        _visibleRowsGenerationId = -1;
                                    }
                                    ImGui.Separator();
                                    if (ImGui.MenuItem($"Copy '{displayTextTruncated}'"))
                                    {
                                        ImGui.SetClipboardText(displayText);
                                    }
                                    ImGui.EndPopup();

                                    // Resume co r for remainder of row.
                                    setColor(LevelToColor(traceRecords[i].Level));
                                }
                            };

                            addColumnData(r => _traceSource.TraceSource.GetProcessName(r.ProcessId));
                            addColumnData(r => _traceSource.TraceSource.GetThreadName(r.ThreadId));
                            addColumnData(r => r.ProviderName);
                            addColumnData(r => _traceSource.TraceSource.GetOpCodeName(r.OpCode));
                            addColumnData(r => r.Level.ToString());
                            addColumnData(r => r.Name);
                            addColumnData(r => r.Timestamp.ToString("HH:mm:ss.ffffff"));
                            addColumnData(r => r.Message.Replace("\n", " ").Replace("\r", " "));

                            ImGui.PopID(); // Row index
                        }
                    }
                    clipper.End();
                });

                setColor(null);

                ImGui.PopStyleVar(); // CellPadding

                if (setScrollIndex.HasValue)
                {
                    ImGui.SetScrollY(setScrollIndex.Value / (float)recordCount * ImGui.GetScrollMaxY());
                }
                // ImGui has a bug with large scroll areas where you can't quite reach the MaxY with the scrollbar (e.g. ScrollY is 103545660 and ScrollMaxY is 103545670).
                // So we use a percentage instead.
                else if (ImGui.GetScrollMaxY() > 0 && ImGui.GetScrollY() / (float)ImGui.GetScrollMaxY() > 0.999f)
                {
                    // Auto scroll - AKA keep the table scrolled to the bottom as new messages come in, but only if the table is already
                    // scrolled to the bottom.
                    ImGui.SetScrollHereY(1.0f);
                }

                ImGui.EndTable();
            }

        }

        private void DrawToolStrip(IUiCommands uiCommands, ref int? setScrollIndex)
        {
            ImGui.BeginDisabled(!_selectedRowIndices.Any());
            if (ImGui.Button("Copy rows") || ImGui.IsKeyChordPressed(ImGuiKey.C | ImGuiKey.ModCtrl))
            {
                CopySelectedRows();
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                _traceSource.TraceSource.Clear();
                _lastSelectedVisibleRowIndex = null;
                _selectedRowIndices.Clear();
            }

            ImGui.SameLine();
            if (ImGui.Button("Clone"))
            {
                uiCommands.AddLogViewerWindow(Clone());
            }

            bool findRequested = false;
            ImGui.SameLine();
            ImGui.PushItemWidth(300);
            if (ImGui.Shortcut(ImGuiKey.F | ImGuiKey.ModCtrl))
            {
                ImGui.SetKeyboardFocusHere();
            }
            if (ImGui.InputTextWithHint("", "Find...", ref _findBuffer, 1024, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                // Focus goes somewhere else when enter is pressed but we want to keep focus so the user can keep pressing enter to go to the next match.
                ImGui.SetKeyboardFocusHere(-1);
                findRequested = true;
            }
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (ImGui.ArrowButton("Find", _findFoward ? ImGuiDir.Right : ImGuiDir.Left))
            {
                findRequested = true;
            }

            _traceSource.TraceSource.ReadUnderLock((generationId, traceRecords) =>
            {
                // Must ensure visible rows are up to date in order to avoid race conditions when comparing counts.
                UpdateVisibleRows(generationId, traceRecords);

                ImGui.SameLine();
                ImGui.Text($"{_visibleRows.Count:N0} rows");
                if (_visibleRows.Count != traceRecords.Count)
                {
                    ImGui.SameLine();
                    ImGui.Text($"({traceRecords.Count - _visibleRows.Count:N0} excluded)");
                }

                if (_errorCount > 0)
                {
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, LevelToColor(TraceLevel.Error));
                    ImGui.TextUnformatted($"{_errorCount:N0} Errors");
                    ImGui.PopStyleColor();
                }
            });

            if (!string.IsNullOrEmpty(_findBuffer))
            {
                if (ImGui.IsKeyPressed(ImGuiKey.F3) && ImGui.IsKeyDown(ImGuiKey.ModShift))
                {
                    _findFoward = false;
                    findRequested = true;
                }
                else if (ImGui.IsKeyPressed(ImGuiKey.F3))
                {
                    _findFoward = true;
                    findRequested = true;
                }

                if (findRequested)
                {
                    setScrollIndex = FindText(_findBuffer);
                }
            }
        }

        private LogViewerWindow Clone()
        {
            LogViewerWindow newWindow = new(_traceSource);
            newWindow._findBuffer = _findBuffer;
            newWindow._viewerRules = _viewerRules.Clone();
            return newWindow;
        }

        private static Vector4 LevelToColor(TraceLevel level)
        {
            return level == TraceLevel.Verbose ? new Vector4(0.75f, 0.75f, 0.75f, 1.0f)     // Gray
                   : level == TraceLevel.Warning ? new Vector4(1.0f, 0.65f, 0.0f, 1.0f)     // Orange
                   : level == TraceLevel.Error ? new Vector4(0.75f, 0.0f, 0.0f, 1.0f)       // Red
                   : level == TraceLevel.Critical ? new Vector4(0.60f, 0.0f, 0.0f, 1.0f)    // Dark Red
                                                   : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);   // White
        }

        private void CopySelectedRows()
        {
            StringBuilder copyText = new();

            _traceSource.TraceSource.ReadUnderLock((generationId,  traceRecords) =>
            {
                foreach (var selectedRowIndex in _selectedRowIndices.OrderBy(i => i))
                {
                    int i = _visibleRows[selectedRowIndex];
                    copyText.Append($"{traceRecords[i].Timestamp:HH:mm:ss.ffffff}\t{traceRecords[i].ProcessId}\t{traceRecords[i].ThreadId}\t{_traceSource.TraceSource.GetOpCodeName(traceRecords[i].OpCode)}\t{traceRecords[i].Level}\t{traceRecords[i].ProviderName}\t{traceRecords[i].Name}\t{traceRecords[i].Message}\n");
                }
            });

            ImGui.SetClipboardText(copyText.ToString());
        }

        private int? FindText(string text)
        {
            int? setScrollIndex = null;
            _traceSource.TraceSource.ReadUnderLock((generationId, traceRecords) =>
            {
                int visibleRowIndex =
                    _findFoward ?
                       (_lastSelectedVisibleRowIndex.HasValue ? _lastSelectedVisibleRowIndex.Value + 1 : 0) :
                       (_lastSelectedVisibleRowIndex.HasValue ? _lastSelectedVisibleRowIndex.Value - 1 : traceRecords.Count - 1);
                while (visibleRowIndex >= 0 && visibleRowIndex < _visibleRows.Count)
                {
                    int i = _visibleRows[visibleRowIndex];

                    if (traceRecords[i].Message.Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase) ||
                        traceRecords[i].Name.Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase) ||
                        _traceSource.TraceSource.GetProcessName(traceRecords[i].ProcessId).Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase) ||
                        _traceSource.TraceSource.GetThreadName(traceRecords[i].ThreadId).Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase))
                    {
                        setScrollIndex = visibleRowIndex;
                        _lastSelectedVisibleRowIndex = visibleRowIndex;
                        _selectedRowIndices.Clear();
                        _selectedRowIndices.Add(visibleRowIndex);
                        break;
                    }

                    visibleRowIndex = (_findFoward ? visibleRowIndex + 1 : visibleRowIndex - 1);
                }
            });
            return setScrollIndex;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _traceSource.ReleaseRef(this);
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
