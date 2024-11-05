using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace InstantTraceViewerUI
{
    internal class TimelineWindow
    {
        private const int PixelsPerSection = 2; // 1 pixel thick lines are too hard to see.
        private const int UnderlineHeight = 5;

        private static int _nextWindowId = 1;
        private const string PopupName = "Timeline";

        private readonly string _name;
        private readonly int _windowId;

        private bool _open = true;

        // The data that is computed on a background thread and rendered on the UI thread.
        class ComputedTimeline
        {
            public int LastFilteredTraceRecordCount;
            public uint[] ColorsBars;
            public DateTime StartTime;
            public DateTime EndTime;
        }

        private ComputedTimeline _computedTimeline;
        private Task<ComputedTimeline> _nextComputedTimelineTask;

        public TimelineWindow(string name)
        {
            _name = name;
            _windowId = _nextWindowId++;
        }

        public bool DrawWindow(IUiCommands uiCommands, FilteredTraceRecordCollectionView visibleTraceRecords, DateTime? startWindow, DateTime? endWindow)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 70), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(100, 70), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{PopupName} - {_name}###Timeline_{_windowId}", ref _open))
            {
                DrawTimelineGraph(visibleTraceRecords, startWindow, endWindow);
            }

            ImGui.End();

            return _open;
        }

        public void DrawTimelineGraph(FilteredTraceRecordCollectionView visibleTraceRecords, DateTime? startWindow, DateTime? endWindow)
        {
            int sectionCount = (int)ImGui.GetContentRegionAvail().X / PixelsPerSection;
            if (sectionCount <= 0 || visibleTraceRecords.Count == 0 || visibleTraceRecords.UnfilteredSnapshot.Schema.TimestampColumn == null )
            {
                return;
            }

            // If the background processing of the timeline is complete, update to use it.
            if (_nextComputedTimelineTask != null && _nextComputedTimelineTask.IsCompleted)
            {
                _computedTimeline = _nextComputedTimelineTask.Result;
                _nextComputedTimelineTask = null;
            }

            ComputedTimeline computedTimelineSnapshot = _computedTimeline;

            // If the background processing of the timeline is not running and the snapshot is out of date, start a new background task.
            if (_nextComputedTimelineTask == null)
            {
                if (computedTimelineSnapshot == null || computedTimelineSnapshot.ColorsBars.Length != sectionCount || computedTimelineSnapshot.LastFilteredTraceRecordCount != visibleTraceRecords.Count)
                {
                    _nextComputedTimelineTask = Task.Run(() => ProcessTraceRecords(sectionCount, visibleTraceRecords));
                }
            }

            if (computedTimelineSnapshot == null)
            {
                return;
            }

            Vector2 topLeft = ImGui.GetCursorScreenPos();

            int barHeight = 30;
            int timelinePixelWidth = sectionCount * PixelsPerSection;

            ImGui.Dummy(new Vector2(timelinePixelWidth, barHeight + UnderlineHeight));

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            for (int i = 0; i < computedTimelineSnapshot.ColorsBars.Length; i++)
            {
                drawList.AddRectFilled(topLeft + new Vector2(i * PixelsPerSection, 0), topLeft + new Vector2((i + 1) * PixelsPerSection, barHeight), computedTimelineSnapshot.ColorsBars[i]);
            }

            // Underline the area that is visible in the log viewer.
            if (startWindow != null && endWindow != null)
            {
                int startSectionIndex = SectionIndexFromTimestamp(startWindow.Value);
                int endSectionIndex = SectionIndexFromTimestamp(endWindow.Value);

                uint underlineColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
                int startX = 0;
                int barWiden = 0;
                for (int e = 0; e < UnderlineHeight; e++)
                {
                    startX = startSectionIndex * PixelsPerSection - barWiden;
                    int endX = (endSectionIndex + 1) * PixelsPerSection + barWiden;
                    drawList.AddLine(topLeft + new Vector2(startX, barHeight + e), topLeft + new Vector2(endX, barHeight + e), underlineColor);
                    if (endX - startX < UnderlineHeight * 2)
                    {
                        barWiden++; // Bar widens out like a trapezoid but stops once it is at least as wide as it is thick.
                    }
                }

                // Render text showing the time offset from the start of the visible trace records.
                string startTimeOffsetStr = GetSmartDurationString(startWindow.Value - visibleTraceRecords.UnfilteredSnapshot.GetTimestamp(visibleTraceRecords.First()));
                float startTimeOffsetStrWidth = ImGui.CalcTextSize(startTimeOffsetStr).X;
                float barWidth = ImGui.GetContentRegionAvail().X;
                Vector2 cursorPos = ImGui.GetCursorPos();
                if (cursorPos.X + startX + startTimeOffsetStrWidth > barWidth)
                {
                    ImGui.SetCursorPos(new Vector2(cursorPos.X + barWidth - startTimeOffsetStrWidth, cursorPos.Y - 5));
                }
                else
                {
                    ImGui.SetCursorPos(cursorPos + new Vector2(startX, -5));
                }
                ImGui.Text(startTimeOffsetStr);
            }
        }

        private int SectionIndexFromTimestamp(DateTime timestamp)
        {
            long ticksPerBar = (_computedTimeline.EndTime - _computedTimeline.StartTime).Ticks / _computedTimeline.ColorsBars.Length;
            return ticksPerBar == 0 ? 0 : (int)((timestamp - _computedTimeline.StartTime).Ticks / ticksPerBar);
        }

        static private ComputedTimeline ProcessTraceRecords(int sectionCount, FilteredTraceRecordCollectionView visibleTraceRecords)
        {
            ComputedTimeline newComputedTimeline = new();
            newComputedTimeline.StartTime = visibleTraceRecords.UnfilteredSnapshot.GetTimestamp(visibleTraceRecords.First());
            newComputedTimeline.EndTime = visibleTraceRecords.UnfilteredSnapshot.GetTimestamp(visibleTraceRecords.Last());

            int[] errorCounts = new int[sectionCount];
            int[] warningCounts = new int[sectionCount];
            int[] verboseCounts = new int[sectionCount];
            int[] otherCounts = new int[sectionCount];

            long ticksPerSection = (newComputedTimeline.EndTime - newComputedTimeline.StartTime).Ticks / sectionCount;

            if (ticksPerSection == 0) // Avoid div-by-zero.
            {
                ticksPerSection = 1;
            }

            //var sw = Stopwatch.StartNew();
            for (int i = 0; i < visibleTraceRecords.Count; i++)
            {
                int rowIndex = visibleTraceRecords[i];
                int sectionIndex = (int)((visibleTraceRecords.UnfilteredSnapshot.GetTimestamp(rowIndex) - newComputedTimeline.StartTime).Ticks / ticksPerSection);

                // Due to rounding errors the sectionIndex can go too high. Protect against too low in case there is a rogue event that is not in chronological order.
                sectionIndex = Math.Clamp(sectionIndex, 0, sectionCount - 1);

                /*
                if (traceRecord.Level == TraceLevel.Error || traceRecord.Level == TraceLevel.Critical)
                {
                    errorCounts[sectionIndex]++;
                }
                else if (traceRecord.Level == TraceLevel.Warning)
                {
                    warningCounts[sectionIndex]++;
                }
                else if (traceRecord.Level == TraceLevel.Verbose)
                {
                    verboseCounts[sectionIndex]++;
                }
                else*/
                {
                    otherCounts[sectionIndex]++;
                }
            }
            //Trace.WriteLine($"Processed {visibleTraceRecords.Count} records in {sw.ElapsedMilliseconds}ms");
            // Currently processes about 83.4k records per millisecond on my PC.

            int maxErrors = errorCounts.Max();
            int maxWarnings = warningCounts.Max();
            int maxVerboses = verboseCounts.Max();
            int maxOthers = otherCounts.Max();
            int maxNonError = Math.Max(maxVerboses, maxOthers);

            newComputedTimeline.ColorsBars = new uint[sectionCount];
            newComputedTimeline.LastFilteredTraceRecordCount = visibleTraceRecords.Count;

            for (int i = 0; i < sectionCount; i++)
            {
                Vector4 color = Vector4.Zero;

                if (errorCounts[i] == 0 && warningCounts[i] == 0 && otherCounts[i] == 0 && verboseCounts[i] > 0)
                {
                    // Only verbose has intensity range [0.2 to 0.4]. Near-black is avoided so even a single verbose event is noticeable.
                    float channelIntensity = ((float)verboseCounts[i] / maxNonError) * 0.2f + 0.2f;
                    color = new Vector4(channelIntensity, channelIntensity, channelIntensity, 1);
                }
                else if (errorCounts[i] == 0 && warningCounts[i] == 0 && otherCounts[i] > 0)
                {
                    // Info has intensity range [0.6 to 1.0]
                    float channelIntensity = ((float)otherCounts[i] / maxNonError) * 0.4f + 0.6f;
                    color = new Vector4(channelIntensity, channelIntensity, channelIntensity, 1);
                }
                else if (errorCounts[i] == 0 && warningCounts[i] > 0)
                {
                    float channelIntensity = ((float)warningCounts[i] / maxWarnings) * 0.5f + 0.5f; // 0.5 to 1.0
                    color = new Vector4(1 * channelIntensity, 0.5f * channelIntensity, 0, 1);
                }
                else if (errorCounts[i] > 0)
                {
                    float channelIntensity = ((float)errorCounts[i] / maxErrors) * 0.5f + 0.5f; // 0.5 to 1.0
                    color = new Vector4(1 * channelIntensity, 0, 0, 1);
                }

                newComputedTimeline.ColorsBars[i] = ImGui.GetColorU32(color);
            }

            return newComputedTimeline;
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
