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
    internal class ScopeWindow
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
        }

        class ComputedTracks
        {
            // Tracks are ordered so that all tracks for a given process are continguous. The renderer depends on this because it creates a new group whenever the process name changes.
            public IReadOnlyList<ComputedTrack> Tracks;
        }

        private Task<ComputedTracks> _computedTracks = null;
        private DateTime? _startRange = null;
        private DateTime? _endRange = null;

        public ScopeWindow(string name, string parentWindowId)
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

        private void DrawTimelineGraph(ITraceTableSnapshot traceTable, DateTime? startWindow, DateTime? endWindow)
        {
            if (traceTable.Schema.TimestampColumn == null || traceTable.Schema.NameColumn == null ||
                traceTable.Schema.ProcessIdColumn == null || traceTable.Schema.ThreadIdColumn == null)
            {
                ImGui.TextUnformatted("Required timestamp, name, process id and/or thread id columns are missing.");
                return;
            }

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
            Vector2 contentRegionAvailable = ImGui.GetContentRegionAvail();

            Vector2 tracksTopLeft = ImGui.GetCursorPos();
            Vector2 tracksTopLeftScreenPos = ImGui.GetCursorScreenPos();

            float zoomAmount = 0;
            if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
            {
                ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);
                if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup))
                {
                    zoomAmount = ImGui.GetIO().MouseWheel; // Positive = zoom in. Negative = zoom out.
                }
            }

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();

            DateTime startRange = (_startRange.HasValue && _startRange.Value >= startLog) ? _startRange.Value : startLog;
            DateTime endRange = (_endRange.HasValue && _endRange.Value <= endLog) ? _endRange.Value : endLog;

            TimeSpan rangeDuration = endRange - startRange;

            if (zoomAmount != 0)
            {
                float percentZoomPerWheelClick = 0.1f; // 10% zoom per wheel click.

                // Adjust zoom to the left/right of the mouse cursor so that the zoom is centered around the mouse cursor.
                float zoomPointPercent = (ImGui.GetMousePos().X - tracksTopLeftScreenPos.X) / contentRegionAvailable.X;

                // Zoom in proportionally to the mouse position to maintain the position of what is under the mouse.
                TimeSpan adjustDuration = rangeDuration * percentZoomPerWheelClick * zoomAmount;
                _startRange = startRange + adjustDuration * zoomPointPercent;
                _endRange = endRange - adjustDuration * (1 - zoomPointPercent);
            }

            uint textColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            float textLineHeight = ImGui.GetTextLineHeight();
            float barHeight = textLineHeight;
            float tickHeight = (float)Math.Round(barHeight * 0.8f);
            float tickDivit = (float)Math.Round(barHeight / 10.0f);
            float tickHalfWidth = (float)Math.Round(barHeight / 5.0f);

            float tickToPixel = contentRegionAvailable.X / rangeDuration.Ticks;

            string? previousProcessName = null;
            bool isCollapsed = false;

            Debug.Assert(_computedTracks.IsCompletedSuccessfully);
            foreach (var track in _computedTracks.Result.Tracks)
            {
                if (previousProcessName != track.ProcessName)
                {
                    if (expandCollapse.HasValue)
                    {
                        ImGui.SetNextItemOpen(expandCollapse.Value, ImGuiCond.Always);
                    }

                    isCollapsed = ImGui.CollapsingHeader(track.ProcessName);
                    previousProcessName = track.ProcessName;
                }

                if (isCollapsed)
                {
                    continue;
                }

                ImGui.TextUnformatted(track.ThreadName);

                Vector2 trackBarsTopLeft = ImGui.GetCursorScreenPos();

                // Reserve space for rendering. This conveniently also allows us to check visibility and skip rendering for non-visible tracks.
                float trackHeight = Math.Max((track.MaxBarDepth + 1) * barHeight, (track.MaxInstantEventDepth + 1) * barHeight);
                ImGui.Dummy(new Vector2(contentRegionAvailable.X, trackHeight));
                if (!ImGui.IsItemVisible())
                {
                    continue;
                }

                float minTextRenderLengthPixels = textLineHeight * 0.2f;
                foreach (var bar in track.Bars)
                {
                    if (bar.Stop.Ticks < startRange.Ticks || bar.Start.Ticks > endRange.Ticks)
                    {
                        continue; // Skip if the bar is outside the range.
                    }

                    long startRelativeTicks = bar.Start.Ticks - startRange.Ticks;
                    long stopRelativeTicks = bar.Stop.Ticks - startRange.Ticks;

                    Vector2 min = new Vector2(startRelativeTicks * tickToPixel, bar.Depth * barHeight) + trackBarsTopLeft;
                    Vector2 max = new Vector2(stopRelativeTicks * tickToPixel + 1 /* +1 otherwise 0 width is invisible */, (bar.Depth + 1) * barHeight) + trackBarsTopLeft;

                    drawList.AddRectFilled(min, max, bar.Color);

                    // Don't bother rendering any text in a bar unless it has some space to see something
                    if (max.X - min.X >= minTextRenderLengthPixels)
                    {
                        Vector4 clipRect = new(min.X, min.Y, max.X, max.Y);
                        drawList.AddText(null /* default font  */, 0.0f /* default font size */,
                            min + new Vector2(1, 0), ImGui.GetColorU32(0xFFFFFFFF), bar.Name, 0.0f /* no text wrap */,
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
                    Vector2 tickBottomRight = tickTop + new Vector2(tickHalfWidth, tickHeight + 1);
                    Vector2 tickBottomMiddle = tickTop + new Vector2(0, tickHeight - tickDivit + 1);
                    Vector2 tickBottomLeft = tickTop + new Vector2(-tickHalfWidth, tickHeight + 1);
                    drawList.AddTriangleFilled(tickTop, tickBottomRight, tickBottomMiddle, instantEvent.Color);
                    drawList.AddTriangleFilled(tickBottomMiddle, tickBottomLeft, tickTop, instantEvent.Color);
                }
            }
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
