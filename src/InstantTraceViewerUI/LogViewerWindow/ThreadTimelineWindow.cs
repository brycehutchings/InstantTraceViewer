/*
 * 1. Mouse hover toolip. Show multiple names if multiple events are within 1-2px of the mouse pointer X.
 * 2. Click to jump to event? (both directions?)
 * 3. Show time range / ticks at the top. Rename ticks below to something more like "arrow"?
 * 4. Click-drag to select time range and show duration. Allow zoom to it?
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
        private const string PopupName = "Scopes";

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
        }

        public struct InstantEvent
        {
            public DateTime Timestamp;
            public int Depth;
            public UnifiedLevel Level;
            public string Name;
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
        }

        private Task<ComputedTracks> _computedTracks = null;
        private HashSet<string> _pinnedTracks = new(); // Value is ComputedTrack UniqueKey.
        private DateTime? _startRange = null;
        private DateTime? _endRange = null;

        private float? _trackAreaLeftScreenPos;
        private float? _trackAreaWidth;

        public ThreadTimelineWindow(string name, string parentWindowId)
        {
            _name = name;
            _parentWindowId = parentWindowId;
        }

        public bool DrawWindow(IUiCommands uiCommands, ITraceTableSnapshot traceTable, DateTime? startWindow, DateTime? endWindow)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 70), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(100, 70), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{PopupName} - {_name}###Scopes_{_parentWindowId}", ref _open))
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

            if (traceTable.RowCount == 0)
            {
                return;
            }

            bool? expandCollapse = null; // true=expand, false=collapse, null=no change

            if (ImGui.Button("Refresh"))
            {
                _computedTracks = null;
            }
            ImGui.SameLine();
            if (ImGui.Button("Collapse All"))
            {
                expandCollapse = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Expand All"))
            {
                expandCollapse = false;
            }

            DateTime startLog = traceTable.GetTimestamp(0);
            DateTime endLog = traceTable.GetTimestamp(traceTable.RowCount - 1);

            if (_computedTracks == null)
            {
                _computedTracks = Task.Run(() => ComputeTracks(traceTable, endLog));
            }

            if (_computedTracks.IsCompletedSuccessfully)
            {
                DrawTracks(startLog, endLog, expandCollapse);
            }
            else if (_computedTracks.IsFaulted)
            {
                ImGui.TextUnformatted($"Error computing tracks: {_computedTracks.Exception?.InnerExceptions.First().Message}");
            }
            else
            {
                ImGui.TextUnformatted("Processing tracks...");
            }
        }

        private void DrawTracks(DateTime startLog, DateTime endLog, bool? expandCollapse)
        {
            float zoomAmount = 0, moveAmount = 0;
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
            {
                if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
                {
                    ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);
                    zoomAmount = ImGui.GetIO().MouseWheel; // Positive = zoom in. Negative = zoom out.

                }
                if (ImGui.IsKeyDown(ImGuiKey.ModShift) && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                    moveAmount = ImGui.GetIO().MouseDelta.X;
                }
            }

            DateTime startRange = (_startRange.HasValue && _startRange.Value >= startLog) ? _startRange.Value : startLog;
            DateTime endRange = (_endRange.HasValue && _endRange.Value <= endLog) ? _endRange.Value : endLog;

            TimeSpan rangeDuration = endRange - startRange;

            if (_trackAreaWidth.HasValue && _trackAreaLeftScreenPos.HasValue && _trackAreaWidth.Value > 0)
            {
                if (zoomAmount != 0)
                {
                    float percentZoomPerWheelClick = 0.15f; // 15% zoom per wheel click.

                    // Adjust zoom to the left/right of the mouse cursor so that the zoom is centered around the mouse cursor.
                    float zoomPointPercent = (ImGui.GetMousePos().X - _trackAreaLeftScreenPos.Value) / _trackAreaWidth.Value;

                    // Zoom in proportionally to the mouse position to maintain the position of what is under the mouse.
                    TimeSpan adjustDuration = rangeDuration * percentZoomPerWheelClick * zoomAmount;
                    _startRange = startRange + adjustDuration * zoomPointPercent;
                    _endRange = endRange - adjustDuration * (1 - zoomPointPercent);
                }
                if (moveAmount != 0)
                {
                    TimeSpan pixelToTick = rangeDuration / _trackAreaWidth.Value;
                    TimeSpan adjustDuration = pixelToTick * -moveAmount;

                    // Slide the start/end range by the same amount without going out of bounds.
                    _startRange = _startRange + adjustDuration < startLog ? startLog : _startRange + adjustDuration;
                    _endRange = _endRange + adjustDuration > endLog ? endLog : _endRange + adjustDuration;

                    // Prevent the range from going outside the log range to keep the range duration constant.
                    _startRange = (_endRange == endLog) ? (endLog - rangeDuration) : _startRange;
                    _endRange = (_startRange == startLog) ? (startLog + rangeDuration) : _endRange;
                }
            }

            // These will be set as we draw the table which is when this information is available.
            _trackAreaLeftScreenPos = null;
            _trackAreaWidth = null;

            // ImGui.TableHeadersRow(); // We don't actually want the header shown

            Debug.Assert(_computedTracks.IsCompletedSuccessfully);

            Vector2 contentRegion = ImGui.GetContentRegionAvail();
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();

            // 'ScrollY' required for freezing rows (for pinning).
            if (ImGui.BeginTable("ScopesTable", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY))
            {
                var pinnedTracks = _computedTracks.Result.Tracks.Where(t => _pinnedTracks.Contains(t.UniqueKey)).ToList();
                ImGui.TableSetupScrollFreeze(0, pinnedTracks.Count); // Pinned tracks are always visible.

                float dpiBase = ImGui.GetFontSize();
                ImGui.TableSetupColumn("Thread", ImGuiTableColumnFlags.WidthFixed, 10.0f * dpiBase);

                // We do our own clipping (both with PushClipRect and with Min/Max), so we can have ImGui draw everything in this column in one draw call by setting NoClip.
                ImGui.TableSetupColumn("Track", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoClip, 1.0f);

                foreach (var track in pinnedTracks)
                {
                    DrawTrack(track, drawList, startRange, endRange, isPinned: true);
                }

                if (pinnedTracks.Any())
                {
                    // Ensure custom drawing doesn't scroll up into the frozen track area.
                    // 100,000 is a "very large size" to include everything to the right and below this point (float.MaxValue doesn't work).
                    Vector2 startClip = ImGui.GetCursorScreenPos();
                    drawList.PushClipRect(startClip, new Vector2(startClip.X + 100000, 100000), false);
                }

                string? previousProcessName = null;
                bool isOpen = false;
                foreach (var track in _computedTracks.Result.Tracks)
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

                    DrawTrack(track, drawList, startRange, endRange, isPinned);
                }

                if (pinnedTracks.Any())
                {
                    drawList.PopClipRect();
                }

                if (isOpen)
                {
                    ImGui.TreePop();
                }

                ImGui.EndTable();
            }
        }

        private void DrawTrack(ComputedTrack track, ImDrawListPtr drawList, DateTime startRange, DateTime endRange, bool isPinned)
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

            Vector2 trackRegionAvailable = ImGui.GetContentRegionAvail();
            Vector2 trackBarsTopLeft = ImGui.GetCursorScreenPos();

            // We need to remember the starting X and width to do zooming centered on the mouse X coord.
            if (_trackAreaLeftScreenPos == null && _trackAreaWidth == null)
            {
                _trackAreaLeftScreenPos = trackBarsTopLeft.X;
                _trackAreaWidth = trackRegionAvailable.X;
            }

            float tickToPixel = trackRegionAvailable.X / rangeDuration.Ticks;

            // Reserve space for rendering. This conveniently also allows us to check visibility and skip rendering for non-visible tracks.
            float trackHeight = Math.Max((track.MaxBarDepth + 1) * barHeight, (track.MaxInstantEventDepth + 1) * barHeight);
            ImGui.Dummy(new Vector2(trackRegionAvailable.X, trackHeight));
            if (ImGui.IsItemVisible())
            {
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

                    drawList.AddRectFilled(min, max, bar.Color);

                    // Don't bother rendering any text in a bar unless it has some space to see something
                    if (max.X - min.X >= minTextRenderLengthPixels)
                    {
                        float centerX = ((max.X - min.X) - ImGui.CalcTextSize(bar.Name).X) / 2.0f;
                        Vector4 clipRect = new(min.X, min.Y, max.X, max.Y);
                        drawList.AddText(null /* default font  */, 0.0f /* default font size */,
                            min + new Vector2(centerX, -1), ImGui.GetColorU32(0xFFFFFFFF), bar.Name, 0.0f /* no text wrap */,
                            ref clipRect);
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
                    drawList.AddTriangleFilled(tickTop, tickBottomRight, tickBottomMiddle, instantEvent.Color);
                    drawList.AddTriangleFilled(tickBottomMiddle, tickBottomLeft, tickTop, instantEvent.Color);

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
                }
            }

            ImGui.PopID();
        }

        private static ComputedTracks ComputeTracks(ITraceTableSnapshot traceTable, DateTime endLog)
        {
            var stopwatch = Stopwatch.StartNew();

            // Key=pid, tid.
            Dictionary<(int, int), Track> tracks = new();

            for (int i = 0; i < traceTable.RowCount; i++)
            {
                (int, int) trackKey = (traceTable.GetProcessId(i), traceTable.GetThreadId(i));

                // If PID or TID is 0 or -1 (these are values from ETW parsing sometimes) then there is no process/thread attributed to the event.
                // For example, a Kernel Process Start event has no associated thread.
                if (trackKey.Item1 <= 0 || trackKey.Item2 <= 0)
                {
                    continue;
                }

                if (!tracks.TryGetValue(trackKey, out Track? track))
                {
                    track = new Track { ThreadId = trackKey.Item2 };
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
                UnifiedOpcode opcode = traceTable.GetUnifiedOpcode(i);
                string name = traceTable.GetName(i);

                if (opcode == UnifiedOpcode.Start)
                {
                    // Push the start time onto the stack.
                    track.StartEvents.Push(new Track.StartEvent { Timestamp = traceEventTime, Name = name });
                }
                else if (opcode == UnifiedOpcode.Stop)
                {
                    // TODO: Verify StartTime is for the same Stop by Name. If it isn't... idk
                    // Pop until we find a match, or leave alone if no match.
                    if (track.StartEvents.TryPop(out Track.StartEvent startEvent))
                    {
                        track.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = traceEventTime, Depth = track.StartEvents.Count, Name = name, Color = PickColor(name) });
                    }
                }
                else
                {
                    track.InstantEvents.Add(new InstantEvent { Timestamp = traceEventTime, Level = traceTable.GetUnifiedLevel(i), Depth = track.StartEvents.Count, Name = name, Color = PickColor(name) });
                }
            }

            // Add implicit stops for any starts that are still open
            foreach (var trackKeyValue in tracks)
            {
                while (trackKeyValue.Value.StartEvents.TryPop(out Track.StartEvent startEvent))
                {
                    trackKeyValue.Value.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = endLog, Depth = trackKeyValue.Value.StartEvents.Count, Name = startEvent.Name, Color = PickColor(startEvent.Name) });
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

            return new ComputedTracks { Tracks = computedTracks };
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

        private string GetSmartDurationString(TimeSpan timeSpan)
        {
            if (timeSpan.TotalSeconds >= 60)
            {
                return $"{(int)timeSpan.TotalMinutes}m {timeSpan.TotalSeconds - (int)timeSpan.TotalMinutes * 60:0.000000}s";
            }
            else
            {
                return $"{timeSpan.TotalSeconds:0.000000}s";
            }
        }
    }
}
