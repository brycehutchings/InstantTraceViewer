using System;
using System.Collections.Generic;

namespace InstantTraceViewerUI.Etw
{
    public readonly record struct LoadedImage(string FileName, ulong ImageBase, ulong ImageSize, uint TimeDateStamp, uint CheckSum, string PdbFileName, int PdbAge, Guid PdbSig, DateTime LoadTime, DateTime? UnloadTime)
    {
        public ulong ImageEnd => ImageBase + ImageSize;
    }

    /// <summary>
    /// Tracks loaded image metadata and their lifetimes for lookup. This is useful for resolving an instruction pointer at a point in time to a module+offset.
    /// </summary>
    public class EtwModuleTracker
    {
        private Dictionary<int /* pid */, List<LoadedImage>> _loadedImages = new();

        // The largest image size ever observed across all processes. Used as an upper bound to early-exit the backward scan in GetLoadedImage.
        private ulong _maxImageSize;

        public void ImageLoad(int pid, string fileName, ulong imageBase, ulong imageSize, uint timeDateStamp, uint checkSum, string pdbFileName, int pdbAge, Guid pdbSig, DateTime loadTime)
        {
            lock (_loadedImages)
            {
                if (!_loadedImages.TryGetValue(pid, out var loadedImages))
                {
                    loadedImages = new List<LoadedImage>();
                    _loadedImages[pid] = loadedImages;
                }

                _maxImageSize = Math.Max(_maxImageSize, imageSize);

                int insertIndex = FindFirstImageWithBaseGreaterThan(loadedImages, imageBase);
                loadedImages.Insert(insertIndex, new LoadedImage(fileName, imageBase, imageSize, timeDateStamp, checkSum, pdbFileName, pdbAge, pdbSig, loadTime, null));
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

                        // Images are sorted by ImageBase. Scanning backwards, the offset (virtualAddress - ImageBase) only grows. Once that
                        // offset reaches the largest image size we've ever seen, no earlier (lower-based) image can possibly contain the address.
                        if (virtualAddress - loadedImage.ImageBase >= _maxImageSize)
                        {
                            break;
                        }

                        if ((loadedImage.ImageBase <= virtualAddress && virtualAddress < loadedImage.ImageEnd) &&
                            loadedImage.LoadTime <= timestamp &&
                            (loadedImage.UnloadTime == null || loadedImage.UnloadTime >= timestamp))
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
