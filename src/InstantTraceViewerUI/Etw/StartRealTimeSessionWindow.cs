using Hexa.NET.ImGui;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace InstantTraceViewerUI.Etw
{
    internal class StartRealTimeSessionWindow : IWindow
    {
        private const string WindowName = "Start real-time ETW session";
        private const string ImportOverwritePopupName = "Import WPRP?";
        private const string RecentWprpPopupName = "Recent WPRP files";
        private static int s_nextWindowId;

        private static readonly IReadOnlyList<KernelTraceEventParser.Keywords> KernelKeywords =
            Enum.GetValues<KernelTraceEventParser.Keywords>()
                .Where(keyword => keyword != KernelTraceEventParser.Keywords.None && IsSingleKeyword(keyword))
                .ToList();

        private static readonly IReadOnlyDictionary<KernelTraceEventParser.Keywords, string> KernelKeywordHelpText = new Dictionary<KernelTraceEventParser.Keywords, string>
        {
            { KernelTraceEventParser.Keywords.None, "Logs nothing." },
            { KernelTraceEventParser.Keywords.DiskFileIO, "Logs the mapping of file IDs to actual kernel file names." },
            { KernelTraceEventParser.Keywords.DiskIO, "Loads the completion of physical disk activity." },
            { KernelTraceEventParser.Keywords.ImageLoad, "Logs native module loads, such as LoadLibrary, and unloads." },
            { KernelTraceEventParser.Keywords.MemoryHardFaults, "Logs all page faults that must fetch the data from the disk, also known as hard faults." },
            { KernelTraceEventParser.Keywords.NetworkTCPIP, "Logs TCP/IP network send and receive events." },
            { KernelTraceEventParser.Keywords.Process, "Logs process starts and stops." },
            { KernelTraceEventParser.Keywords.ProcessCounters, "Logs process performance counters. Vista+ only." },
            { KernelTraceEventParser.Keywords.Profile, "Sampled based profiling every millisecond. Vista+ only. Expect about 1K events per process per second." },
            { KernelTraceEventParser.Keywords.Thread, "Logs thread starts and stops." },
            { KernelTraceEventParser.Keywords.ContextSwitch, "Logs thread context switches. Vista only. Can be more than 10K events per second." },
            { KernelTraceEventParser.Keywords.DiskIOInit, "Logs disk operations. Vista+ only. Generally less than 1K events per second. Stacks are associated with this." },
            { KernelTraceEventParser.Keywords.Dispatcher, "Thread dispatcher ReadyThread events. Vista+ only. Can be more than 10K events per second." },
            { KernelTraceEventParser.Keywords.FileIO, "Logs file FileOperationEnd events with status codes when they complete, including operations that do not cause disk I/O. Vista+ only. Generally less than 1K events per second. No stacks are associated with these." },
            { KernelTraceEventParser.Keywords.FileIOInit, "Logs the start of file I/O operations as well as the end. Vista+ only. Generally less than 1K events per second." },
            { KernelTraceEventParser.Keywords.Memory, "Logs all page faults, hard or soft. Can be more than 1K events per second." },
            { KernelTraceEventParser.Keywords.Registry, "Logs activity to the Windows registry. Can be more than 1K events per second." },
            { KernelTraceEventParser.Keywords.SystemCall, "Logs calls to the OS. Vista+ only. Very high volume, and can be more than 100K events per second." },
            { KernelTraceEventParser.Keywords.VirtualAlloc, "Logs VirtualAlloc and VirtualFree calls. Vista+ only. Generally less than 1K events per second." },
            { KernelTraceEventParser.Keywords.VAMap, "Logs mapping of files into memory. Win8 and above only. Generally low volume." },
            { KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls, "Logs Advanced Local Procedure Call events." },
            { KernelTraceEventParser.Keywords.DeferedProcedureCalls, "Logs deferred procedure calls, a kernel mechanism for having work done asynchronously. Vista+ only." },
            { KernelTraceEventParser.Keywords.Driver, "Device driver logging. Vista+ only." },
            { KernelTraceEventParser.Keywords.Interrupt, "Logs hardware interrupts. Vista+ only." },
            { KernelTraceEventParser.Keywords.SplitIO, "Disk I/O that was split, for example because of mirroring requirements. Vista+ only." },
            { KernelTraceEventParser.Keywords.Default, "Good default kernel flags." },
            { KernelTraceEventParser.Keywords.Verbose, "Turns on interesting events that are too verbose for normal use. Does not include SystemCall because it is too verbose." },
            { KernelTraceEventParser.Keywords.ThreadTime, "Use this if you care about blocked time." },
            { KernelTraceEventParser.Keywords.OS, "You mostly do not care about these unless you are dealing with OS internals." },
            { KernelTraceEventParser.Keywords.All, "All legal kernel events." },
            { KernelTraceEventParser.Keywords.NonContainer, "Kernel events that are not allowed in containers. Can be subtracted out." },
            { KernelTraceEventParser.Keywords.PMCProfile, "Turns on PMC, or Precise Machine Counter, events. Win8 only." },
            { KernelTraceEventParser.Keywords.ReferenceSet, "Kernel reference set events, like XPERF ReferenceSet. Fully works only on Win8." },
            { KernelTraceEventParser.Keywords.ThreadPriority, "Events when thread priorities change." },
            { KernelTraceEventParser.Keywords.IOQueue, "Events when queuing and dequeuing from I/O completion ports." },
            { KernelTraceEventParser.Keywords.Handle, "Handle creation and closing, useful for handle leaks." },
        };

        private readonly int _windowId = ++s_nextWindowId;
        private EtwSessionProfile _profile = CreateDefaultProfile();
        private string _newProviderName = "";
        private TraceEventLevel _newProviderLevel = TraceEventLevel.Verbose;
        private string _newProviderKeywords = "0xFFFFFFFFFFFFFFFF";
        private string _pendingImportWprpFile = "";
        private string _errorMessage = "";
        private bool _closeRequested;

        public bool DrawWindow(IUiCommands uiCommands)
        {
            bool isOpen = true;
            ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);
            if (ImGui.Begin($"{WindowName}###{WindowName}_{_windowId}", ref isOpen))
            {
                DrawSessionDetails();
                ImGui.Separator();

                float actionAreaHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
                if (!string.IsNullOrEmpty(_errorMessage))
                {
                    actionAreaHeight += ImGui.GetTextLineHeightWithSpacing();
                }

                if (ImGui.BeginChild("EtwSessionOptionsRegion", new Vector2(0, -actionAreaHeight)))
                {
                    DrawSessionOptionTabs();
                }
                ImGui.EndChild();

                ImGui.Separator();

                DrawActions(uiCommands);
                DrawImportOverwriteConfirmation();
            }

            ImGui.End();

            return isOpen && !_closeRequested;
        }

        private void DrawSessionDetails()
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Display Name:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            string displayName = _profile.DisplayName;
            if (ImGui.InputText("##DisplayName", ref displayName, ImGuiWidgets.GetInputTextBufferSize(displayName, 256)))
            {
                _profile.DisplayName = displayName;
            }
        }

        private void DrawSessionOptionTabs()
        {
            int kernelKeywordCount = KernelKeywords.Count(keyword => _profile.KernelKeywords.HasFlag(keyword));
            if (ImGui.BeginTabBar("EtwSessionOptions"))
            {
                if (ImGui.BeginTabItem($"Providers ({_profile.Providers.Count})###Providers"))
                {
                    DrawProviders();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem($"Kernel keywords ({kernelKeywordCount})###KernelKeywords"))
                {
                    DrawKernelKeywords();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private void DrawProviders()
        {
            Vector2 tableSize = new Vector2(-1, ImGui.GetContentRegionAvail().Y);
            if (ImGui.BeginTable("EtwProviders", 5,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable,
                tableSize))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 8);
                ImGui.TableSetupColumn("Keywords", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 12);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5);
                ImGui.TableHeadersRow();

                for (int i = 0; i < _profile.Providers.Count; i++)
                {
                    ImGui.PushID(i);
                    EtwSessionEnabledProvider provider = _profile.Providers[i];

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(provider.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(string.IsNullOrWhiteSpace(provider.Description) ? "n/a" : provider.Description);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(provider.Level.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"0x{provider.MatchAnyKeyword:X}");

                    ImGui.TableNextColumn();
                    if (ImGui.Button("Remove"))
                    {
                        _profile.Providers.RemoveAt(i);
                        i--;
                    }

                    ImGui.PopID();
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##NewProviderName", "Provider name or GUID", ref _newProviderName, ImGuiWidgets.GetInputTextBufferSize(_newProviderName, 256));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted("n/a");

                ImGui.TableNextColumn();
                DrawLevelCombo("##NewProviderLevel", ref _newProviderLevel);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##NewProviderKeywords", ref _newProviderKeywords, ImGuiWidgets.GetInputTextBufferSize(_newProviderKeywords, 32));

                ImGui.TableNextColumn();
                if (ImGui.Button("Add"))
                {
                    AddProvider();
                }

                ImGui.EndTable();
            }
        }

        private void DrawKernelKeywords()
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(AppTheme.WarningColor));
            ImGui.TextWrapped("Warning: Many kernel events are not yet supported and will be ignored.");
            if (!(TraceEventSession.IsElevated() ?? false))
            {
                ImGui.TextWrapped("Warning: Kernel events require running Instant Trace Viewer as administrator.");
            }
            ImGui.PopStyleColor();

            Vector2 tableSize = new Vector2(-1, ImGui.GetContentRegionAvail().Y);
            if (ImGui.BeginTable("EtwKernelKeywords", 2, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, tableSize))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Keyword", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 12);
                ImGui.TableSetupColumn("Help", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableHeadersRow();

                foreach (KernelTraceEventParser.Keywords keyword in KernelKeywords)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool enabled = _profile.KernelKeywords.HasFlag(keyword);
                    if (ImGui.Checkbox(keyword.ToString(), ref enabled))
                    {
                        if (enabled)
                        {
                            _profile.KernelKeywords |= keyword;
                        }
                        else
                        {
                            _profile.KernelKeywords &= ~keyword;
                        }
                    }

                    ImGui.TableNextColumn();
                    if (KernelKeywordHelpText.TryGetValue(keyword, out string helpText))
                    {
                        ImGui.TextUnformatted(helpText);
                    }
                }

                ImGui.EndTable();
            }
        }

        private void DrawActions(IUiCommands uiCommands)
        {
            if (!string.IsNullOrEmpty(_errorMessage))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(AppTheme.ErrorColor));
                ImGui.TextUnformatted(_errorMessage);
                ImGui.PopStyleColor();
            }

            DrawImportWprpButton();

            ImGui.SameLine();

            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_profile.DisplayName) || !HasConfiguredSessionOptions());
            if (ImGui.Button("Export WPRP..."))
            {
                ExportWprp();
            }
            ImGui.EndDisabled();

            ImGui.SameLine();

            // Make width oversized so the button is more prominent.
            float startButtonWidth = ImGui.GetFontSize() * 6;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - startButtonWidth);

            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_profile.DisplayName) || (_profile.KernelKeywords == KernelTraceEventParser.Keywords.None && _profile.Providers.Count == 0));
            if (ImGui.Button("Start", new Vector2(startButtonWidth, 0)))
            {
                StartSession(uiCommands);
            }
            ImGui.EndDisabled();
        }

        private void DrawImportOverwriteConfirmation()
        {
            if (ImGui.BeginPopup(ImportOverwritePopupName))
            {
                ImGui.TextUnformatted("Importing a .WPRP file will replace the current providers and kernel keywords.");
                ImGui.NewLine();

                if (ImGui.Button("Import"))
                {
                    ImGui.CloseCurrentPopup();
                    ImportRequestedWprp();
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        private void DrawImportWprpButton()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, ImGui.GetStyle().ItemSpacing.Y));

            if (ImGui.Button("Import WPRP..."))
            {
                RequestImportWprp("");
            }

            ImGui.SameLine();
            if (ImGui.ArrowButton("##RecentWprpFiles", ImGuiDir.Down))
            {
                ImGui.OpenPopup(RecentWprpPopupName);
            }

            ImGui.PopStyleVar();

            if (ImGui.BeginPopup(RecentWprpPopupName))
            {
                IReadOnlyList<string> recentWprpFiles = Settings.GetRecentlyOpenedWprp();
                if (recentWprpFiles.Count == 0)
                {
                    ImGui.BeginDisabled();
                    ImGui.MenuItem("No recent WPRP files");
                    ImGui.EndDisabled();
                }

                foreach (string file in recentWprpFiles)
                {
                    if (ImGui.MenuItem(file))
                    {
                        RequestImportWprp(file);
                    }
                }

                ImGui.EndPopup();
            }
        }

        private bool HasConfiguredSessionOptions()
        {
            return _profile.Providers.Count > 0 || _profile.KernelKeywords != KernelTraceEventParser.Keywords.None;
        }

        private void AddProvider()
        {
            _errorMessage = "";

            if (string.IsNullOrWhiteSpace(_newProviderName))
            {
                _errorMessage = "Provider name is required.";
                return;
            }

            if (!TryParseKeywords(_newProviderKeywords, out ulong keywords))
            {
                _errorMessage = "Provider keywords must be a decimal or 0x-prefixed hexadecimal value.";
                return;
            }

            _profile.Providers.Add(new EtwSessionEnabledProvider
            {
                Name = _newProviderName.Trim(),
                Description = "",
                Level = _newProviderLevel,
                MatchAnyKeyword = keywords,
            });
            _newProviderName = "";
            _newProviderKeywords = "0xFFFFFFFFFFFFFFFF";
        }

        private void RequestImportWprp(string file)
        {
            _pendingImportWprpFile = file;

            if (HasConfiguredSessionOptions())
            {
                ImGui.OpenPopup(ImportOverwritePopupName);
            }
            else
            {
                ImportRequestedWprp();
            }
        }

        private void ImportRequestedWprp()
        {
            string file = _pendingImportWprpFile;
            _pendingImportWprpFile = "";

            if (string.IsNullOrEmpty(file))
            {
                file = FileDialog.OpenFile("Windows Performance Recorder Profile Files (*.wprp)|*.wprp", Settings.WprpOpenLocation);
            }

            if (string.IsNullOrEmpty(file))
            {
                return;
            }

            Settings.WprpOpenLocation = Path.GetDirectoryName(file);
            Settings.AddRecentlyOpenedWprp(file);

            try
            {
                Wprp wprp = Wprp.Load(file);
                if (wprp.Profiles.Count == 0)
                {
                    _errorMessage = "The selected .WPRP file does not contain a supported profile.";
                    return;
                }

                _profile = wprp.Profiles[0].ConvertToSessionProfile();
                _errorMessage = "";
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to import .WPRP file: {ex.Message}";
            }
        }

        private void ExportWprp()
        {
            try
            {
                Wprp.SaveToWprp(_profile);
                _errorMessage = "";
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to export .WPRP file: {ex.Message}";
            }
        }

        private void StartSession(IUiCommands uiCommands)
        {
            try
            {
                EtwTraceSource realTimeSession = EtwTraceSource.CreateRealTimeSession(_profile);
                uiCommands.AddWindow(new LogViewerWindow(realTimeSession));
                _closeRequested = true;
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to start ETW session: {ex.Message}";
            }
        }

        private static void DrawLevelCombo(string label, ref TraceEventLevel level)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo(label, level.ToString()))
            {
                foreach (TraceEventLevel candidate in Enum.GetValues<TraceEventLevel>())
                {
                    if (ImGui.Selectable(candidate.ToString(), level == candidate))
                    {
                        level = candidate;
                    }
                }
                ImGui.EndCombo();
            }
        }

        private static bool TryParseKeywords(string text, out ulong keywords)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ulong.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out keywords);
            }

            return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out keywords);
        }

        private static bool IsSingleKeyword(KernelTraceEventParser.Keywords keyword)
        {
            int value = (int)keyword;
            return value != 0 && (value & (value - 1)) == 0;
        }

        private static EtwSessionProfile CreateDefaultProfile()
        {
            return new EtwSessionProfile { DisplayName = "Real-time ETW" };
        }

        public void Dispose()
        {
        }
    }
}
