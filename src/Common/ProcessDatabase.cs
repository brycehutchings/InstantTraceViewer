using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace InstantTraceViewer
{
    public readonly record struct LoadedImage(string FileName, ulong ImageBase, ulong ImageSize, uint TimeDateStamp, uint CheckSum, DateTime LoadTime, DateTime? UnloadTime)
    {
        public ulong ImageEnd => ImageBase + ImageSize;
    }

    /// <summary>
    /// Tracks process/thread names and loaded images.
    /// This class expects timestamp data happens chronologically but also allows for retroactive changes like a thread name for an already-known tid to be assigned.
    /// When this happens the caller is instructed to bump the generation id so that any filtering can be re-evaluated with the new information.
    /// This class is very carefully designed so that the same timestamp never maps to a different process/thread when new start/name events are observed.
    /// This is important because filtering that runs as events stream in won't know to re-evaluate unless told to by bumping the generation id.
    /// </summary>
    public class ProcessDatabase
    {
        record struct TrackedName
        {
            public string Name;
            public DateTime Start;
        }

        // Value is a List because PIDs may be reused.
        private Dictionary<int /* pid */, List<TrackedName>> _trackedProcesses = new();

        // Value is a List because TIDs may be reused.
        // This is kept outside of the process tracking because ETW thread start events may be observed before process datacollectionstart events.
        // PID is not used in the key because some events exclude PID and only include TID.
        private Dictionary<int /* tid */, List<TrackedName>> _trackedThreads = new();

        private Dictionary<int /* pid */, List<LoadedImage>> _loadedImages = new();

        public string? GetProcessName(int pid, DateTime timestamp)
        {
            lock (_trackedProcesses)
            {
                if (_trackedProcesses.TryGetValue(pid, out var processes))
                {
                    return GetName(processes, timestamp);
                }
            }

            return null;
        }

        public void SetProcessName(int pid, string name, DateTime startTime)
        {
            lock (_trackedProcesses)
            {
                if (!_trackedProcesses.TryGetValue(pid, out var processes))
                {
                    processes = new List<TrackedName>();
                    _trackedProcesses[pid] = processes;
                }

                processes.Add(new TrackedName { Name = name, Start = startTime });
            }
        }

        public string? GetThreadName(int tid, DateTime timestamp)
        {
            lock (_trackedThreads)
            {
                if (_trackedThreads.TryGetValue(tid, out var threads))
                {
                    return GetName(threads, timestamp);
                }
            }

            return null;
        }

        public void SetThreadName(int tid, string name, DateTime startTime)
        {
            lock (_trackedThreads)
            {
                if (!_trackedThreads.TryGetValue(tid, out var threads))
                {
                    threads = new List<TrackedName>();
                    _trackedThreads[tid] = threads;
                }

                threads.Add(new TrackedName { Name = name, Start = startTime });
            }
        }

        // Entries are appended in chronological order, so the list is sorted ascending by Start.
        // The matching entry is the last one whose Start is <= timestamp (its end is the next entry's Start).
        private static string? GetName(List<TrackedName> entries, DateTime timestamp)
        {
            if (entries.Count == 0)
            {
                return null;
            }

            // Fast path: the timestamp is at or after the most recent entry, which is the common case for streaming events.
            if (entries[^1].Start <= timestamp)
            {
                return entries[^1].Name;
            }

            // Binary search for the rightmost entry whose Start is <= timestamp.
            int low = 0;
            int high = entries.Count - 1;
            int match = -1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (entries[middle].Start <= timestamp)
                {
                    match = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return match >= 0 ? entries[match].Name : null;
        }

        public void ImageLoad(int pid, string fileName, ulong imageBase, ulong imageSize, uint timeDateStamp, uint checkSum, DateTime loadTime)
        {
            lock (_loadedImages)
            {
                if (!_loadedImages.TryGetValue(pid, out var loadedImages))
                {
                    loadedImages = new List<LoadedImage>();
                    _loadedImages[pid] = loadedImages;
                }

                int insertIndex = FindFirstImageWithBaseGreaterThan(loadedImages, imageBase);
                loadedImages.Insert(insertIndex, new LoadedImage(fileName, imageBase, imageSize, timeDateStamp, checkSum, loadTime, null));
            }
        }

        public void ImageUnload(int pid, ulong imageBase, DateTime unloadTime)
        {
            lock (_loadedImages)
            {
                if (_loadedImages.TryGetValue(pid, out var loadedImages))
                {
                    int endIndex = FindFirstImageWithBaseGreaterThan(loadedImages, imageBase);
                    if (endIndex > 0)
                    {
                        int candidateIndex = endIndex - 1;
                        var loadedImage = loadedImages[candidateIndex];
                        if (loadedImage.ImageBase == imageBase && loadedImage.UnloadTime == null)
                        {
                            loadedImages[candidateIndex] = loadedImage with { UnloadTime = unloadTime };
                        }
                    }
                }
            }
        }

        public LoadedImage? GetLoadedImage(int pid, ulong virtualAddress, DateTime timestamp)
        {
            lock (_loadedImages)
            {
                if (_loadedImages.TryGetValue(pid, out var loadedImages))
                {
                    int endIndex = FindFirstImageWithBaseGreaterThan(loadedImages, virtualAddress);
                    for (int i = endIndex - 1; i >= 0; i--)
                    {
                        var loadedImage = loadedImages[i];
                        if ((loadedImage.ImageBase <= virtualAddress && virtualAddress < loadedImage.ImageEnd) &&
                            loadedImage.LoadTime <= timestamp && (loadedImage.UnloadTime == null || loadedImage.UnloadTime >= timestamp))
                        {
                            return loadedImage;
                        }
                    }
                }
            }

            return null;
        }

        private static int FindFirstImageWithBaseGreaterThan(List<LoadedImage> loadedImages, ulong imageBase)
        {
            int low = 0;
            int high = loadedImages.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (loadedImages[middle].ImageBase <= imageBase)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

    }
}
