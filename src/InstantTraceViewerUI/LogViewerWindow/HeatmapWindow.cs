using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal class HeatmapWindow
    {
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
                int heatmapPixelWidth = (int)ImGui.GetContentRegionAvail().X;

                if (heatmapPixelWidth > 0 && visibleTraceRecords.Count > 0)
                {
                    if (_colorHeatmap == null || _colorHeatmap.Length != heatmapPixelWidth || _lastVisibleTraceRecordCount != visibleTraceRecords.Count)
                    {
                        ProcessTraceRecords(heatmapPixelWidth, visibleTraceRecords);
                    }

                    Vector2 topLeft = ImGui.GetCursorScreenPos();

                    int barHeight = 30;

                    ImGui.Dummy(new Vector2(heatmapPixelWidth, barHeight));

                    ImDrawListPtr drawList = ImGui.GetWindowDrawList();
                    for (int i = 0; i < _colorHeatmap.Length; i++)
                    {
                        drawList.AddLine(topLeft + new Vector2(i, 0), topLeft + new Vector2(i, barHeight), _colorHeatmap[i]);
                    }

                    // Underline the area that is visible in the log viewer.
                    if (startWindow != null && endWindow != null)
                    {
                        int startWindowX = (int)Math.Floor(PercentFromTimestamp(startWindow.Value) * heatmapPixelWidth);
                        int endWindowX = (int)Math.Floor(PercentFromTimestamp(endWindow.Value) * heatmapPixelWidth);

                        for (int e = 0; e < 5; e++) // Underline bar height
                        {
                            drawList.AddLine(topLeft + new Vector2(startWindowX - e, barHeight + e), topLeft + new Vector2(endWindowX + e + 1, barHeight + e), ImGui.GetColorU32(new Vector4(1, 1, 0, 1)));
                        }
                    }
                }
            }

            ImGui.End();

            return _open;
        }

        private float PercentFromTimestamp(DateTime timestamp) => (float)(timestamp - _startTime).Ticks / (_endTime - _startTime).Ticks;

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
                    color = new Vector4(1, 0.5f, 0, 1);
                }
                else if (errorCounts[i] > 0)
                {
                    color = new Vector4(1, 0, 0, 1);
                }

                _colorHeatmap[i] = ImGui.GetColorU32(color);
            }
        }
    }
}
