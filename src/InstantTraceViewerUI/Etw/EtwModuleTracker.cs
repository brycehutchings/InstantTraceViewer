using Hexa.NET.ImGui;
using InstantTraceViewerUI.Symbols;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace InstantTraceViewerUI.Etw
{
    internal readonly record struct LoadedImage(string FileName, ulong ImageBase, ulong ImageSize, uint TimeDateStamp, uint CheckSum, string PdbFileName, int PdbAge, Guid PdbSig, RegisteredModule RegisteredModule, DateTime LoadTime, DateTime? UnloadTime)
    {
        public ulong ImageEnd => ImageBase + ImageSize;
    }

    /// <summary>
    /// Tracks loaded image metadata and their lifetimes for lookup. This is useful for resolving an instruction pointer at a point in time to a module+offset.
    /// </summary>
    internal class EtwModuleTracker
    {
        private Dictionary<int /* pid */, List<LoadedImage>> _loadedImages = new();

        // The largest image size ever observed across all processes. Used as an upper bound to early-exit the backward scan in GetLoadedImage.
        private ulong _maxImageSize;

        // Symbol handles for the modules tracked here, used by the symbol manager window. Disposed via ClearRegisteredModules.
        private readonly List<RegisteredModule> _registeredModules = new();
        private RegisteredModule? _selectedDiagnosticLog;

        // Raised after symbols are successfully loaded for a module so consumers can re-resolve existing stack frames.
        public event Action? SymbolsLoaded;

        // Height of the resizable diagnostic log region at the bottom of the symbol manager window. Initialized lazily and then
        // owned by the ResizeY child window; we read it back each frame so the table above fills the remaining space (i.e. resizing
        // the window grows/shrinks the table while the log height stays put).
        private float _logHeight;

        public void ImageLoad(int pid, string fileName, ulong imageBase, ulong imageSize, uint timeDateStamp, uint checkSum, string pdbFileName, int pdbAge, Guid pdbSig, DateTime loadTime, RegisteredModule registeredModule)
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
                loadedImages.Insert(insertIndex, new LoadedImage(fileName, imageBase, imageSize, timeDateStamp, checkSum, pdbFileName, pdbAge, pdbSig, registeredModule, loadTime, null));
            }

            lock (_registeredModules)
            {
                _registeredModules.Add(registeredModule);
            }
        }

        public void ClearRegisteredModules()
        {
            lock (_registeredModules)
            {
                foreach (var registeredModule in _registeredModules)
                {
                    registeredModule.Dispose();
                }
                _registeredModules.Clear();
                _selectedDiagnosticLog = null;
            }
        }

        public void RenderSymbolManagerWindow(IUiCommands uiCommands, ref bool isOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("Symbols", ref isOpen))
            {
                // FIXME: Don't take across whole render?
                lock (_registeredModules)
                {
                    // Modules are presented per loaded module for easy searching/sorting. Multiple loaded modules may share the
                    // same underlying symbol data, so loading symbols for one may populate others with the same key.
                    List<RegisteredModule> modules = new(_registeredModules.Count);
                    HashSet<SymbolResolver.Module> seenModules = new();
                    foreach (var registeredModule in _registeredModules)
                    {
                        if (seenModules.Add(registeredModule.Module))
                        {
                            modules.Add(registeredModule);
                        }
                    }
                    modules.Sort((left, right) => string.Compare(left.Module.FileName, right.Module.FileName, StringComparison.OrdinalIgnoreCase));

                    if (modules.Count == 0)
                    {
                        ImGui.TextUnformatted("No modules registered.");
                    }

                    if (_logHeight <= 0)
                    {
                        _logHeight = ImGui.GetTextLineHeightWithSpacing() * 8;
                    }

                    float spacing = ImGui.GetStyle().ItemSpacing.Y;
                    float splitterThickness = ImGui.GetFontSize() * 0.25f;
                    float availY = ImGui.GetContentRegionAvail().Y;

                    // Keep both regions usable: the log can shrink to one line but never squeeze the table out entirely.
                    float minRegion = ImGui.GetTextLineHeightWithSpacing();
                    _logHeight = Math.Clamp(_logHeight, minRegion, Math.Max(minRegion, availY - splitterThickness - spacing * 2 - minRegion));

                    // Table fills whatever space the log and splitter don't, so growing the window grows the table.
                    Vector2 tableSize = new Vector2(-1, availY - _logHeight - splitterThickness - spacing * 2);
                    if (ImGui.BeginTable("SymbolModules", 2,
                        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                        ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable,
                        tableSize))
                    {
                        float dpiBase = ImGui.GetFontSize();

                        ImGui.TableSetupScrollFreeze(0, 1);
                        ImGui.TableSetupColumn("Loaded Module", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                        ImGui.TableSetupColumn("Symbol", ImGuiTableColumnFlags.WidthFixed, dpiBase * 1.0f);
                        ImGui.TableHeadersRow();

                        foreach (var registeredModule in modules)
                        {
                            SymbolResolver.Module module = registeredModule.Module;
                            ImGui.PushID(HashCode.Combine(registeredModule.Key, module));

                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();

                            if (ImGui.CollapsingHeader(module.FileName))
                            {
                                string timeDateStampFormatted = module.TimeDateStamp == 0 ? "n/a" : $"0x{module.TimeDateStamp:X8}";
                                ImGui.Indent();
                                ImGui.TextUnformatted($"SizeOfImage: {module.SizeOfImage} TimeDateStamp: {timeDateStampFormatted}");
                                string pdbSigFormatted = module.PdbSig == Guid.Empty ? "n/a" : module.PdbSig.ToString("D");
                                string pdbAgeFormatted = module.PdbAge == 0 ? "n/a" : $"{module.PdbAge}";
                                string pdbFileNameFormatted = string.IsNullOrEmpty(module.PdbFileName) ? "n/a" : module.PdbFileName;
                                ImGui.TextUnformatted($"PdbSig: {pdbSigFormatted} PdbAge: {pdbAgeFormatted} PdbFileName: {pdbFileNameFormatted}");
                                string? pdbPath = SymbolResolver.Instance.GetPdbPath(registeredModule.Key);
                                string resolvedBinaryFormatted = string.IsNullOrEmpty(pdbPath) ? "n/a" : pdbPath;
                                ImGui.TextUnformatted($"Pdb: {resolvedBinaryFormatted}");
                                ImGui.Unindent();
                            }

                            ImGui.TableNextColumn();

                            if (string.IsNullOrEmpty(SymbolResolver.Instance.GetPdbPath(registeredModule.Key)))
                            {
                                if (ImGui.Button("Find Symbols"))
                                {
                                    if (SymbolResolver.Instance.TryLoadSymbols(registeredModule.Key, registeredModule.Module))
                                    {
                                        SymbolsLoaded?.Invoke();
                                    }
                                    _selectedDiagnosticLog = registeredModule;
                                }
                            }
                            else
                            {
                                // TODO: Render success icon
                            }

                            if (SymbolResolver.Instance.HasDiagnosticLog(registeredModule.Key))
                            {
                                ImGui.SameLine();
                                if (ImGui.Button("Show Logs"))
                                {
                                    _selectedDiagnosticLog = registeredModule;
                                }
                            }

                            ImGui.PopID();
                        }

                        ImGui.EndTable();
                    }

                    // Splitter handle at the seam between the table and log. Dragging it trades space between the two; window
                    // resizes are absorbed by the table because its height is derived from the available space minus the log.
                    ImGui.InvisibleButton("##SymbolLogSplitter", new Vector2(-1, splitterThickness));
                    if (ImGui.IsItemActive())
                    {
                        _logHeight -= ImGui.GetIO().MouseDelta.Y;
                    }
                    if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
                    }
                    Vector2 splitterMin = ImGui.GetItemRectMin();
                    Vector2 splitterMax = ImGui.GetItemRectMax();
                    float splitterLineY = (splitterMin.Y + splitterMax.Y) * 0.5f;
                    uint splitterColor = ImGui.GetColorU32(ImGui.IsItemActive() || ImGui.IsItemHovered() ? ImGuiCol.SeparatorActive : ImGuiCol.Separator);
                    ImGui.GetWindowDrawList().AddLine(new Vector2(splitterMin.X, splitterLineY), new Vector2(splitterMax.X, splitterLineY), splitterColor, 1.0f);

                    string selectedLog = _selectedDiagnosticLog != null ? SymbolResolver.Instance.GetDiagnosticLog(_selectedDiagnosticLog.Key) : string.Empty;
                    ImGui.InputTextMultiline(
                        "##SelectedSymbolModuleLog",
                        ref selectedLog,
                        ImGuiWidgets.GetInputTextBufferSize(selectedLog, 1),
                        new Vector2(-1, -1),
                        ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
                }
            }

            ImGui.End();
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
