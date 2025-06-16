/*
 * --- FUTURE WORK/IDEAS
 * 0. Highlight thread row (or process row if collapsed) of last selected event to make it easier to find.
 * 1. Inline thread timeline in log viewer with only pinned threads. Context menu item for thread cell to pin.
 * 2. Click-drag to select time range and show duration. Allow zoom to it.
 * 3. Option to aggregate by Provider instead of Pid/Tid?
 */
using ImGuiNET;
using System;
using System.Numerics;
using InstantTraceViewer;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO.Hashing;

namespace InstantTraceViewerUI
{
    internal class ThreadTimelineWindow
    {
        private const string PopupName = "Thread Timeline";

        private record struct PidTidKey(int Pid, int Tid);

        private readonly string _name;
        private readonly string _parentWindowId;

        private bool _open = true;

        struct Bar
        {
            public DateTime Start;
            public DateTime Stop;
            public int Depth;
            public string Name;
            public uint Color;

            public TimeSpan Duration => Stop - Start;

            // Some of the information in this struct like Level, Depth and Color could be determined by looking up the event in the table with this row index
            // but it would be slower so we cache the information that is needed for per-frame rendering above and use this for tooltips and other things that are not per-frame.
            // This points to the Start event (not the Stop event).
            public int VisibleRowIndex;
        }

        public struct InstantEvent
        {
            public DateTime Timestamp;
            public int Depth;
            public UnifiedLevel Level;
            public uint Color;

            // Some of the information in this struct like Level, Depth and Color could be determined by looking up the event in the table with this row index
            // but it would be slower so we cache the information that is needed for per-frame rendering above and use this for tooltips and other things that are not per-frame.
            public int VisibleRowIndex;
        }

        class Track
        {
            //
            // Processing State:
            //
            public struct StartEvent
            {
                public DateTime Timestamp;
                public string Name;
                public int VisibleRowIndex; // For start event.
            }

            public Stack<StartEvent> StartEvents = new Stack<StartEvent>();

            //
            // Post-processing Output:
            //
            public string? ProcessName;
            public string? ThreadName;
            public List<Bar> Bars = new();
            public List<InstantEvent> InstantEvents = new();
        }

        struct ComputedTrack
        {
            public int ProcessId;
            public string ProcessName;
            public int ThreadId;
            public string ThreadName;

            public int MaxBarDepth;
            public List<Bar> Bars;

            public int MaxInstantEventDepth;
            public List<InstantEvent> InstantEvents;

            public string UniqueKey => $"{UniqueProcessKey}_{ThreadId}_{ThreadName}";
            public string UniqueProcessKey => $"{ProcessId}_{ProcessName}";
        }

        class ComputedTracks
        {
            // Tracks are ordered so that all tracks for a given process are continguous. The renderer depends on this because it creates a new group whenever the process name changes.
            public IReadOnlyList<ComputedTrack> Tracks;

            // Snapshot of the trace table used to compute the Tracks.
            public ITraceTableSnapshot TraceTableSnapshot;

            public DateTime StartTraceTable => TraceTableSnapshot.GetTimestamp(0);

            public DateTime EndTraceTable => TraceTableSnapshot.GetTimestamp(TraceTableSnapshot.RowCount - 1);
        }

        private Task<ComputedTracks> _computedTracksTask = null;
        private ComputedTracks _latestComputedTracks;
        private List<string> _pinnedTracks = new(); // Value is ComputedTrack UniqueKey.
        private DateTime _startZoomRange = DateTime.MinValue;
        private DateTime _endZoomRange = DateTime.MaxValue;
        private bool _isMouseHoveringTable = false;

        // Special state needed to manage rare case for panning at extreme zoom levels due to TimeSpan/DateTime coarsensss.
        float _accumulatedPanAmountPixels = 0;

        private float? _trackAreaLeftScreenPos;
        private float? _trackAreaWidth;

        public ThreadTimelineWindow(string name, string parentWindowId)
        {
            _name = name;
            _parentWindowId = parentWindowId;
        }

        // This is reset every frame and only set if a click happens on an event.
        public int? ClickedVisibleRowIndex { get; set; }

        public bool DrawWindow(IUiCommands uiCommands, ITraceTableSnapshot traceTable, int? lastSelectedVisibleRowIndex, ViewerRules viewerRules, DateTime? startWindow, DateTime? endWindow)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(600, 200), new Vector2(float.MaxValue, float.MaxValue));

            // There is no input control over the track column of the table (it is all drawn manually), so when the user
            // uses SHIFT+MouseMove to pan, we need to disable the window moving because ImGui allows windows to move by
            // click+dragging anywhere there is no input control.
            ImGuiWindowFlags flags = _isMouseHoveringTable ? ImGuiWindowFlags.NoMove : ImGuiWindowFlags.None;
            if (ImGui.Begin($"{PopupName} - {_name}###ThreadTimeline_{_parentWindowId}", ref _open, flags))
            {
                DrawTimelineGraph(traceTable, lastSelectedVisibleRowIndex, viewerRules, startWindow, endWindow);
            }

            ImGui.End();

