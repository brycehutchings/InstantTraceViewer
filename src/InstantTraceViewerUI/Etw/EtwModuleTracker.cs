using System;
using System.Collections.Generic;

namespace InstantTraceViewerUI.Etw
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
    public class EtwModuleTracker
    {
        private Dictionary<int /* pid */, List<LoadedImage>> _loadedImages = new();

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
