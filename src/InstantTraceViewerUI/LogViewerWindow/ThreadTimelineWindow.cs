/*
 * --- MVP
 * 0. Show startWindow/endWindow range in graph.
 * 1. Fix stack popping with name matching.
 * --- FUTURE
 * 0. Show indication in hover tooltip if there are multiple events under the mouse.
 * 1. Click-drag to select time range and show duration. Allow zoom to it.
 * 2. Inline thread timeline in log viewer with only pinned threads. Context menu item for thread cell to pin.
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
            public int VisibleRowIndex; // For start event.
            public uint Color;

            public TimeSpan Duration => Stop - Start;
        }

        public struct InstantEvent
        {
            public DateTime Timestamp;
            public int Depth;
            public UnifiedLevel Level;
            public string Name;
            public int VisibleRowIndex;
            public uint Color;
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
            public int ThreadId;
            public string? ThreadName;
            public List<Bar> Bars = new();
            public List<InstantEvent> InstantEvents = new();
        }

        struct ComputedTrack
        {
            public string ProcessName;
            public string ThreadName;

            public int MaxBarDepth;
            public List<Bar> Bars;

            public int MaxInstantEventDepth;
            public List<InstantEvent> InstantEvents;

            public string UniqueKey => $"{ProcessName}_{ThreadName}";
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
        private HashSet<string> _pinnedTracks = new(); // Value is ComputedTrack UniqueKey.
        private DateTime _startZoomRange = DateTime.MinValue;
        private DateTime _endZoomRange = DateTime.MaxValue;
        private bool _isMouseHoveringTable = false;

        private float? _trackAreaLeftScreenPos;
        private float? _trackAreaWidth;

        public ThreadTimelineWindow(string name, string parentWindowId)
        {
            _name = name;
            _parentWindowId = parentWindowId;
        }

        // This is reset every frame and only set if a click happens on an event.
        public int? ClickedVisibleRowIndex { get; set; }

        public bool DrawWindow(IUiCommands uiCommands, ITraceTableSnapshot traceTable, DateTime? startWindow, DateTime? endWindow)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(600, 200), new Vector2(float.MaxValue, float.MaxValue));

            // There is no input control over the track column of the table (it is all drawn manually), so when the user
            // uses SHIFT+MouseMove to pan, we need to disable the window moving because ImGui allows windows to move by
            // click+dragging anywhere there is no input control.
            ImGuiWindowFlags flags = _isMouseHoveringTable ? ImGuiWindowFlags.NoMove : ImGuiWindowFlags.None;
            if (ImGui.Begin($"{PopupName} - {_name}###ThreadTimeline_{_parentWindowId}", ref _open, flags))
            {
                DrawTimelineGraph(traceTable, startWindow, endWindow);
            }

            ImGui.End();

            return _open;
        }

        public static bool IsSupported(TraceTableSchema schema)
            => schema.TimestampColumn != null && schema.NameColumn != null && schema.ProcessIdColumn != null && schema.ThreadIdColumn != null;

        private void DrawTimelineGraph(ITraceTableSnapshot traceTable, DateTime? startWindow, DateTime? endWindow)
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
            ImGuiWidgets.HelpIconToolip(
                "CTRL+Mouse Wheel --- Zoom in and out centered on mouse cursor\n" +
                "SHIFT+Mouse Move Left/Right --- Pan left/right\n" +
                "CTRL+Mouse click --- Jump to hovered event");

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
                DrawTrackGraph(expandCollapse);
            }
            else
            {
                // This is only shown the first time when _latestComputedTracks has no cached result to display.
                ImGui.TextUnformatted("Processing tracks...");
            }
        }

        private void DrawTrackGraph(bool? expandCollapse)
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

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();

            // 'ScrollY' required for freezing rows (for pinning/timeline header).
            if (ImGui.BeginTable("ScopesTable", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY))
            {
                var pinnedTracks = _latestComputedTracks.Tracks.Where(t => _pinnedTracks.Contains(t.UniqueKey)).ToList();
                ImGui.TableSetupScrollFreeze(0, pinnedTracks.Count + 1); // Pinned tracks are always visible. Plus one because we also want to pin the timeline.

                float dpiBase = ImGui.GetFontSize();
                ImGui.TableSetupColumn("Thread", ImGuiTableColumnFlags.WidthFixed, 10.0f * dpiBase);

                // We do our own clipping (both with PushClipRect and with Min/Max), so we can have ImGui draw everything in this column in one draw call by setting NoClip.
                ImGui.TableSetupColumn("Track", ImGuiTableColumnFlags.WidthStretch /*| ImGuiTableColumnFlags.NoClip*/, 1.0f);

                // ImGui.TableHeadersRow(); // We don't actually want the header shown

                DrawTimeline(drawList, _startZoomRange, _endZoomRange);

                object hoveredEvent = null; // Either 'Bar' or 'InstantEvent'
                foreach (var track in pinnedTracks)
                {
                    DrawTrack(track, drawList, _startZoomRange, _endZoomRange, isPinned: true, ref hoveredEvent);
                }

                if (pinnedTracks.Any())
                {
                    // Blank row to separate pinned tracks from the rest. Fill color matches border color so it looks like a solid thick separator.
                    ImGui.TableNextRow();
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableBorderLight));
                    ImGui.TableNextColumn(); // Track name (none for timeline)
                    ImGui.TableNextColumn(); // Track graph area
                }

                // Ensure custom drawing doesn't scroll up into the frozen track area.
                // 1,000,000 is a "very large size" to include everything below this point (float.MaxValue doesn't work).
                Vector2 startClip = ImGui.GetCursorScreenPos();
                drawList.PushClipRect(startClip, new Vector2(startClip.X + 1000000, 1000000), false);

                string? previousProcessName = null;
                bool isOpen = false;
                foreach (var track in _latestComputedTracks.Tracks)
                {
                    bool isPinned = _pinnedTracks.Contains(track.UniqueKey);
                    if (isPinned)
                    {
                        continue; // Pinned tracks were already rendered.
                    }

                    if (previousProcessName != track.ProcessName)
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
                        isOpen = ImGui.TreeNodeEx(track.ProcessName, ImGuiTreeNodeFlags.SpanFullWidth);
                        ImGui.TableNextColumn();

                        previousProcessName = track.ProcessName;
                    }

                    if (!isOpen)
                    {
                        continue;
                    }

                    DrawTrack(track, drawList, _startZoomRange, _endZoomRange, isPinned, ref hoveredEvent);
                }

                drawList.PopClipRect();

                if (isOpen)
                {
                    ImGui.TreePop();
                }

                ImGui.EndTable();

                _isMouseHoveringTable = ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

                if (hoveredEvent != null)
                {
                    bool clickable = false;
                    if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
                    {
                        clickable = true;
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    }

                    if (ImGui.BeginTooltip())
                    {
                        if (hoveredEvent is Bar bar)
                        {
                            if (clickable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            {
                                ClickedVisibleRowIndex = bar.VisibleRowIndex;
                            }
                            ImGui.Text($"{bar.Name} ({FriendlyStringify.ToString(bar.Duration)})");
                        }
                        else if (hoveredEvent is InstantEvent instantEvent)
                        {
                            if (clickable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            {
                                ClickedVisibleRowIndex = instantEvent.VisibleRowIndex;
                            }

                            DrawEventDetails(_latestComputedTracks.TraceTableSnapshot, instantEvent.VisibleRowIndex);
                        }
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

        private void ApplyZoomPanAndClamp()
        {
            _startZoomRange = ClampDateTime(_startZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);
            _endZoomRange = ClampDateTime(_endZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);

            if (!_trackAreaWidth.HasValue || !_trackAreaLeftScreenPos.HasValue || _trackAreaWidth.Value <= 0)
            {
                return;
            }

            float zoomAmount = 0, panAmount = 0;
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
            {
                if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
                {
                    zoomAmount = ImGui.GetIO().MouseWheel; // Positive = zoom in. Negative = zoom out.
                }
                if (ImGui.IsKeyDown(ImGuiKey.ModShift) && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                    panAmount = ImGui.GetIO().MouseDelta.X;
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

            if (panAmount != 0)
            {
                TimeSpan pixelToTick = zoomDuration / _trackAreaWidth.Value;
                TimeSpan adjustDuration = pixelToTick * -panAmount;

                // Slide the start/end range by the same amount without going out of bounds.
                _startZoomRange = _startZoomRange + adjustDuration < _latestComputedTracks.StartTraceTable ? _latestComputedTracks.StartTraceTable : _startZoomRange + adjustDuration;
                _endZoomRange = _endZoomRange + adjustDuration > _latestComputedTracks.EndTraceTable ? _latestComputedTracks.EndTraceTable : _endZoomRange + adjustDuration;

                // Prevent the range from going outside the log range while keeping the range duration constant (dumb clamping will result in a zoom).
                _startZoomRange = (_endZoomRange == _latestComputedTracks.EndTraceTable) ? (_latestComputedTracks.EndTraceTable - zoomDuration) : _startZoomRange;
                _endZoomRange = (_startZoomRange == _latestComputedTracks.StartTraceTable) ? (_latestComputedTracks.StartTraceTable + zoomDuration) : _endZoomRange;
            }

            _startZoomRange = ClampDateTime(_startZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);
            _endZoomRange = ClampDateTime(_endZoomRange, _latestComputedTracks.StartTraceTable, _latestComputedTracks.EndTraceTable);
        }

        private static void DrawEventDetails(ITraceTableSnapshot traceTable, int visibleRowIndex)
        {
            foreach (var col in traceTable.Schema.Columns)
            {
                if (col == traceTable.Schema.ProcessIdColumn || col == traceTable.Schema.ThreadIdColumn)
                {
                    continue; // These are already displayed in the track graphically.
                }

                string value = traceTable.GetColumnValueString(visibleRowIndex, col, allowMultiline: true);
                if (!string.IsNullOrEmpty(value))
                {
                    if (value.Contains('\n'))
                    {
                        ImGui.SeparatorText(col.Name);
                        ImGui.TextUnformatted(value);
                    }
                    else
                    {
                        ImGui.TextUnformatted($"{col.Name}: {value}");
                    }
                }
            }
        }

        private void DrawTimeline(ImDrawListPtr drawList, DateTime startRange, DateTime endRange)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); // Track name (none for timeline)
            ImGui.TableNextColumn(); // Track graph area

            float trackRegionAvailableWidth = ImGui.GetContentRegionAvail().X;
            Vector2 trackBarsTopLeft = ImGui.GetCursorScreenPos();

            // We need to remember the starting X and width to do zooming centered on the mouse X coord.
            if (_trackAreaLeftScreenPos == null && _trackAreaWidth == null)
            {
                _trackAreaLeftScreenPos = trackBarsTopLeft.X;
                _trackAreaWidth = trackRegionAvailableWidth;
            }

            TimeSpan rangeDuration = endRange - startRange;
            float tickToPixel = trackRegionAvailableWidth / rangeDuration.Ticks;
            float timelineHeight = ImGui.GetFontSize() * 1; // * 1 because there is just one row of text at the moment.
            ImGui.Dummy(new Vector2(trackRegionAvailableWidth, timelineHeight));
            if (ImGui.IsItemVisible())
            {
                float lineHeight = ImGui.GetTextLineHeight();
                Vector4 clipRect = new(trackBarsTopLeft.X, trackBarsTopLeft.Y, trackBarsTopLeft.X + trackRegionAvailableWidth, trackBarsTopLeft.Y + timelineHeight);
                var drawAligned = (string text, float xPercent, float y) =>
                {
                    float textLength = ImGui.CalcTextSize(text).X;
                    float x = xPercent * (trackRegionAvailableWidth - textLength);
                    drawList.AddText(null /* default font  */, 0.0f /* default font size */,
                        trackBarsTopLeft + new Vector2(x, y), ImGui.GetColorU32(ImGuiCol.Text), text, 0.0f /* no text wrap */,
                        ref clipRect);
                };

                // NOTE: Update timelineHeight coefficient if more lines are added.
                drawAligned($"\uF060 {FriendlyStringify.ToString(startRange - _latestComputedTracks.StartTraceTable)}", 0.0f, lineHeight * 0);
                drawAligned($"{FriendlyStringify.ToString(endRange - _latestComputedTracks.StartTraceTable)} \uF061", 1.0f, lineHeight * 0);
                drawAligned($"[{FriendlyStringify.ToString(rangeDuration)}]", 0.5f, lineHeight * 0);
            }
        }

        private void DrawTrack(ComputedTrack track, ImDrawListPtr drawList, DateTime startRange, DateTime endRange, bool isPinned, ref object hoveredEvent)
        {
            TimeSpan rangeDuration = endRange - startRange;

            uint textColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            float textLineHeight = ImGui.GetTextLineHeight();
            float barHeight = textLineHeight;
            float tickHeight = (float)Math.Round(barHeight * 0.8f);
            float tickDivit = (float)Math.Round(barHeight / 6.0f);
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

            ImGui.TextUnformatted(track.ThreadName);

            ImGui.TableNextColumn();

            float trackRegionAvailableWidth = ImGui.GetContentRegionAvail().X;
            Vector2 trackBarsTopLeft = ImGui.GetCursorScreenPos();

            float tickToPixel = trackRegionAvailableWidth / rangeDuration.Ticks;

            // Reserve space for rendering. This conveniently also allows us to check visibility and skip rendering for non-visible tracks.
            float trackHeight = Math.Max((track.MaxBarDepth + 1) * barHeight, (track.MaxInstantEventDepth + 1) * barHeight);
            ImGui.Dummy(new Vector2(trackRegionAvailableWidth, trackHeight));
            if (ImGui.IsItemVisible())
            {
                Vector2 mousePos = ImGui.GetMousePos();

                DateTime mouseTimestamp = new DateTime(Math.Max((long)((mousePos.X - trackBarsTopLeft.X) * (1 / tickToPixel)), DateTime.MinValue.Ticks));
                TimeSpan nearestEventOffset = TimeSpan.MaxValue;
                object? nearestEvent = null;

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

                    Vector2 min = new Vector2(startRelativeTicks * tickToPixel, bar.Depth * barHeight) + trackBarsTopLeft;
                    Vector2 max = new Vector2(stopRelativeTicks * tickToPixel + 1 /* +1 otherwise 0 width is invisible */, (bar.Depth + 1) * barHeight) + trackBarsTopLeft;

                    bool isHovered = mousePos.Y >= min.Y && mousePos.Y < max.Y && mousePos.X >= min.X && mousePos.X < max.X;
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
                        nearestEvent = bar;
                        nearestEventOffset = TimeSpan.Zero;
                    }
                }

                foreach (var instantEvent in track.InstantEvents)
                {
                    if (instantEvent.Timestamp.Ticks < startRange.Ticks || instantEvent.Timestamp.Ticks > endRange.Ticks)
                    {
                        continue; // Skip if the event is outside the range.
                    }

                    long relativeTicks = instantEvent.Timestamp.Ticks - startRange.Ticks;

                    Vector2 tickTop = new Vector2((float)Math.Round(relativeTicks * tickToPixel), instantEvent.Depth * barHeight) + trackBarsTopLeft;
                    Vector2 tickBottomRight = tickTop + new Vector2(tickHalfWidth, tickHeight);
                    Vector2 tickBottomMiddle = tickTop + new Vector2(0, tickHeight - tickDivit);
                    Vector2 tickBottomLeft = tickTop + new Vector2(-tickHalfWidth, tickHeight);

                    bool isHovered = mousePos.Y >= tickTop.Y && mousePos.Y < tickBottomRight.Y && mousePos.X >= tickBottomLeft.X && mousePos.X < tickBottomRight.X;
                    uint tickColor = isHovered ? DarkenColor(instantEvent.Color) : instantEvent.Color;

                    drawList.AddTriangleFilled(tickTop, tickBottomRight, tickBottomMiddle, tickColor);
                    drawList.AddTriangleFilled(tickBottomMiddle, tickBottomLeft, tickTop, tickColor);

                    Vector4? levelMarkerColor = instantEvent.Level switch
                    {
                        UnifiedLevel.Fatal => AppTheme.FatalColor,
                        UnifiedLevel.Error => AppTheme.ErrorColor,
                        UnifiedLevel.Warning => AppTheme.WarningColor,
                        _ => null
                    };

                    if (levelMarkerColor.HasValue)
                    {
                        Vector2 levelMarkerBottomRight = tickBottomRight + new Vector2(0, tickDivit);
                        Vector2 levelMarkerBottomLeft = tickBottomLeft + new Vector2(0, tickDivit);
                        drawList.AddTriangleFilled(tickBottomMiddle, tickBottomRight, levelMarkerBottomRight, ImGui.ColorConvertFloat4ToU32(levelMarkerColor.Value));
                        drawList.AddTriangleFilled(tickBottomMiddle, levelMarkerBottomRight, levelMarkerBottomLeft, ImGui.ColorConvertFloat4ToU32(levelMarkerColor.Value));
                        drawList.AddTriangleFilled(tickBottomMiddle, levelMarkerBottomLeft, tickBottomLeft, ImGui.ColorConvertFloat4ToU32(levelMarkerColor.Value));
                    }

                    if (isHovered)
                    {
                        TimeSpan mouseTimeDelta = (instantEvent.Timestamp - mouseTimestamp).Duration();
                        if (mouseTimeDelta < nearestEventOffset)
                        {
                            nearestEvent = instantEvent;
                            nearestEventOffset = mouseTimeDelta;
                        }
                    }
                }

                hoveredEvent ??= nearestEvent;
            }

            ImGui.PopID();
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
                    track = new Track { ThreadId = trackKey.Tid };
                    tracks.Add(trackKey, track);
                }

                if (track.ProcessName == null)
                {
                    track.ProcessName = traceTable.GetColumnValueString(i, traceTable.Schema.ProcessIdColumn);
                }
                if (track.ThreadName == null)
                {
                    track.ThreadName = traceTable.GetColumnValueString(i, traceTable.Schema.ThreadIdColumn);
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
                    // TODO: Verify StartTime is for the same Stop by Name. If it isn't... idk
                    // Pop until we find a match, or leave alone if no match.
                    if (track.StartEvents.TryPop(out Track.StartEvent startEvent))
                    {
                        track.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = traceEventTime, Depth = track.StartEvents.Count, Name = name, VisibleRowIndex = startEvent.VisibleRowIndex, Color = PickColor(name) });
                    }
                }
                else
                {
                    track.InstantEvents.Add(new InstantEvent { Timestamp = traceEventTime, Level = traceTable.GetUnifiedLevel(i), Depth = track.StartEvents.Count, Name = name, VisibleRowIndex = i, Color = PickColor(name) });
                }
            }

            // Add implicit stops for any starts that are still open
            foreach (var trackKeyValue in tracks)
            {
                DateTime endTraceTable = traceTable.GetTimestamp(traceTable.RowCount - 1);
                while (trackKeyValue.Value.StartEvents.TryPop(out Track.StartEvent startEvent))
                {
                    trackKeyValue.Value.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = endTraceTable, Depth = trackKeyValue.Value.StartEvents.Count, Name = startEvent.Name, VisibleRowIndex = startEvent.VisibleRowIndex, Color = PickColor(startEvent.Name) });
                }
            }

            Trace.WriteLine($"Processed events in {stopwatch.ElapsedMilliseconds}ms");
            stopwatch.Restart();

            var computedTracks = new List<ComputedTrack>();
            {
                // Group by process name, ordered so that the most active processes are at the top. Bars count as two events (start/stop), instant events count as one.
                // We must group by process name because ComputedTracks.Tracks requires this for proper UI rendering.
                foreach (var processTracks in tracks.GroupBy(t => t.Value.ProcessName).OrderByDescending(tg => tg.Sum(tg2 => (tg2.Value.Bars.Count * 2) + tg2.Value.InstantEvents.Count)))
                {
                    // Next order the tracks for the process by thread id so they are easy to visually search.
                    foreach (var track in processTracks.OrderBy(t => t.Value.ThreadId))
                    {
                        if (track.Value.Bars.Count == 0 && track.Value.InstantEvents.Count == 0)
                        {
                            continue;
                        }

                        computedTracks.Add(new ComputedTrack
                        {
                            ProcessName = track.Value.ProcessName,
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

        private static uint PickColor(string name)
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

        private static DateTime ClampDateTime(DateTime dateTime, DateTime min, DateTime max)
            => dateTime < min ? min : dateTime > max ? max : dateTime;
    }
}
