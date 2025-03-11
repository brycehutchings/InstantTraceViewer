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
            public int Level;
            public string Name;
        }

        class Track
        {
            //
            // Processing State:
            //

            // TODO: Needs name too. Maybe row index too?
            public Stack<DateTime> StartTimes = new Stack<DateTime>();

            //
            // Output:
            //

            public List<Bar> Bars = new();
        }

        struct ComputedTrack
        {
            public int ProcessId;
            public int ThreadId;
            public List<Bar> Bars;
        }

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

        private Task<List<ComputedTrack>> _computedTracks = null;
        private DateTime? _startRange = null;
        private DateTime? _endRange = null;

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
        }

        private void DrawTracks(DateTime startLog, DateTime endLog)
        {
            Vector2 contentRegionAvailable = ImGui.GetContentRegionAvail();

            Vector2 tracksTopLeft = ImGui.GetCursorPos();
            Vector2 tracksTopLeftScreenPos = ImGui.GetCursorScreenPos();

            // Fun/hacky thing here to cover the entire window with an invisible item (button) so that it can capture the mouse wheel Y
            // when CTRL is held down.
            float zoomAmount = 0;
            {
                ImGui.InvisibleButton("###Reserved", contentRegionAvailable);
                if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
                {
                    ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);
                    if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup))
                    {
                        zoomAmount = ImGui.GetIO().MouseWheel; // Positive = zoom in. Negative = zoom out.
                    }
                }
                ImGui.SetCursorPos(tracksTopLeft);
            }

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();

            DateTime startRange = (_startRange.HasValue && _startRange.Value >= startLog) ? _startRange.Value : startLog;
            DateTime endRange = (_endRange.HasValue && _endRange.Value <= endLog) ? _endRange.Value : endLog;

            TimeSpan rangeDuration = endRange - startRange;

            if (zoomAmount != 0)
            {
                float percentZoomPerWheelClick = 0.05f; // 5% zoom per wheel click.

                // Adjust zoom to the left/right of the mouse cursor so that the zoom is centered around the mouse cursor.
                float zoomPointPercent = (ImGui.GetMousePos().X - tracksTopLeftScreenPos.X) / contentRegionAvailable.X;

                // Zoom in proportionally to the mouse position to maintain the position of what is under the mouse.
                TimeSpan adjustDuration = rangeDuration * percentZoomPerWheelClick * zoomAmount;
                _startRange = startRange + adjustDuration * zoomPointPercent;
                _endRange = endRange - adjustDuration * (1 - zoomPointPercent);
            }

            uint textColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            float barHeight = ImGui.GetTextLineHeight();

            float tickToPixel = contentRegionAvailable.X / rangeDuration.Ticks;
            int totalBars = 0;
            Debug.Assert(_computedTracks.IsCompletedSuccessfully);
            foreach (var track in _computedTracks.Result)
            {
                ImGui.TextUnformatted(track.ThreadId > 0 ? $"{track.ProcessId}:{track.ThreadId}" : track.ProcessId.ToString());

                Vector2 trackBarsTopLeft = ImGui.GetCursorScreenPos();

                // This optimization might not help
                //if (trackBarsTopLeft.Y > (contentRegionTopLeft.Y + contentRegionAvailable.Y))
                //{
                //    break; // We are below the fold and won't be seen.
                //}

                int maxLevel = 0;
                foreach (var bar in track.Bars)
                {
                    if (bar.Stop.Ticks < startRange.Ticks || bar.Start.Ticks > endRange.Ticks)
                    {
                        continue; // Skip if the bar is outside the range.
                    }

                    long startRelativeTicks = bar.Start.Ticks - startRange.Ticks;
                    long stopRelativeTicks = bar.Stop.Ticks - startRange.Ticks;

                    Vector2 min = new Vector2(startRelativeTicks * tickToPixel, bar.Level * barHeight) + trackBarsTopLeft;
                    Vector2 max = new Vector2(stopRelativeTicks * tickToPixel + 1, (bar.Level + 1) * barHeight + 1) + trackBarsTopLeft; // + 1 because the max is apparently exclusive.

                    // If the bottom of the bar is above the top of the content region, or the top of the bar is below
                    // the bottom of the content region, no need to draw since it won't be seen.
                    // if (max.Y >= contentRegionTopLeft.Y && min.Y <= (contentRegionTopLeft.Y + contentRegionAvailable.Y))
                    {
                        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.5f, 0.4f, 0.3f, 1.0f)));
                        drawList.AddText(min + new Vector2(1, 0), ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 1.0f)), bar.Name);
                    }

                    maxLevel = Math.Max(maxLevel, bar.Level);
                    totalBars++;
                }

                float totalBarHeight = (maxLevel + 1) * barHeight;
                ImGui.Dummy(new Vector2(contentRegionAvailable.X, totalBarHeight));
            }

            // ImGui.GetContentRegionAvail()
            // ImGui.GetCursorScreenPos();
            // ImGui.Dummy
            // ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            // drawList.AddRectFilled
            // drawList.AddLine
            // ImGui.GetColorU32...
            // ImGui.CalcTextSize(startTimeOffsetStr);
            // ImGui.SetCursorPos
            // ImGui.TextUnformatted

            //if ((startX < 0 && eventX < 0) || (startX > contentRegionAvailable.X && eventX > contentRegionAvailable.X))
            //{
            //    continue; // Skip if both start and end are outside the range.
            //}
        }

        private static List<ComputedTrack> ComputeTracks(ITraceTableSnapshot traceTable, DateTime endLog)
        {
            var stopwatch = Stopwatch.StartNew();

            // Key=pid, tid.
            Dictionary<(int, int), Track> tracks = new();

            for (int i = 0; i < traceTable.RowCount; i++)
            {
                (int, int) trackKey = (traceTable.GetProcessId(i), traceTable.GetThreadId(i));
                if (!tracks.TryGetValue(trackKey, out Track? track))
                {
                    track = new Track();
                    tracks.Add(trackKey, track);
                }

                DateTime traceEventTime = traceTable.GetTimestamp(i);
                UnifiedOpcode opcode = traceTable.GetUnifiedOpcode(i);
                string name = traceTable.GetName(i);

                if (opcode == UnifiedOpcode.Start)
                {
                    // Push the start time onto the stack.
                    track.StartTimes.Push(traceEventTime);
                }
                else if (opcode == UnifiedOpcode.Stop)
                {
                    // TODO: Verify StartTime is for the same Stop by Name. If it isn't... idk
                    // Pop until we find a match, or leave alone if no match.
                    if (track.StartTimes.TryPop(out DateTime startTime))
                    {
                        track.Bars.Add(new Bar { Start = startTime, Stop = traceEventTime, Level = track.StartTimes.Count, Name = name });
                    }
                }
                else
                {
                    // Instant event.
                }
            }

            // Add implicit stops for any starts that are still open
            foreach (var trackKeyValue in tracks)
            {
                DateTime startTime;
                while (trackKeyValue.Value.StartTimes.TryPop(out startTime))
                {
                    trackKeyValue.Value.Bars.Add(new Bar { Start = startTime, Stop = endLog, Level = trackKeyValue.Value.StartTimes.Count, Name = "TODO" });
                }
            }

            var computedTracks = new List<ComputedTrack>();
            {
                // Group by process id, ordered so that the most active processes are at the top.
                foreach (var processTracks in tracks.GroupBy(t => t.Key.Item1).OrderByDescending(tg => tg.Sum(tg2 => tg2.Value.Bars.Count)))
                {
                    // Next order the tracks for the process so that the most active threads are at the top.
                    foreach (var track in processTracks.OrderByDescending(t => t.Value.Bars.Count))
                    {
                        // TODO: Once there are instant events then this needs to be removed or updated.
                        if (track.Value.Bars.Count == 0)
                        {
                            continue;
                        }

                        computedTracks.Add(new ComputedTrack { ProcessId = track.Key.Item1, ThreadId = track.Key.Item2, Bars = track.Value.Bars });
                    }
                }
            }

            Trace.WriteLine($"Computed tracks in {stopwatch.ElapsedMilliseconds}ms");

            return computedTracks;
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
