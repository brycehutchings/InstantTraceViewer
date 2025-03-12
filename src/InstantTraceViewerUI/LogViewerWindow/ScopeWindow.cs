// Colorization research: https://www.perplexity.ai/search/what-algorithm-does-ui-perfett-0cQg61wjSjykpzHEY5loMQ

using ImGuiNET;
using System;
using System.Numerics;
using InstantTraceViewer;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;

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
        }

        public struct InstantEvent
        {
            public DateTime Timestamp;
            public int Depth;
            public UnifiedLevel Level;
            public string Name;
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
            public List<Bar> Bars;
            public List<InstantEvent> InstantEvents;
        }

        class ComputedTracks
        {
            public IReadOnlyList<ComputedTrack> Tracks;
        }

        private Dictionary<string, bool> _processCollapsed = new Dictionary<string, bool>();
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

            if (ImGui.Button("Refresh"))
            {
                _computedTracks = null;
            }

            DateTime startLog = traceTable.GetTimestamp(0);
            DateTime endLog = traceTable.GetTimestamp(traceTable.RowCount - 1);

            if (_computedTracks == null)
            {
                _computedTracks = Task.Run(() => ComputeTracks(traceTable, endLog));
            }

            if (_computedTracks.IsCompletedSuccessfully)
            {
                DrawTracks(startLog, endLog);
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

        private void DrawTracks(DateTime startLog, DateTime endLog)
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
            float barHeight = ImGui.GetTextLineHeight();
            float tickPadding = barHeight / 7.0f;
            float tickDivit = barHeight / 10.0f;

            float tickToPixel = contentRegionAvailable.X / rangeDuration.Ticks;
            int totalBars = 0;
            string? previousProcessName = null;
            Debug.Assert(_computedTracks.IsCompletedSuccessfully);
            foreach (var track in _computedTracks.Result.Tracks)
            {
                _processCollapsed.TryGetValue(track.ProcessName, out bool isCollapsed);
                if (previousProcessName != track.ProcessName)
                {
                    isCollapsed = ImGui.CollapsingHeader(track.ProcessName);
                    _processCollapsed[track.ProcessName] = isCollapsed;
                    previousProcessName = track.ProcessName;
                }

                if (isCollapsed)
                {
                    continue;
                }

                ImGui.TextUnformatted(track.ThreadName);

                Vector2 trackBarsTopLeft = ImGui.GetCursorScreenPos();

                // This optimization might not help
                //if (trackBarsTopLeft.Y > (contentRegionTopLeft.Y + contentRegionAvailable.Y))
                //{
                //    break; // We are below the fold and won't be seen.
                //}

                float maxHeight = 0;
                foreach (var bar in track.Bars)
                {
                    if (bar.Stop.Ticks < startRange.Ticks || bar.Start.Ticks > endRange.Ticks)
                    {
                        continue; // Skip if the bar is outside the range.
                    }

                    long startRelativeTicks = bar.Start.Ticks - startRange.Ticks;
                    long stopRelativeTicks = bar.Stop.Ticks - startRange.Ticks;

                    Vector2 min = new Vector2(startRelativeTicks * tickToPixel, bar.Depth * barHeight) + trackBarsTopLeft;
                    Vector2 max = new Vector2(stopRelativeTicks * tickToPixel + 1, (bar.Depth + 1) * barHeight + 1) + trackBarsTopLeft; // + 1 because the max is apparently exclusive.

                    // If the bottom of the bar is above the top of the content region, or the top of the bar is below
                    // the bottom of the content region, no need to draw since it won't be seen.
                    // if (max.Y >= contentRegionTopLeft.Y && min.Y <= (contentRegionTopLeft.Y + contentRegionAvailable.Y))
                    {
                        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.5f, 0.4f, 0.3f, 1.0f)));
                        drawList.AddText(min + new Vector2(1, 0), ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 1.0f)), bar.Name);
                    }

                    maxHeight = Math.Max(maxHeight, bar.Depth * barHeight);
                    totalBars++;
                }

                foreach (var instantEvent in track.InstantEvents)
                {
                    if (instantEvent.Timestamp.Ticks < startRange.Ticks || instantEvent.Timestamp.Ticks > endRange.Ticks)
                    {
                        continue; // Skip if the event is outside the range.
                    }
                    long relativeTicks = instantEvent.Timestamp.Ticks - startRange.Ticks;
                    Vector2 tickTop = new Vector2(relativeTicks * tickToPixel, instantEvent.Depth * barHeight) + trackBarsTopLeft;

                    drawList.AddTriangleFilled(
                        tickTop + new Vector2(0, tickPadding),
                        tickTop + new Vector2(-4, barHeight - tickPadding * 2),
                        tickTop + new Vector2(0, barHeight - tickPadding * 2 - tickDivit),
                        ImGui.GetColorU32(0xFF9073EF));
                    drawList.AddTriangleFilled(
                        tickTop + new Vector2(0, tickPadding),
                        tickTop + new Vector2(0, barHeight - tickPadding * 2 - tickDivit),
                        tickTop + new Vector2(4, barHeight - tickPadding * 2),
                        ImGui.GetColorU32(0xFF9073EF));

                    maxHeight = Math.Max(maxHeight, (instantEvent.Depth + 1) * barHeight);
                }

                ImGui.Dummy(new Vector2(contentRegionAvailable.X, maxHeight));
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
                        track.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = traceEventTime, Depth = track.StartEvents.Count, Name = name });
                    }
                }
                else
                {
                    track.InstantEvents.Add(new InstantEvent { Timestamp = traceEventTime, Level = traceTable.GetUnifiedLevel(i), Depth = track.StartEvents.Count, Name = name });
                }
            }

            // Add implicit stops for any starts that are still open
            foreach (var trackKeyValue in tracks)
            {
                while (trackKeyValue.Value.StartEvents.TryPop(out Track.StartEvent startEvent))
                {
                    trackKeyValue.Value.Bars.Add(new Bar { Start = startEvent.Timestamp, Stop = endLog, Depth = trackKeyValue.Value.StartEvents.Count, Name = startEvent.Name });
                }
            }

            var computedTracks = new List<ComputedTrack>();
            {
                // Group by process id, ordered so that the most active processes are at the top.
                foreach (var processTracks in tracks.GroupBy(t => t.Key.Item1).OrderByDescending(tg => tg.Sum(tg2 => tg2.Value.Bars.Count)))
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
                            Bars = track.Value.Bars,
                            InstantEvents = track.Value.InstantEvents
                        });
                    }
                }
            }

            Trace.WriteLine($"Computed tracks in {stopwatch.ElapsedMilliseconds}ms");

            return new ComputedTracks { Tracks = computedTracks };
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
