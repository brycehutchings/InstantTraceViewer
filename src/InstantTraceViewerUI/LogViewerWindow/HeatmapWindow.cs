using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal class HeatmapWindow
    {
        private const int PixelsPerSection = 2; // 1 pixel thick lines are too hard to see.
        private const int UnderlineHeight = 5;

        private static int _nextWindowId = 1;
        private const string PopupName = "Heatmap";

        private readonly string _name;
        private readonly int _windowId;

        private int _lastVisibleTraceRecordCount = 0;
        private uint[] _colorHeatmap;
        private DateTime _startTime;
        private DateTime _endTime;
        private bool _open = true;

        public HeatmapWindow(string name)
        {
            _name = name;
            _windowId = _nextWindowId++;
        }

        public bool DrawWindow(IUiCommands uiCommands, FilteredTraceRecordCollection visibleTraceRecords, DateTime? startWindow, DateTime? endWindow)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 70), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(100, 70), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{PopupName} - {_name}###Heatmap_{_windowId}", ref _open))
            {
                DrawHeatmapGraph(visibleTraceRecords, startWindow, endWindow);
            }

            ImGui.End();

            return _open;
        }

        public void DrawHeatmapGraph(FilteredTraceRecordCollection visibleTraceRecords, DateTime? startWindow, DateTime? endWindow)
        {
            int sectionCount = (int)ImGui.GetContentRegionAvail().X / PixelsPerSection;

            if (sectionCount <= 0 || visibleTraceRecords.Count == 0)
            {
                return;
            }

            if (_colorHeatmap == null || _colorHeatmap.Length != sectionCount || _lastVisibleTraceRecordCount != visibleTraceRecords.Count)
            {
                ProcessTraceRecords(sectionCount, visibleTraceRecords);
            }

            Vector2 topLeft = ImGui.GetCursorScreenPos();

            int barHeight = 30;
            int heatmapPixelWidth = sectionCount * PixelsPerSection;

            ImGui.Dummy(new Vector2(heatmapPixelWidth, barHeight + UnderlineHeight));

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            for (int i = 0; i < _colorHeatmap.Length; i++)
            {
                drawList.AddRectFilled(topLeft + new Vector2(i * PixelsPerSection, 0), topLeft + new Vector2((i + 1) * PixelsPerSection, barHeight), _colorHeatmap[i]);
            }

            // Underline the area that is visible in the log viewer.
            if (startWindow != null && endWindow != null)
            {
                int startSectionIndex = SectionIndexFromTimestamp(startWindow.Value);
                int endSectionIndex = SectionIndexFromTimestamp(endWindow.Value);

                int startX = 0;
                int barWiden = 0;
                for (int e = 0; e < UnderlineHeight; e++)
                {
                    startX = startSectionIndex * PixelsPerSection - barWiden;
                    int endX = endSectionIndex * PixelsPerSection + barWiden + 1;
                    drawList.AddLine(topLeft + new Vector2(startX, barHeight + e), topLeft + new Vector2(endX, barHeight + e), ImGui.GetColorU32(new Vector4(1, 1, 1, 1)));
                    if (endX - startX < UnderlineHeight * 2)
                    {
                        barWiden++; // Bar widens out like a trapezoid but stops once it is at least as wide as it is thick.
                    }
                }

                string startTimeOffsetStr = (startWindow.Value - visibleTraceRecords.First().Timestamp).ToString("'+'mm':'ss'.'ffffff");
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

        private int SectionIndexFromTimestamp(DateTime timestamp) => (int)((timestamp - _startTime).Ticks / ((_endTime - _startTime).Ticks / _colorHeatmap.Length));

        private void ProcessTraceRecords(int sectionCount, FilteredTraceRecordCollection visibleTraceRecords)
        {
            _startTime = visibleTraceRecords.First().Timestamp;
            _endTime = visibleTraceRecords.Last().Timestamp;

            int[] errorCounts = new int[sectionCount];
            int[] warningCounts = new int[sectionCount];
            int[] verboseCounts = new int[sectionCount];
            int[] otherCounts = new int[sectionCount];

            long ticksPerSection = (_endTime - _startTime).Ticks / sectionCount;

            if (ticksPerSection == 0) // Avoid div-by-zero.
            {
                ticksPerSection = 1;
            }

            //var sw = Stopwatch.StartNew();
            for (int i = 0; i < visibleTraceRecords.Count; i++)
            {
                TraceRecord traceRecord = visibleTraceRecords[i];
                int sectionIndex = (int)((traceRecord.Timestamp - _startTime).Ticks / ticksPerSection);

                // Due to rounding errors the sectionIndex can go too high. Protect against too low in case there is a rogue event that is not in chronological order.
                sectionIndex = Math.Clamp(sectionIndex, 0, sectionCount - 1);

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
                else
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

            _colorHeatmap = new uint[sectionCount];
            _lastVisibleTraceRecordCount = visibleTraceRecords.Count;

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

                _colorHeatmap[i] = ImGui.GetColorU32(color);
            }
        }
    }
}