            return _open;
        }

        public static bool IsSupported(TraceTableSchema schema)
            => schema.TimestampColumn != null && schema.NameColumn != null && schema.ProcessIdColumn != null && schema.ThreadIdColumn != null;

        private void DrawTimelineGraph(ITraceTableSnapshot traceTable, int? lastSelectedVisibleRowIndex, ViewerRules viewerRules, DateTime? startWindow, DateTime? endWindow)
        {
            Trace.Assert(IsSupported(traceTable.Schema));

            bool latestComputedTracksOutOfDate = _latestComputedTracks != null &&
                (_latestComputedTracks.TraceTableSnapshot.GenerationId != traceTable.GenerationId ||
                  _latestComputedTracks.TraceTableSnapshot.RowCount != traceTable.RowCount);

            bool? expandCollapse = null; // true=expand, false=collapse, null=no change
            if (ImGui.Button("\uF31E Expand All"))
            {
                expandCollapse = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("\uF78C Collapse All"))
            {
                expandCollapse = true;
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(_startZoomRange == DateTime.MinValue && _endZoomRange == DateTime.MaxValue); // No zoom
            if (ImGui.Button("\uF0B0 Filter to zoom"))
            {
                var startZoomTimeStr = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(_startZoomRange);
                var endZoomTimeStr = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(_endZoomRange);
                viewerRules.AddRule(
                    $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(traceTable.Schema.TimestampColumn)} {TraceTableRowSelectorSyntax.LessThanOperatorName} {startZoomTimeStr}"
                  + $" {TraceTableRowSelectorSyntax.OrOperatorName} "
                  + $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(traceTable.Schema.TimestampColumn)} {TraceTableRowSelectorSyntax.GreaterThanOperatorName} {endZoomTimeStr}",
                    TraceRowRuleAction.Exclude);
            }
            ImGui.SetItemTooltip("Adds an exclude rule to filter out events that come before or after the current zoomed in time range.");
            ImGui.SameLine();
            if (ImGui.Button("\uF010 Zoom out"))
            {
                _startZoomRange = DateTime.MinValue;
                _endZoomRange = DateTime.MaxValue;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!startWindow.HasValue || !endWindow.HasValue);
            if (ImGui.Button("\uF0CE Zoom to table view"))
            {
                _startZoomRange = startWindow.Value;
                _endZoomRange = endWindow.Value;
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGuiWidgets.HelpIconToolip(
                "How to navigate with the mouse:\n\n" +
                "CTRL + Scroll Wheel --- Zoom in and out centered on mouse cursor\n" +
                "SHIFT + Click + Drag --- Pan left/right\n" +
                "CTRL + Click --- Jump to hovered event");

            // Re-compute tracks if the trace table has changed since the last computation, or if there is no cached result yet.
            if ((_latestComputedTracks == null || latestComputedTracksOutOfDate) && _computedTracksTask == null)
            {
                _computedTracksTask = Task.Run(() => ComputeTracks(traceTable));
            }

            if (_computedTracksTask?.IsCompletedSuccessfully ?? false)
            {
                _latestComputedTracks = _computedTracksTask.Result;
                _computedTracksTask = null;
            }
            else if (_computedTracksTask?.IsFaulted ?? false)
            {
                ImGui.TextUnformatted($"Error computing tracks: {_computedTracksTask.Exception?.InnerExceptions.First().Message}");
            }

            if (_latestComputedTracks != null)
            {
                DrawTrackGraph(lastSelectedVisibleRowIndex, startWindow, endWindow, expandCollapse);
            }
            else
            {
                // This is only shown the first time when _latestComputedTracks has no cached result to display.
                ImGui.TextUnformatted("Processing tracks...");
            }
        }

        private void DrawTrackGraph(int? lastSelectedVisibleRowIndex, DateTime? startWindow, DateTime? endWindow, bool? expandCollapse)
        {
            ClickedVisibleRowIndex = null;

            if (_latestComputedTracks.TraceTableSnapshot.RowCount == 0)
            {
                // This also protects code below which assumes there is at least one row.
                ImGui.TextUnformatted("No events to display.");
                return;
            }

            ApplyZoomPanAndClamp();

            TimeSpan zoomDuration = _startZoomRange - _endZoomRange;

            // These will be set as we draw the table which is when this information is available.
            _trackAreaLeftScreenPos = null;
            _trackAreaWidth = null;

            // 'ScrollY' required for freezing rows (for pinning/timeline header).
            if (ImGui.BeginTable("ScopesTable", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY))
            {
                var pinnedTracksSnapshot = _pinnedTracks.ToList(); // Copy the list so we can modify it while iterating.

                // Freeze pinned tracks so they are always visible. Plus one because we also want to pin the timeline. Plus one more if we have pinned tracks because that is the separator row.
                ImGui.TableSetupScrollFreeze(0, pinnedTracksSnapshot.Count + 1 + (pinnedTracksSnapshot.Any() ? 1 : 0));

                float dpiBase = ImGui.GetFontSize();
                ImGui.TableSetupColumn("Thread", ImGuiTableColumnFlags.WidthFixed, 10.0f * dpiBase);
                ImGui.TableSetupColumn("Track", ImGuiTableColumnFlags.WidthStretch, 1.0f);

                // ImGui.TableHeadersRow(); // We don't actually want the header shown

                DrawTimeline(_startZoomRange, _endZoomRange, startWindow, endWindow, lastSelectedVisibleRowIndex);

                List<object> hoveredEvents = new(); // Contains 0 or more 'Bar' and 'InstantEvent' objects.
                foreach (var trackKey in pinnedTracksSnapshot)
                {
                    var track = _latestComputedTracks.Tracks.FirstOrDefault(t => t.UniqueKey == trackKey);
                    DrawTrack(track, _startZoomRange, _endZoomRange, isPinned: true, hoveredEvents);
                }

                if (pinnedTracksSnapshot.Any())
                {
                    // Blank row to separate pinned tracks from the rest. Fill color matches border color so it looks like a solid thick separator.
                    // If this is removed, the ScrollFreeze math needs to change too.
                    ImGui.TableNextRow();
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableBorderLight));
                    ImGui.TableNextColumn(); // Track name
                    ImGui.TableNextColumn(); // Track graph area
                }

                string? previousProcessKey = null;
                bool isOpen = false;
                foreach (var track in _latestComputedTracks.Tracks)
                {
                    bool isPinned = pinnedTracksSnapshot.Contains(track.UniqueKey);
                    if (isPinned)
                    {
                        continue; // Pinned tracks were already rendered.
                    }

                    if (previousProcessKey != track.UniqueProcessKey)
                    {
                        if (isOpen)
                        {
                            ImGui.TreePop();
                        }

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();

                        if (expandCollapse.HasValue)
                        {
                            ImGui.SetNextItemOpen(!expandCollapse.Value, ImGuiCond.Always);
                        }

                        string processDescription = string.IsNullOrEmpty(track.ProcessName) ? $"{track.ProcessId}" : $"{track.ProcessId} ({track.ProcessName})";
                        isOpen = ImGui.TreeNodeEx(processDescription, ImGuiTreeNodeFlags.SpanFullWidth);
                        ImGui.TableNextColumn();

                        previousProcessKey = track.UniqueProcessKey;
                    }

                    if (!isOpen)
                    {
                        continue;
                    }

                    DrawTrack(track, _startZoomRange, _endZoomRange, isPinned, hoveredEvents);
                }

                if (isOpen)
                {
                    ImGui.TreePop();
                }

                ImGui.EndTable();

                _isMouseHoveringTable = ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

                if (hoveredEvents.Count > 0)
                {
                    bool clickable = false;
                    if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
                    {
                        clickable = true;
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    }

                    if (ImGui.BeginTooltip())
                    {
                        DrawHoveredEventTooltipContent(hoveredEvents, clickable);
                        ImGui.EndTooltip();
                    }
                }
            }

            // If the zoomed region begins and/or ends with the log range, then reset the zoom so that it is "sticky" when new data comes in.
            // ApplyZoomPanAndClamp will handle santizing this on the next frame and make it sticky (i.e. endZoomRange will lock onto a new endTraceTable)
            if (_endZoomRange == _latestComputedTracks.EndTraceTable)
            {
                _endZoomRange = DateTime.MaxValue;
            }
            if (_startZoomRange == _latestComputedTracks.StartTraceTable)
            {
                _startZoomRange = DateTime.MinValue;
            }
        }

        private void DrawHoveredEventTooltipContent(List<object> hoveredEvents, bool clickable)
        {
            if (hoveredEvents.Count > 1)
            {
                var eventNames = hoveredEvents.Select(e => e switch
                {
                    Bar bar => _latestComputedTracks.TraceTableSnapshot.GetName(bar.VisibleRowIndex),
                    InstantEvent instantEvent => _latestComputedTracks.TraceTableSnapshot.GetName(instantEvent.VisibleRowIndex),
                    _ => throw new InvalidOperationException("Unexpected event type.")
                });

                // Order by count ascending because we want the least common events (more likely to be interesting) to be shown at the top.
                ImGui.SeparatorText("Multiple events");
                var groupedEvents = eventNames.GroupBy(n => n).Where(g => !string.IsNullOrEmpty(g.Key)).OrderBy(n => n.Count()).ToList();
                foreach (var group in groupedEvents)
                {
                    ImGui.Text($"{group.Key} ({group.Count()})");
                }

                if (clickable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    ClickedVisibleRowIndex = hoveredEvents.First() switch
                    {
                        Bar bar => bar.VisibleRowIndex,
                        InstantEvent instantEvent => instantEvent.VisibleRowIndex,
                        _ => throw new InvalidOperationException("Unexpected event type.")
                    };
                }
            }
            else if (hoveredEvents.SingleOrDefault() is Bar bar)
            {
                if (clickable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    ClickedVisibleRowIndex = bar.VisibleRowIndex;
                }

                DrawEventDetails(_latestComputedTracks.TraceTableSnapshot, bar.VisibleRowIndex, bar.Duration);
            }
            else if (hoveredEvents.SingleOrDefault() is InstantEvent instantEvent)
            {
                if (clickable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    ClickedVisibleRowIndex = instantEvent.VisibleRowIndex;
                }

                DrawEventDetails(_latestComputedTracks.TraceTableSnapshot, instantEvent.VisibleRowIndex);
            }
        }

        private static void DrawEventDetails(ITraceTableSnapshot traceTable, int visibleRowIndex, TimeSpan? duration = null)
        {
            Action<TraceSourceSchemaColumn> renderColumn = (TraceSourceSchemaColumn column) =>
            {
                string value = traceTable.GetColumnValueString(visibleRowIndex, column, allowMultiline: true);
                if (!string.IsNullOrEmpty(value))
                {
                    if (value.Contains('\n'))
                    {
                        ImGui.SeparatorText(column.Name);
                        ImGui.TextUnformatted(value);
                    }
                    else
                    {
                        ImGui.TextUnformatted($"{column.Name}: {value}");
                    }
                }
            };

            // Name column is most important and should go first, regardless of the order used for the log viewer table.
            renderColumn(traceTable.Schema.NameColumn);

            if (duration.HasValue)
            {
                ImGui.Text($"Duration: {FriendlyStringify.ToString(duration.Value)}");
            }

            foreach (var col in traceTable.Schema.Columns)
            {
                if (col == traceTable.Schema.ProcessIdColumn || col == traceTable.Schema.ThreadIdColumn || col == traceTable.Schema.NameColumn)
                {
                    continue; // These are already displayed in the track graphically or prioritized first.
                }

                renderColumn(col);
            }
        }

        private void DrawTimeline(DateTime startRange, DateTime endRange, DateTime? startWindow, DateTime? endWindow, int? lastSelectedVisibleRowIndex)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); // Track name (none for timeline)
            ImGui.TableNextColumn(); // Track graph area

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();

            float trackRegionAvailableWidth = ImGui.GetContentRegionAvail().X;
            Vector2 timelineTopLeft = ImGui.GetCursorScreenPos();

            // We need to remember the starting X and width to do zooming centered on the mouse X coord.
            if (_trackAreaLeftScreenPos == null && _trackAreaWidth == null)
            {
                _trackAreaLeftScreenPos = timelineTopLeft.X;
                _trackAreaWidth = trackRegionAvailableWidth;
            }

            TimeSpan rangeDuration = endRange - startRange;
            float tickToPixel = trackRegionAvailableWidth / rangeDuration.Ticks;

            // Color background region that is visible in the log viewer.
            if (startWindow.HasValue && endWindow.HasValue && endWindow.Value >= startRange && startWindow.Value <= endRange)
            {
                long startRelativeTicks = startWindow.Value.Ticks - startRange.Ticks;
                long stopRelativeTicks = endWindow.Value.Ticks - startRange.Ticks;

                // Truncate the bar so it doesn't draw outside the range.
                startRelativeTicks = Math.Max(startRelativeTicks, 0);
                stopRelativeTicks = Math.Min(stopRelativeTicks, endRange.Ticks - startRange.Ticks);

                Vector2 min = new Vector2((float)Math.Floor(startRelativeTicks * tickToPixel), 0) + timelineTopLeft;
                Vector2 max = new Vector2((float)Math.Ceiling(stopRelativeTicks * tickToPixel), float.MaxValue) + timelineTopLeft;
                drawList.AddRectFilled(min, max, AppTheme.ThreadTimelineLogViewRegionColor);
            }

            // We want the vertical line for the selected row to be draw over the visible region (previous block of code) but under all of the track bars/ticks.
            if (lastSelectedVisibleRowIndex.HasValue && _trackAreaWidth.HasValue && _trackAreaLeftScreenPos.HasValue)
            {
                DateTime timestamp = _latestComputedTracks.TraceTableSnapshot.GetTimestamp(lastSelectedVisibleRowIndex.Value);
                if (timestamp >= _startZoomRange && timestamp <= _endZoomRange)
                {
                    // Give the vertical line the same color as the bar/tick.
                    uint color = GenerateColorFromName(_latestComputedTracks.TraceTableSnapshot.GetName(lastSelectedVisibleRowIndex.Value));

                    float xPos = (float)Math.Round((timestamp.Ticks - _startZoomRange.Ticks) * tickToPixel);

                    // AddQuadFilled is used instead of AddRectFilled because it can be used to get antialiasing so that instead of the width being 3 pixels
                    // of a solid color (which looks too strong), it is 1 pixel solid with 1 pixel on each side of antialiasing which looks good.
                    drawList.AddQuadFilled(
                        new Vector2(xPos - 0.5f + _trackAreaLeftScreenPos.Value, timelineTopLeft.Y),
                        new Vector2(xPos + 1.5f + _trackAreaLeftScreenPos.Value, timelineTopLeft.Y),
                        new Vector2(xPos + 1.5f + _trackAreaLeftScreenPos.Value, 1000000),
                        new Vector2(xPos - 0.5f + _trackAreaLeftScreenPos.Value, 1000000),
                        color);
                }
            }

            // Draw the start/stop and duration of the zoomed region on the timeline.
            float timelineHeight = ImGui.GetFontSize() * 1; // * 1 because there is just one row of text at the moment.
            ImGui.Dummy(new Vector2(trackRegionAvailableWidth, timelineHeight));
            if (ImGui.IsItemVisible())
            {
                float lineHeight = ImGui.GetTextLineHeight();
                Vector4 clipRect = new(timelineTopLeft.X, timelineTopLeft.Y, timelineTopLeft.X + trackRegionAvailableWidth, timelineTopLeft.Y + timelineHeight);
                var drawAligned = (string text, float xPercent, float y) =>
                {
                    float textLength = ImGui.CalcTextSize(text).X;
                    float x = xPercent * (trackRegionAvailableWidth - textLength);
                    drawList.AddText(null /* default font  */, 0.0f /* default font size */,
                        timelineTopLeft + new Vector2(x, y), ImGui.GetColorU32(ImGuiCol.Text), text, 0.0f /* no text wrap */,
                        ref clipRect);
                };

                // NOTE: Update timelineHeight coefficient if more lines are added.
                drawAligned($"\uF060 {FriendlyStringify.ToString(startRange - _latestComputedTracks.StartTraceTable)}", 0.0f, lineHeight * 0);
                drawAligned($"{FriendlyStringify.ToString(endRange - _latestComputedTracks.StartTraceTable)} \uF061", 1.0f, lineHeight * 0);
                drawAligned($"[{FriendlyStringify.ToString(rangeDuration)}]", 0.5f, lineHeight * 0);
            }
        }

        private void DrawTrack(ComputedTrack track, DateTime startRange, DateTime endRange, bool isPinned, List<object> hoveredEvents)
        {
            TimeSpan rangeDuration = endRange - startRange;

            uint textColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            float textLineHeight = ImGui.GetTextLineHeight();
            float barHeight = textLineHeight;
            float tickHeight = (float)Math.Round(barHeight * 0.8f);
            float tickLevelHeight = (float)Math.Round(barHeight / 6.0f);
            float tickHalfWidth = (float)Math.Round(barHeight / 4.0f);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            ImGui.PushID(track.UniqueKey);

            if (isPinned)
            {
                if (ImGuiWidgets.UndecoratedButton("\uE68F", "Unpin"))
                {
                    _pinnedTracks.Remove(track.UniqueKey);
                }
            }
            else
            {
                if (ImGuiWidgets.UndecoratedButton("\uF08D", "Pin to top"))
                {
                    _pinnedTracks.Add(track.UniqueKey);
                }
            }
            ImGui.SameLine();

            string threadDescription = string.IsNullOrEmpty(track.ThreadName) ? $"{track.ThreadId}" : $"{track.ThreadId} ({track.ThreadName})";
            ImGui.TextUnformatted(threadDescription);

            ImGui.TableNextColumn();

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();

            float trackRegionAvailableWidth = ImGui.GetContentRegionAvail().X;
            Vector2 trackTopLeft = ImGui.GetCursorScreenPos();

            float tickToPixel = trackRegionAvailableWidth / rangeDuration.Ticks;

            // Reserve space for rendering. This conveniently also allows us to check visibility and skip rendering for non-visible tracks.
            float trackHeight = Math.Max((track.MaxBarDepth + 1) * barHeight, (track.MaxInstantEventDepth + 1) * barHeight);
            ImGui.Dummy(new Vector2(trackRegionAvailableWidth, trackHeight));
            if (ImGui.IsItemVisible())
            {
                Vector2 mousePos = ImGui.GetMousePos();

                DateTime mouseTimestamp = new DateTime(Math.Max((long)((mousePos.X - trackTopLeft.X) * (1 / tickToPixel)), DateTime.MinValue.Ticks));

                float minTextRenderLengthPixels = textLineHeight * 0.2f;
                foreach (var bar in track.Bars)
                {
                    if (bar.Stop.Ticks < startRange.Ticks || bar.Start.Ticks > endRange.Ticks)
                    {
                        continue; // Skip if the bar is outside the range.
                    }

                    long startRelativeTicks = bar.Start.Ticks - startRange.Ticks;
                    long stopRelativeTicks = bar.Stop.Ticks - startRange.Ticks;

                    // Truncate the bar so it doesn't draw outside the range.
                    startRelativeTicks = Math.Max(startRelativeTicks, 0);
                    stopRelativeTicks = Math.Min(stopRelativeTicks, endRange.Ticks - startRange.Ticks);

                    Vector2 min = new Vector2(startRelativeTicks * tickToPixel, bar.Depth * barHeight) + trackTopLeft;
                    Vector2 max = new Vector2(stopRelativeTicks * tickToPixel + 1 /* +1 otherwise 0 width is invisible */, (bar.Depth + 1) * barHeight) + trackTopLeft;

                    // The earlier IsItemVisible check will ensure some part of the track is visible, but the part the mouse is hovering over might be clipped so
                    // we need to check if the mouse point is visible in addition to if it is over the bar.
                    bool isHovered = (mousePos.Y >= min.Y && mousePos.Y < max.Y && mousePos.X >= min.X && mousePos.X < max.X) &&
                        ImGui.IsRectVisible(mousePos, mousePos);

                    uint barColor = isHovered ? DarkenColor(bar.Color) : bar.Color;
                    drawList.AddRectFilled(min, max, barColor);

                    // Don't bother rendering any text in a bar unless it has some space to see something
                    if (max.X - min.X >= minTextRenderLengthPixels)
                    {
                        float centerX = ((max.X - min.X) - ImGui.CalcTextSize(bar.Name).X) / 2.0f;
                        Vector4 clipRect = new(min.X, min.Y, max.X, max.Y);
                        drawList.AddText(null /* default font  */, 0.0f /* default font size */,
                            min + new Vector2(centerX, -1), ImGui.GetColorU32(0xFFFFFFFF), bar.Name, 0.0f /* no text wrap */,
                            ref clipRect);
                    }

                    if (isHovered)
                    {
                        hoveredEvents.Add(bar);
                    }
                }

                Func<DateTime, int, Vector2> getTickTop = (timestamp, depth) =>
                {
                    long relativeTicks = timestamp.Ticks - startRange.Ticks;
                    Vector2 tickTop = new Vector2((float)Math.Round(relativeTicks * tickToPixel), depth * barHeight) + trackTopLeft;
                    tickTop.X += 0.5f; // Move half a pixel so that the a single pixel of the top tip is inside the triangle. Otherwise the top tip is 2 pixels wide.
                    return tickTop;
                };

                // When multiple instant events are on the same pixel, we aggregate them into one tick where the intensity of the shade of gray is based on the number of events.
                // We also will need to remember the most severe level of the events that are on the same pixel so we can draw the most important level marker.
                int overlapsPreviousEventCounter = 0;
                UnifiedLevel mostSevereTickLevel = UnifiedLevel.Verbose;

                for (int i = 0; i < track.InstantEvents.Count; i++)
                {
                    var instantEvent = track.InstantEvents[i];

                    if (instantEvent.Timestamp.Ticks < startRange.Ticks || instantEvent.Timestamp.Ticks > endRange.Ticks)
                    {
                        continue; // Skip if the event is outside the range.
                    }

                    Vector2 tickTop = getTickTop(instantEvent.Timestamp, instantEvent.Depth);
                    Vector2 tickBottomLeft = tickTop + new Vector2(-tickHalfWidth, tickHeight);
                    Vector2 tickBottomRight = tickTop + new Vector2(tickHalfWidth, tickHeight);
                    // This '+ 0.01f' results in the left and right edges of the triangle being symmetrical.
                    // I think because D3D uses the "Top-Left" rule, this results in the right edge being biased to match the left edge.
                    // See the += 0.5f in getTickTop which is another nudge to the rasterizer to render things optimally.
                    tickBottomRight.X += 0.01f;

                    mostSevereTickLevel = (UnifiedLevel)Math.Max((int)mostSevereTickLevel, (int)instantEvent.Level);

                    // The earlier IsItemVisible check will ensure some part of the track is visible, but the part the mouse is hovering over might be clipped so
                    // we need to check if the mouse point is visible in addition to if it is over the instant event.
                    bool isHovered = (mousePos.Y >= tickTop.Y && mousePos.Y < tickBottomRight.Y && mousePos.X >= tickBottomLeft.X && mousePos.X < tickBottomRight.X) &&
                        ImGui.IsRectVisible(mousePos, mousePos);

                    if (isHovered)
                    {
                        hoveredEvents.Add(instantEvent);
                    }

                    if (i < track.InstantEvents.Count - 1 && getTickTop(track.InstantEvents[i + 1].Timestamp, track.InstantEvents[i + 1].Depth) == tickTop)
                    {
                        overlapsPreviousEventCounter++;
                        continue; // The last event on this same pixel will draw the aggregated tick.
                    }

                    uint color = overlapsPreviousEventCounter > 0 ? CalcOverlappingEventColor(overlapsPreviousEventCounter) : instantEvent.Color;
                    uint tickColor = isHovered ? DarkenColor(color) : color;

                    drawList.AddTriangleFilled(tickTop, tickBottomRight, tickBottomLeft, tickColor);

                    Vector4? levelMarkerColor = mostSevereTickLevel switch
                    {
                        UnifiedLevel.Fatal => AppTheme.FatalColor,
                        UnifiedLevel.Error => AppTheme.ErrorColor,
                        UnifiedLevel.Warning => AppTheme.WarningColor,
                        _ => null
                    };

                    if (levelMarkerColor.HasValue)
                    {
                        Vector2 levelMarkerBottomRight = tickBottomRight + new Vector2(0, tickLevelHeight);
                        drawList.AddRectFilled(tickBottomLeft, levelMarkerBottomRight, ImGui.ColorConvertFloat4ToU32(levelMarkerColor.Value));
                    }

                    overlapsPreviousEventCounter = 0;
                    mostSevereTickLevel = UnifiedLevel.Verbose;
                }

            }

            ImGui.PopID();
        }

        private void ApplyZoomPanAndClamp()
        {
            _startZoomRange = ClampDateTime(_startZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);
            _endZoomRange = ClampDateTime(_endZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);

            if (!_trackAreaWidth.HasValue || !_trackAreaLeftScreenPos.HasValue || _trackAreaWidth.Value <= 0)
            {
                return;
            }

            float zoomAmount = 0, panAmountPixels = 0;
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
            {
                if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
                {
                    zoomAmount = ImGui.GetIO().MouseWheel; // Positive = zoom in. Negative = zoom out.
                    _accumulatedPanAmountPixels = 0;
                }
                if (ImGui.IsKeyDown(ImGuiKey.ModShift) && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                    panAmountPixels = ImGui.GetIO().MouseDelta.X;
                }
                else
                {
                    _accumulatedPanAmountPixels = 0;
                }
            }

            TimeSpan zoomDuration = _endZoomRange - _startZoomRange;
            if (zoomAmount != 0)
            {
                float percentZoomPerWheelClick = 0.15f; // 15% zoom per wheel click.

                // Adjust zoom to the left/right of the mouse cursor so that the zoom is centered around the mouse cursor.
                float zoomPointPercent = (ImGui.GetMousePos().X - _trackAreaLeftScreenPos.Value) / _trackAreaWidth.Value;

                // Zoom in proportionally to the mouse position to maintain the position of what is under the mouse.
                TimeSpan adjustDuration = zoomDuration * percentZoomPerWheelClick * zoomAmount;
                _startZoomRange = _startZoomRange + adjustDuration * zoomPointPercent;
                _endZoomRange = _endZoomRange - adjustDuration * (1 - zoomPointPercent);
            }

            if (panAmountPixels != 0)
            {
                // Multiplication is happening here before dividing to avoid rounding down to 0 at extremely high zoom levels.
                // More logically you would do zoomDuration / trackAreaWidth to get a pixelToTick coefficient and then multiply by pan amount, but this has worse precision.
                TimeSpan adjustDuration = (zoomDuration * -(panAmountPixels + _accumulatedPanAmountPixels)) / _trackAreaWidth.Value;

                if (adjustDuration.Ticks == 0)
                {
                    // If the user has zoomed in A LOT (like the zoomDuration is 10us), then the adjustedDuration may round to 0 since TimeSpan/DateTime can only store with
                    // a granularity of 0.1us. So in this case, accumulate the panning until it's enough to not round to zero.
                    _accumulatedPanAmountPixels += panAmountPixels;
                }
                else
                {
                    _accumulatedPanAmountPixels = 0;

                    // Slide the start/end range by the same amount without going out of bounds.
                    _startZoomRange = _startZoomRange + adjustDuration < _latestComputedTracks.StartTraceTable ? _latestComputedTracks.StartTraceTable : _startZoomRange + adjustDuration;
                    _endZoomRange = _endZoomRange + adjustDuration > _latestComputedTracks.EndTraceTable ? _latestComputedTracks.EndTraceTable : _endZoomRange + adjustDuration;

                    // Prevent the range from going outside the log range while keeping the range duration constant (dumb clamping will result in a zoom).
                    _startZoomRange = (_endZoomRange == _latestComputedTracks.EndTraceTable) ? (_latestComputedTracks.EndTraceTable - zoomDuration) : _startZoomRange;
                    _endZoomRange = (_startZoomRange == _latestComputedTracks.StartTraceTable) ? (_latestComputedTracks.StartTraceTable + zoomDuration) : _endZoomRange;
                }
            }

            _startZoomRange = ClampDateTime(_startZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);
            _endZoomRange = ClampDateTime(_endZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);
        }

        private static ComputedTracks ComputeTracks(ITraceTableSnapshot traceTable)
        {
            var stopwatch = Stopwatch.StartNew();

            Dictionary<PidTidKey, Track> tracks = new();

            for (int i = 0; i < traceTable.RowCount; i++)
            {
                PidTidKey trackKey = new PidTidKey(traceTable.GetProcessId(i), traceTable.GetThreadId(i));

                // If PID or TID is 0 or -1 (these are values from ETW parsing sometimes) then there is no process/thread attributed to the event.
                // For example, a Kernel Process Start event has no associated thread.
                if (trackKey.Pid <= 0 || trackKey.Tid <= 0)
                {
                    continue;
                }

                if (!tracks.TryGetValue(trackKey, out Track? track))
                {
                    track = new Track();
                    tracks.Add(trackKey, track);
                }

                if (track.ProcessName == null)
                {
                    track.ProcessName = traceTable.GetColumnValueNameForId(i, traceTable.Schema.ProcessIdColumn);
                }
                if (track.ThreadName == null)
                {
                    track.ThreadName = traceTable.GetColumnValueNameForId(i, traceTable.Schema.ThreadIdColumn);
                }

                DateTime traceEventTime = traceTable.GetTimestamp(i);
                string name = traceTable.GetName(i);

                UnifiedOpcode opcode = traceTable.Schema.UnifiedOpcodeColumn != null ? traceTable.GetUnifiedOpcode(i) : UnifiedOpcode.None;
                if (opcode == UnifiedOpcode.Start)
                {
                    // Push the start time onto the stack.
                    track.StartEvents.Push(new Track.StartEvent { Timestamp = traceEventTime, Name = name, VisibleRowIndex = i });
                }
                else if (opcode == UnifiedOpcode.Stop)
                {
                    if (track.StartEvents.TryPeek(out Track.StartEvent startEvent))
                    {
                        if (startEvent.Name != name)
                        {
                            // This can happen if there is a Stop for a Start that happened before the trace capture started.
                            continue;
                        }

                        track.StartEvents.Pop();
                        track.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = traceEventTime, Depth = track.StartEvents.Count, Name = name, Color = GenerateColorFromName(name), VisibleRowIndex = startEvent.VisibleRowIndex });
                    }
                }
                else
                {
                    UnifiedLevel level = traceTable.Schema.UnifiedLevelColumn == null ? UnifiedLevel.Info : traceTable.GetUnifiedLevel(i);
                    track.InstantEvents.Add(new InstantEvent { Timestamp = traceEventTime, Level = level, Depth = track.StartEvents.Count, Color = GenerateColorFromName(name), VisibleRowIndex = i });
                }
            }

            // Add implicit stops for any starts that are still open
            foreach (var trackKeyValue in tracks)
            {
                DateTime endTraceTable = traceTable.GetTimestamp(traceTable.RowCount - 1);
                while (trackKeyValue.Value.StartEvents.TryPop(out Track.StartEvent startEvent))
                {
                    trackKeyValue.Value.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = endTraceTable, Depth = trackKeyValue.Value.StartEvents.Count, Name = startEvent.Name, Color = GenerateColorFromName(startEvent.Name), VisibleRowIndex = startEvent.VisibleRowIndex });
                }
            }

            Trace.WriteLine($"Processed events in {stopwatch.ElapsedMilliseconds}ms");
            stopwatch.Restart();

            var computedTracks = new List<ComputedTrack>();
            {
                // Group by Pid+ProcessName, ordered so that the most active processes are at the top. Bars count as two events (start/stop), instant events count as one.
                // We group by Pid+Process name because PIDs can be reused so this can prevent separate instances to be combined.
                // The UI also expects all tracks for a process to be grouped together so that the process name is shown only once.
                foreach (var processTracks in tracks.GroupBy(t => (t.Key.Pid, t.Value.ProcessName)).OrderByDescending(tg => tg.Sum(tg2 => (tg2.Value.Bars.Count * 2) + tg2.Value.InstantEvents.Count)))
                {
                    // Next order the tracks for the process by thread id so they are easy to visually search.
                    foreach (var track in processTracks.OrderBy(t => t.Key.Tid))
                    {
                        if (track.Value.Bars.Count == 0 && track.Value.InstantEvents.Count == 0)
                        {
                            continue;
                        }

                        computedTracks.Add(new ComputedTrack
                        {
                            ProcessId = track.Key.Pid,
                            ProcessName = track.Value.ProcessName,
                            ThreadId = track.Key.Tid,
                            ThreadName = track.Value.ThreadName,
                            MaxBarDepth = track.Value.Bars.Count > 0 ? track.Value.Bars.Max(b => b.Depth) : 0,
                            Bars = track.Value.Bars,
                            MaxInstantEventDepth = track.Value.InstantEvents.Count > 0 ? track.Value.InstantEvents.Max(b => b.Depth) : 0,
                            InstantEvents = track.Value.InstantEvents
                        });
                    }
                }
            }

            Trace.WriteLine($"Grouped and sorted events in {stopwatch.ElapsedMilliseconds}ms");

            return new ComputedTracks { Tracks = computedTracks, TraceTableSnapshot = traceTable };
        }

        private static uint DarkenColor(uint originalColor)
        {
            Vector4 originalColorVec = ImGui.ColorConvertU32ToFloat4(originalColor);
            ImGui.ColorConvertRGBtoHSV(originalColorVec.X, originalColorVec.Y, originalColorVec.Z, out float h, out float s, out float v);
            ImGui.ColorConvertHSVtoRGB(h, s, v * 0.8f /* darken by 20% */, out float r, out float g, out float b);
            return ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, originalColorVec.W));
        }

        // Calculate a color where intensity is related to the number of events.
        private static unsafe uint CalcOverlappingEventColor(int eventCount)
        {
            // This converts the number of events that collide on a single pixel to the log2 and then makes it a percentage (0.0 to 1.0)
            // So 2 events = 16%, 4 events = 33%, 8 events = 50%, ..., and 64 events = 100%
            float MaxLogEventCount = 6; // 2^6 = 64
            float t = Math.Min((float)Math.Log2(eventCount), MaxLogEventCount /* cap at 64 or more events */) / MaxLogEventCount;

            // Lerp between dark and light gray. We don't want to go too bright or dark where the color would be hard to see (blend in with the background) depending on user's color theme.
            return ImGui.GetColorU32(Vector4.Lerp(new Vector4(0.4f, 0.4f, 0.4f, 1.0f), new Vector4(0.7f, 0.7f, 0.7f, 1.0f), t));
        }

        // Use a deterministic hash to generate a color from a name avoiding too little saturation and a bright (but not too bright!) value.
        private static uint GenerateColorFromName(string name)
        {
            uint hash = XxHash32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(name));

            uint hue = hash % 360;
            uint saturation = 50 + (hash % 50);
            // Values over 80 can make white text hard to read and under 60 are aesthetically displeasing.
            uint value = 60 + (hash % 20);

            // Convert HSL to RGB
            ImGui.ColorConvertHSVtoRGB(hue / 360.0f, saturation / 100.0f, value / 100.0f, out float r, out float g, out float b);

            return ImGui.GetColorU32(new Vector4(r, g, b, 1.0f));
        }

        private static DateTime ClampDateTime(DateTime dateTime, DateTime min, DateTime max) => dateTime < min ? min : dateTime > max ? max : dateTime;
    }
}
