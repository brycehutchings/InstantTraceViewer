using Hexa.NET.ImGui;
using InstantTraceViewerUI.Symbols;
using System;
using System.Collections.Generic;
using System.Linq;
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
    internal class EtwModuleTracker // TODO: Implement IDisposable and have EtwModuleTracker call it.
    {
        private Dictionary<int /* pid */, List<LoadedImage>> _loadedImages = new();

        // The largest image size ever observed across all processes. Used as an upper bound to early-exit the backward scan in GetLoadedImage.
        private ulong _maxImageSize;

        // Raised after symbols are successfully loaded for a module so consumers can re-resolve existing stack frames.
        public event Action? SymbolsLoaded;

        // Height of the resizable diagnostic log region at the bottom of the symbol manager window. Initialized lazily and then
        // owned by the ResizeY child window; we read it back each frame so the table above fills the remaining space (i.e. resizing
        // the window grows/shrinks the table while the log height stays put).
        private float _logHeight;

        // Modules selected (by symbol key) in the manager window for batch operations like loading several at once.
        private readonly HashSet<SymbolKey> _selectedSymbolKeys = new();

        // Substring filter applied to the module list. Empty shows everything.
        private string _moduleFilter = string.Empty;

        // Pid whose modules are shown in the manager window. -1 means all processes (no process filter).
        private int _processFilterPid = -1;

        // Search text typed into the process filter dropdown to narrow the list of processes shown.
        private string _processFilterSearch = string.Empty;

        // Shows a blocking modal while symbols load on a background thread so the slow dbghelp/network work doesn't stall the UI.
        private readonly ImGuiWidgets.ProcessingModal _processingModal = new();

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
        }

        public void RenderSymbolManagerWindow(IUiCommands uiCommands, IReadOnlyDictionary<int, string> processNames, ref bool isOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("Symbols", ref isOpen))
            {
                // Loaded symbols are tied to SymbolKeys, so we only want to show one row per SymbolKey.
                // The modules are needed when trying to load the symbol (SymLoadModuleExW takes both the module name and pdb filename, so in
                // theory the module filename can affect searching.
                Dictionary<SymbolKey, HashSet<SymbolResolver.Module>> loadedModulesForDisplayMap = new();

                // TODO: Recompute only when a module is added/removed.
                lock (_loadedImages)
                {
                    foreach ((var pid, var images) in _loadedImages)
                    {
                        if (_processFilterPid != -1 && pid != _processFilterPid)
                        {
                            continue;
                        }

                        foreach (var image in images)
                        {
                            if (!string.IsNullOrEmpty(_moduleFilter) && !image.FileName.Contains(_moduleFilter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (loadedModulesForDisplayMap.TryGetValue(image.RegisteredModule.Key, out var modules))
                            {
                                modules.Add(image.RegisteredModule.Module);
                            }
                            else
                            {
                                loadedModulesForDisplayMap.Add(image.RegisteredModule.Key, [image.RegisteredModule.Module]);
                            }
                        }
                    }
                }

                var loadedModulesForDisplay = loadedModulesForDisplayMap.OrderBy(lm => lm.Value.First().FileName).ToList();

                // Toolbar: batch-load symbols for the selected modules, clear the diagnostic log, and filter the list.
                ImGui.BeginDisabled(_selectedSymbolKeys.Count == 0 || _processingModal.IsRunning);
                if (ImGui.Button("Load Selected Modules"))
                {
                    // Snapshot the selected, not-yet-loaded modules so the worker thread never touches ImGui state.
                    List<(SymbolKey Key, SymbolResolver.Module Module)> toLoad = new();
                    foreach (var loadedImage in loadedModulesForDisplay)
                    {
                        if (_selectedSymbolKeys.Contains(loadedImage.Key) &&
                            string.IsNullOrEmpty(SymbolResolver.Instance.GetPdbPath(loadedImage.Key)))
                        {
                            foreach (var module in loadedImage.Value)
                            {
                                toLoad.Add((loadedImage.Key, module));
                            }
                        }
                    }

                    _processingModal.Start("Loading symbols...", (progress, cancellationToken) =>
                    {
                        bool anyLoaded = false;
                        for (int i = 0; i < toLoad.Count; i++)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            var (key, module) = toLoad[i];
                            progress.Report((float)i / toLoad.Count, $"Loading {System.IO.Path.GetFileName(module.FileName)}...");
                            if (SymbolResolver.Instance.TryLoadSymbols(key, module))
                            {
                                anyLoaded = true;
                            }
                        }

                        if (anyLoaded)
                        {
                            SymbolsLoaded?.Invoke();
                        }
                    });
                }
                ImGui.EndDisabled();

                string diagnosticLog = SymbolResolver.Instance.GetDiagnosticLog();
                if (!string.IsNullOrEmpty(diagnosticLog))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Clear Log"))
                    {
                        SymbolResolver.Instance.ClearDiagnosticLog();
                    }
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
                string processComboPreview = _processFilterPid == -1 ? "All processes" : FormatProcessLabel(_processFilterPid, processNames);
                if (ImGui.BeginCombo("##ProcessFilter", processComboPreview))
                {
                    // Focus the search box the moment the popup opens so the user can type to filter right away.
                    if (ImGui.IsWindowAppearing())
                    {
                        _processFilterSearch = string.Empty;
                        ImGui.SetKeyboardFocusHere();
                    }
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##ProcessFilterSearch", "Search process...", ref _processFilterSearch, ImGuiWidgets.GetInputTextBufferSize(_processFilterSearch, 256));

                    bool hasProcessSearch = !string.IsNullOrEmpty(_processFilterSearch);
                    if (!hasProcessSearch && ImGui.Selectable("All processes", _processFilterPid == -1))
                    {
                        _processFilterPid = -1;
                    }

                    foreach (int pid in _loadedImages.Keys.OrderBy(pid => GetProcessName(pid, processNames), StringComparer.OrdinalIgnoreCase).ThenBy(pid => pid))
                    {
                        string label = FormatProcessLabel(pid, processNames);
                        if (hasProcessSearch && !label.Contains(_processFilterSearch, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        bool isSelected = pid == _processFilterPid;
                        if (ImGui.Selectable(label, isSelected))
                        {
                            _processFilterPid = pid;
                        }
                        if (isSelected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }

                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##ModuleFilter", "Filter modules...", ref _moduleFilter, ImGuiWidgets.GetInputTextBufferSize(_moduleFilter, 256), ImGuiInputTextFlags.AutoSelectAll);

                if (loadedModulesForDisplayMap.Count == 0)
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
                    ImGui.TableSetupColumn("Module", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableSetupColumn("Symbols?", ImGuiTableColumnFlags.WidthFixed, dpiBase * 4);
                    ImGui.TableHeadersRow();

                    var multiselectIO = ImGui.BeginMultiSelect(ImGuiMultiSelectFlags.ClearOnEscape | ImGuiMultiSelectFlags.BoxSelect2D);
                    var applyMultiselectRequests = () =>
                    {
                        for (int reqIdx = 0; reqIdx < multiselectIO.Requests.Size; reqIdx++)
                        {
                            var req = multiselectIO.Requests[reqIdx];

                            long startIndex;
                            long endIndex;
                            if (req.Type == ImGuiSelectionRequestType.SetAll)
                            {
                                startIndex = 0;
                                endIndex = loadedModulesForDisplayMap.Count - 1;
                            }
                            else
                            {
                                // RangeLastItem can be less than RangeFirstItem with RangeDirection = -1. We don't care about order so ignore direction.
                                startIndex = Math.Min(req.RangeFirstItem, req.RangeLastItem);
                                endIndex = Math.Max(req.RangeFirstItem, req.RangeLastItem);
                            }

                            for (long i = startIndex; i <= endIndex; i++)
                            {
                                SymbolKey key = loadedModulesForDisplay[(int)i].Key;
                                if (req.Selected != 0)
                                {
                                    _selectedSymbolKeys.Add(key);
                                }
                                else
                                {
                                    _selectedSymbolKeys.Remove(key);
                                }
                            }
                        }
                    };
                    applyMultiselectRequests();

                    int rowIndex = 0;
                    foreach (var registeredModule in loadedModulesForDisplay)
                    {
                        foreach (var module in registeredModule.Value)
                        {
                            ImGui.PushID(HashCode.Combine(registeredModule.Key, module));

                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();

                            bool selected = _selectedSymbolKeys.Contains(registeredModule.Key);
                            ImGui.SetNextItemSelectionUserData(rowIndex++);
                            ImGui.Selectable(module.FileName, selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap);

                            string? pdbPath = SymbolResolver.Instance.GetPdbPath(registeredModule.Key);

                            if (ImGui.IsItemHovered())
                            {
                                ImGui.BeginTooltip();
                                ImGui.TextUnformatted(module.FileName);

                                ImGui.Separator();
                                string timeDateStampFormatted = module.TimeDateStamp == 0 ? "n/a" : $"0x{module.TimeDateStamp:X8}";
                                ImGui.TextUnformatted($"Image Size: {module.SizeOfImage}\nTimeDateStamp: {timeDateStampFormatted}");

                                ImGui.Separator();
                                string pdbSigFormatted = module.PdbSig == Guid.Empty ? "n/a" : module.PdbSig.ToString("D");
                                string pdbAgeFormatted = module.PdbAge == 0 ? "n/a" : $"{module.PdbAge}";
                                string pdbFileNameFormatted = string.IsNullOrEmpty(module.PdbFileName) ? "n/a" : module.PdbFileName;
                                ImGui.TextUnformatted($"Pdb Signature: {pdbSigFormatted}\nPdb Age: {pdbAgeFormatted}\nOriginal Pdb FileName: {pdbFileNameFormatted}");

                                ImGui.Separator();
                                ImGui.TextUnformatted($"Pdb: {pdbPath ?? "<not loaded>"}");

                                ImGui.EndTooltip();
                            }

                            ImGui.TableNextColumn();

                            if (pdbPath != null)
                            {
                                ImGui.TextUnformatted("\uF058"); // "circle-check"
                            }

                            ImGui.PopID();
                        }
                    }

                    ImGui.EndMultiSelect();
                    applyMultiselectRequests();

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

                string selectedLog = SymbolResolver.Instance.GetDiagnosticLog();
                ImGui.InputTextMultiline(
                    "##SelectedSymbolModuleLog",
                    ref selectedLog,
                    ImGuiWidgets.GetInputTextBufferSize(selectedLog, 1),
                    new Vector2(-1, -1),
                    ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
            }


            _processingModal.Draw(uiCommands);

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

        private static string GetProcessName(int pid, IReadOnlyDictionary<int, string> processNames)
            => processNames != null && processNames.TryGetValue(pid, out string name) && !string.IsNullOrEmpty(name)
                ? name
                : string.Empty;

        private static string FormatProcessLabel(int pid, IReadOnlyDictionary<int, string> processNames)
        {
            string name = GetProcessName(pid, processNames);
            return string.IsNullOrEmpty(name) ? pid.ToString() : $"{pid} ({name})";
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
