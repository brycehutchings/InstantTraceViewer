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

        private ViewerRules _viewerRules = new();
        private FilteredTraceRecordCollection _filteredTraceRecords = new();

        private HashSet<int> _selectedTraceRecordIds = new HashSet<int>();
        private int? _lastSelectedVisibleRowIndex;
        private int? _topmostVisibleTraceRecordId;

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

        public void DrawWindow(IUiCommands uiCommands)
        {
            if (IsClosed)
            {
                return;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _selectedTraceRecordIds.Clear();
                _lastSelectedVisibleRowIndex = null;
            }

            // TODO: _filteredTraceRecords keeps a reference to traceRecords outside of the lock. This is actually safe
            // as long as no one else calls ReadUnderLock since this is the only time the collection is updated. Can I do better?
            bool filteredViewRebuilt = false;
            _traceSource.TraceSource.ReadUnderLock((int generationId, IReadOnlyList<TraceRecord> traceRecords) =>
            {
                filteredViewRebuilt = _filteredTraceRecords.Update(_viewerRules, generationId, traceRecords);
            });

            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);

            bool opened = true;
            if (ImGui.Begin($"{_traceSource.TraceSource.DisplayName}###LogViewerWindow_{_windowId}", ref opened))
            {
                DrawWindowContents(uiCommands, _filteredTraceRecords, filteredViewRebuilt);
            }

            ImGui.End();

            if (!opened)
            {
                Dispose();
            }
        }

        private unsafe void DrawWindowContents(IUiCommands uiCommands, FilteredTraceRecordCollection visibleTraceRecords, bool filteredViewRebuilt)
        {
            int? setScrollIndex = null;             // Row index to scroll to (it will be the topmost row that is visible).

            DrawToolStrip(uiCommands, visibleTraceRecords, ref setScrollIndex);

            // If we are scrolling to show a line (like for CTRL+F), position the line ~1/3 of the way down.
            // TODO: ImGui.GetTextLineHeightWithSpacing() is the correct number, but is it technically the right thing to rely on?
            Vector2 remainingRegion = ImGui.GetContentRegionAvail();
            int setScrollIncludePriorRowCount = (int)((remainingRegion.Y * 0.3f) / ImGui.GetTextLineHeightWithSpacing());

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

                // Maintain scroll position when the view was rebuilt. The scroll will happen after the table is drawn so that the row height can be used to calculate the scroll offset.
                if (filteredViewRebuilt && _topmostVisibleTraceRecordId.HasValue)
                {
                    Debug.WriteLine("Trying to maintain scroll position...");

                    // TODO: This could be a binary search.
                    for (int i = 0; i < visibleTraceRecords.Count; i++)
                    {
                        if (visibleTraceRecords.GetRecordId(i) >= _topmostVisibleTraceRecordId)
                        {
                            Debug.WriteLine($"Scrolling to index {i} / {visibleTraceRecords.Count}");
                            setScrollIndex = i;
                            setScrollIncludePriorRowCount = 0; // We don't want things to move around when filtering changes.
                            break;
                        }
                    }
                }

                _topmostVisibleTraceRecordId = null;

                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(visibleTraceRecords.Count);
                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        // Don't bother to scroll to the selected index if it's already in view.
                        if (i == setScrollIndex)
                        {
                            setScrollIndex = null;
                        }

                        TraceRecord traceRecord = visibleTraceRecords[i];
                        int traceRecordId = visibleTraceRecords.GetRecordId(i);

                        // ImGuiListClipper always does row 0 to calculate row height, so this must be ignored.
                        if (_topmostVisibleTraceRecordId == null && i != 0)
                        {
                            _topmostVisibleTraceRecordId = traceRecordId;
                        }

                        ImGui.PushID(traceRecordId);

                        ImGui.TableNextRow();

                        setColor(LevelToColor(traceRecord.Level));

                        ImGui.TableNextColumn();

                        // Create an empty selectable that spans the full row to enable row selection.
                        bool isSelected = _selectedTraceRecordIds.Contains(traceRecordId);
                        if (ImGui.Selectable($"##TableRow", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                        {
                            if (ImGui.GetIO().KeyShift)
                            {
                                if (_lastSelectedVisibleRowIndex.HasValue)
                                {
                                    _selectedTraceRecordIds.Clear();
                                    for (int j = System.Math.Min(i, _lastSelectedVisibleRowIndex.Value); j <= System.Math.Max(i, _lastSelectedVisibleRowIndex.Value); j++)
                                    {
                                        _selectedTraceRecordIds.Add(visibleTraceRecords.GetRecordId(j));
                                    }
                                }
                            }
                            else if (ImGui.GetIO().KeyCtrl)
                            {
                                if (isSelected)
                                {
                                    _selectedTraceRecordIds.Remove(traceRecordId);
                                }
                                else
                                {
                                    _selectedTraceRecordIds.Add(traceRecordId);
                                }

                                _lastSelectedVisibleRowIndex = i;
                            }
                            else
                            {
                                _selectedTraceRecordIds.Clear();
                                _selectedTraceRecordIds.Add(traceRecordId);
                                _lastSelectedVisibleRowIndex = i;
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
                            string displayText = getDisplayText(traceRecord);
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
                                    _viewerRules.GenerationId++;
                                }
                                if (ImGui.MenuItem($"Include '{displayTextTruncated}'"))
                                {
                                    _viewerRules.VisibleRules.Add(new TraceRecordVisibleRule(
                                        Rule: new TraceRecordRule { IsMatch = record => getDisplayText(record) == displayText },
                                        Action: TraceRecordRuleAction.Include));
                                    _viewerRules.GenerationId++;
                                }
                                if (ImGui.MenuItem($"Exclude '{displayTextTruncated}'"))
                                {
                                    _viewerRules.VisibleRules.Add(new TraceRecordVisibleRule(
                                        Rule: new TraceRecordRule { IsMatch = record => getDisplayText(record) == displayText },
                                        Action: TraceRecordRuleAction.Exclude));
                                    _viewerRules.GenerationId++;
                                }
                                ImGui.Separator();
                                if (ImGui.MenuItem($"Copy '{displayTextTruncated}'"))
                                {
                                    ImGui.SetClipboardText(displayText);
                                }
                                ImGui.EndPopup();

                                // Resume co r for remainder of row.
                                setColor(LevelToColor(traceRecord.Level));
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

                        ImGui.PopID(); // Trace record id
                    }
                }
                clipper.End();

                setColor(null);

                ImGui.PopStyleVar(); // CellPadding

                Vector2 size = ImGui.GetItemRectSize();

                if (setScrollIndex.HasValue)
                {
                    // If we're trying to precisely maintain the content after the count has changed, we need to account for the partial row scroll.
                    float partialRowScroll = setScrollIncludePriorRowCount == 0 ? ((int)ImGui.GetScrollY() % (int)clipper.ItemsHeight) : 0;
                    ImGui.SetScrollY((setScrollIndex.Value - setScrollIncludePriorRowCount) * clipper.ItemsHeight + partialRowScroll);
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

        private void DrawToolStrip(IUiCommands uiCommands, FilteredTraceRecordCollection visibleTraceRecords, ref int? setScrollIndex)
        {
            ImGui.BeginDisabled(!_selectedTraceRecordIds.Any());
            if (ImGui.Button("Copy rows") || ImGui.IsKeyChordPressed(ImGuiKey.C | ImGuiKey.ModCtrl))
            {
                CopySelectedRows(visibleTraceRecords);
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                _traceSource.TraceSource.Clear();
                _lastSelectedVisibleRowIndex = null;
                _selectedTraceRecordIds.Clear();
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(!_viewerRules.VisibleRules.Any());
            string clearFilterSuffix = _viewerRules.VisibleRules.Any() ? $" ({_viewerRules.VisibleRules.Count()})" : string.Empty;
            if (ImGui.Button($"Clear filters" + clearFilterSuffix))
            {
                _viewerRules.VisibleRules.Clear();
                _viewerRules.GenerationId++;
            }
            ImGui.EndDisabled();

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

            ImGui.SameLine();
            ImGui.Text($"{visibleTraceRecords.Count:N0} rows");
            if (visibleTraceRecords.Count != visibleTraceRecords.UnfilteredCount)
            {
                ImGui.SameLine();
                ImGui.Text($"({visibleTraceRecords.UnfilteredCount - visibleTraceRecords.Count:N0} excluded)");
            }

            if (visibleTraceRecords.ErrorCount > 0)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, LevelToColor(TraceLevel.Error));
                ImGui.TextUnformatted($"{visibleTraceRecords.ErrorCount:N0} Errors");
                ImGui.PopStyleColor();
            }

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
                    setScrollIndex = FindText(visibleTraceRecords, _findBuffer);
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

        private void CopySelectedRows(FilteredTraceRecordCollection visibleTraceRecords)
        {
            StringBuilder copyText = new();

            foreach (var selectedRecordId in _selectedTraceRecordIds.OrderBy(i => i))
            {
                TraceRecord record = visibleTraceRecords.GetRecordFromId(selectedRecordId);
                copyText.Append($"{record.Timestamp:HH:mm:ss.ffffff}\t{record.ProcessId}\t{record.ThreadId}\t{_traceSource.TraceSource.GetOpCodeName(record.OpCode)}\t{record.Level}\t{record.ProviderName}\t{record.Name}\t{record.Message}\n");
            }

            ImGui.SetClipboardText(copyText.ToString());
        }

        private int? FindText(FilteredTraceRecordCollection visibleTraceRecords, string text)
        {
            int? setScrollIndex = null;
            int visibleRowIndex =
                _findFoward ?
                   (_lastSelectedVisibleRowIndex.HasValue ? _lastSelectedVisibleRowIndex.Value + 1 : 0) :
                   (_lastSelectedVisibleRowIndex.HasValue ? _lastSelectedVisibleRowIndex.Value - 1 : visibleTraceRecords.Count - 1);
            while (visibleRowIndex >= 0 && visibleRowIndex < visibleTraceRecords.Count)
            {
                TraceRecord traceRecord = visibleTraceRecords[visibleRowIndex];

                if (traceRecord.Message.Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase) ||
                    traceRecord.Name.Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase) ||
                    _traceSource.TraceSource.GetProcessName(traceRecord.ProcessId).Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase) ||
                    _traceSource.TraceSource.GetThreadName(traceRecord.ThreadId).Contains(_findBuffer, StringComparison.InvariantCultureIgnoreCase))
                {
                    setScrollIndex = visibleRowIndex;
                    _lastSelectedVisibleRowIndex = visibleRowIndex;
                    _selectedTraceRecordIds.Clear();
                    _selectedTraceRecordIds.Add(visibleTraceRecords.GetRecordId(visibleRowIndex));
                    break;
                }

                visibleRowIndex = (_findFoward ? visibleRowIndex + 1 : visibleRowIndex - 1);
            }
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
