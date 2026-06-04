using Hexa.NET.ImGui;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace InstantTraceViewerUI.Symbols
{
    public class SymbolModule
    {
        IntPtr _dbgHelpSessionHandle;

        internal SymbolModule(IntPtr dbgHelpSessionHandle)
        {
            _dbgHelpSessionHandle = dbgHelpSessionHandle;
        }
    }

    /// <summary>
    /// Resolves module+offset addresses to symbol names using the Windows Debug Help library (dbghelp.dll).
    /// Supports symbol server downloads (e.g. the Microsoft public symbol server) and local symbol/binary stores
    /// via the search path passed to the constructor.
    /// 
    /// All potentially-slow operations (binary lookup, PDB download, module load, symbol resolution) are exposed
    /// as <c>Async</c> methods and their results are cached on the instance, so repeating the same query is cheap.
    ///
    /// Multiple independent instances may exist concurrently; each owns its own caches and dbghelp session, so
    /// loaded modules from one instance do not affect another. Internally calls into dbghelp are serialized with
    /// a process-wide lock because the underlying library is not thread-safe.
    ///
    /// dbghelp symbol options (<c>SYMOPT_*</c>) are process-global. Configure them once per process via
    /// <see cref="SetGlobalSymbolOptions(uint)"/> rather than per instance.
    /// </summary>
    internal sealed class SymbolResolver : IDisposable
    {
        private class RegisteredModuleRevoker : IDisposable
        {
            private ModuleData _moduleData;

            public RegisteredModuleRevoker(ModuleData moduleData)
            {
                _moduleData = moduleData;
            }

            public void Dispose()
            {
                lock (_moduleData)
                {
                    _moduleData.ReferenceCount--;
                    if (_moduleData.ReferenceCount == 0)
                    {
                        // TODO: Unload module or perform cleanup
                    }
                }
            }
        }

        private const int MaxPathBufferLength = 32768;

        public readonly record struct Module(string FileName, ulong SizeOfImage, uint TimeDateStamp);

        private record class ModuleData
        {
            public string? ResolvedBinaryPath { get; set; }

            public StringBuilder ResolvedBinaryPathLog { get; } = new();

            public int ReferenceCount { get; set; }
        }

        private readonly record struct ModuleManagerRow(Module Module, string? ResolvedBinaryPath);

        private sealed class CurrentModuleDataScope : IDisposable
        {
            private readonly ModuleData? _previousModuleData;

            public CurrentModuleDataScope(ModuleData? moduleData)
            {
                _previousModuleData = CurrentModuleData;
                CurrentModuleData = moduleData;
            }

            public void Dispose()
            {
                CurrentModuleData = _previousModuleData;
            }
        }

        private sealed class ModuleDataTraceSink
        {
            public void WriteLine(string message)
            {
                ModuleData? moduleData = CurrentModuleData;
                if (moduleData == null || string.IsNullOrEmpty(message))
                {
                    Trace.WriteLine("[No ModuleData] " + message);
                    return;
                }

                lock (moduleData)
                {
                    moduleData.ResolvedBinaryPathLog.AppendLine(message);
                }
            }
        }

        internal static readonly object DbgHelpLock = new();
        private static readonly ModuleDataTraceSink ResolveModuleTraceSink = new();

        [ThreadStatic]
        private static ModuleData? CurrentModuleData;

        private static long NextHandleValue = 1;

        private readonly DbgHelpSessionHandle _sessionHandle;

        private readonly Dictionary<Module, ModuleData> _binaryLookupCache = new();

        private Module? _selectedSymbolManagerModule;

        /// <summary>
        /// Creates a new resolver with its own dbghelp session and caches.
        /// </summary>
        /// <param name="searchPath">
        /// The dbghelp search path used to locate binaries and PDBs (local directories and/or <c>srv*</c> entries
        /// for symbol servers). See <see cref="CreateDefaultSearchPath"/> for a sensible default.
        /// </param>
        /// <exception cref="InvalidOperationException">Thrown if dbghelp fails to initialize the session.</exception>
        public SymbolResolver(string searchPath)
        {
            ArgumentNullException.ThrowIfNull(searchPath);
            _sessionHandle = new DbgHelpSessionHandle(new IntPtr(Interlocked.Increment(ref NextHandleValue)));

            WriteTraceLine($"SymbolResolver: Initializing with search path '{searchPath}'.");
            using (BeginCurrentModuleDataScope(null))
            {
                lock (DbgHelpLock)
                {
                    if (!PInvoke.SymInitializeW(_sessionHandle, searchPath, fInvadeProcess: false))
                    {
                        throw new InvalidOperationException($"SymInitializeW failed. LastError={Marshal.GetLastPInvokeError()}");
                    }

                    unsafe
                    {
                        if (!PInvoke.SymRegisterCallbackW64(_sessionHandle, &DbgHelpCallback, 0))
                        {
                            WriteTraceLine($"SymbolResolver: SymRegisterCallbackW64 failed. LastError={Marshal.GetLastPInvokeError()}.");
                        }
                    }
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static BOOL DbgHelpCallback(HANDLE hProcess, uint actionCode, ulong callbackData, ulong userContext)
        {
            // TODO: CBA_DEFERRED_SYMBOL_LOAD_START, CBA_DEFERRED_SYMBOL_LOAD_COMPLETE, CBA_DEFERRED_SYMBOL_LOAD_FAILURE, CBA_SRCSRV_EVENT/CBA_SRCSRV_INFO
            if (actionCode == PInvoke.CBA_DEBUG_INFO && callbackData != 0)
            {
                string? message = Marshal.PtrToStringUni((nint)callbackData);
                if (!string.IsNullOrEmpty(message))
                {
                    WriteTraceLine(message.TrimEnd());
                }

                return true;
            }

            return false; // Not handled.
        }

        /// <summary>
        /// Sets the process-global dbghelp symbol options (<c>SYMOPT_*</c>). These options are shared by every
        /// dbghelp session in the process, so this should typically be called once at startup before creating any
        /// <see cref="SymbolResolver"/> instances.
        /// </summary>
        public static void SetGlobalSymbolOptions(uint symbolOptions)
        {
            using (BeginCurrentModuleDataScope(null))
            {
                lock (DbgHelpLock)
                {
                    PInvoke.SymSetOptions(symbolOptions | PInvoke.SYMOPT_DEBUG);
                }
            }
        }

        public static void SetParentWindow(HWND hwnd)
        {
            using (BeginCurrentModuleDataScope(null))
            {
                lock (DbgHelpLock)
                {
                    PInvoke.SymSetParentWindow(hwnd);
                }
            }
        }

        public ResolvedSymbol? Resolve(in Module moduleLookupRequest, ulong relativeVirtualAddress)
        {
            ModuleData moduleData = GetOrAddModuleData(moduleLookupRequest);
            using (BeginCurrentModuleDataScope(moduleData))
            {
                lock (DbgHelpLock)
                {
                    // Need to find binary to get symbol information.
                    string? binaryPath = FindBinary(moduleLookupRequest, findMethod: FindBinaryMethod.Default);
                    if (binaryPath == null)
                    {
                        return null;
                    }

                    // TODO

                    return null;
                }
            }
        }

        public enum FindBinaryMethod
        {
            // Use value from cache if available, otherwise search for it.
            Default,

            // Instant. No disk or network hit.
            CacheOnly,

            // Ignore cache and search again. Useful if previously found binary was changed.
            ForceRefresh,
        }

        public IDisposable RegisterModule(in Module moduleLookupRequest)
        {
            lock (_binaryLookupCache)
            {
                ModuleData value = GetOrAddModuleDataLocked(moduleLookupRequest);
                value.ReferenceCount++;
                return new RegisteredModuleRevoker(value);
            }
        }

        public unsafe string? FindBinary(in Module moduleLookupRequest, FindBinaryMethod findMethod)
        {
            ModuleData? moduleData = TryGetModuleData(moduleLookupRequest);

            // First try the cache.
            if ((findMethod == FindBinaryMethod.CacheOnly || findMethod == FindBinaryMethod.Default) &&
                moduleData != null &&
                !string.IsNullOrEmpty(moduleData.ResolvedBinaryPath))
            {
                return moduleData.ResolvedBinaryPath;
            }

            if (findMethod == FindBinaryMethod.CacheOnly)
            {
                return null;
            }

            moduleData ??= GetOrAddModuleData(moduleLookupRequest);
            using (BeginCurrentModuleDataScope(moduleData))
            {
                // Second try the local filesystem at the exact path specified to avoid potentially slow symbol server hit.
                string? foundBinary = FindBinaryLocal(moduleLookupRequest);
                if (foundBinary == null)
                {
                    Span<char> foundFile = stackalloc char[MaxPathBufferLength];
                    bool found;
                    lock (DbgHelpLock)
                    {
                        found = PInvoke.SymFindFileInPathW(
                            _sessionHandle,
                            null,
                            moduleLookupRequest.FileName, // File part of the path is used.
                            (void*)(nuint)moduleLookupRequest.TimeDateStamp,
                            checked((uint)moduleLookupRequest.SizeOfImage),
                            0,
                            SYM_FIND_ID_OPTION.SSRVOPT_DWORD,
                            foundFile,
                            null,
                            null);
                    }
                    if (!found)
                    {
                        if (Marshal.GetLastPInvokeError() == 2 /* ERROR_NOT_FOUND */)
                        {
                            WriteTraceLine($"SymbolResolver[FindBinary]: '{moduleLookupRequest.FileName}' not found by SymFindFileInPathW.");
                        }
                        else
                        {
                            WriteTraceLine($"SymbolResolver[FindBinary]: Unexpected failure by SymFindFileInPathW. LastError={Marshal.GetLastPInvokeError()}.");
                        }
                        return null;
                    }

                    foundBinary = StringFromNullTerminated(foundFile);
                    WriteTraceLine($"SymbolResolver[FindBinary]: {foundBinary} found via SymFindFileInPathW.");
                }

                lock (_binaryLookupCache)
                {
                    ModuleData value = GetOrAddModuleDataLocked(moduleLookupRequest);
                    value.ResolvedBinaryPath = foundBinary;
                }

                return foundBinary;
            }
        }

        public void RenderSymbolManagerWindow(IUiCommands uiCommands, ref bool isOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("Symbols", ref isOpen))
            {
                List<ModuleManagerRow> modules;
                lock (_binaryLookupCache)
                {
                    modules = new(_binaryLookupCache.Count);
                    foreach ((Module module, ModuleData data) in _binaryLookupCache)
                    {
                        modules.Add(new ModuleManagerRow(module, data.ResolvedBinaryPath));
                    }
                }
                
                modules.Sort((left, right) => string.Compare(left.Module.FileName, right.Module.FileName, StringComparison.OrdinalIgnoreCase));

                if (modules.Count == 0)
                {
                    ImGui.TextUnformatted("No modules registered.");
                }

                float logHeight = ImGui.GetTextLineHeightWithSpacing() * 8;
                Vector2 tableSize = new Vector2(-1, -logHeight - ImGui.GetFrameHeightWithSpacing());
                if (ImGui.BeginTable("SymbolModules", 5,
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                    ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable,
                    tableSize))
                {
                    float dpiBase = ImGui.GetFontSize();

                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Loaded Module", ImGuiTableColumnFlags.WidthStretch, 0.35f);
                    ImGui.TableSetupColumn("TimeDateStamp", ImGuiTableColumnFlags.WidthFixed, dpiBase * 8.0f);
                    ImGui.TableSetupColumn("SizeOfImage", ImGuiTableColumnFlags.WidthFixed, dpiBase * 8.0f);
                    ImGui.TableSetupColumn("Resolved Module", ImGuiTableColumnFlags.WidthStretch, 0.65f);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, dpiBase * 6.0f);
                    ImGui.TableHeadersRow();

                    foreach (ModuleManagerRow row in modules)
                    {
                        Module module = row.Module;
                        ImGui.PushID($"{module.FileName}|{module.TimeDateStamp:X8}|{module.SizeOfImage:X}");

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        bool isSelected = _selectedSymbolManagerModule == module;
                        if (ImGui.Selectable("##TableRow", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                        {
                            _selectedSymbolManagerModule = module;
                        }
                        ImGui.SameLine();
                        ImGui.TextUnformatted(module.FileName);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"0x{module.TimeDateStamp:X8}");

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"0x{module.SizeOfImage:X}");

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(row.ResolvedBinaryPath ?? "");

                        ImGui.TableNextColumn();
                        if (ImGui.Button("Resolve"))
                        {
                            _selectedSymbolManagerModule = module;
                            FindBinary(module, FindBinaryMethod.ForceRefresh);
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                string selectedLog = GetSelectedSymbolManagerModuleLog();
                ImGui.InputTextMultiline(
                    "##SelectedSymbolModuleLog",
                    ref selectedLog,
                    ImGuiWidgets.GetInputTextBufferSize(selectedLog, 1),
                    new Vector2(-1, logHeight),
                    ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
            }

            ImGui.End();
        }

        public void Dispose()
        {
            if (_sessionHandle.IsClosed)
            {
                return;
            }

            using (BeginCurrentModuleDataScope(null))
            {
                lock (DbgHelpLock)
                {
                    // TODO: PInvoke.SymUnloadModule64(_sessionHandle, BaseAddress);
                    // TODO: PInvoke.SymCleanup(_sessionHandle);
                }
            }

            _sessionHandle.Dispose();
        }

        private string? FindBinaryLocal(in Module moduleLookupRequest)
        {
            if (!Path.IsPathFullyQualified(moduleLookupRequest.FileName))
            {
                return null;
            }

            if (!Path.Exists(moduleLookupRequest.FileName))
            {
                WriteTraceLine($"SymbolResolver[FindBinary]: Local file '{moduleLookupRequest.FileName}' does not exist.");
                return null;
            }

            try
            {
                using FileStream fileStream = File.OpenRead(moduleLookupRequest.FileName);
                using PEReader peReader = new(fileStream);
                PEHeader? peHeader = peReader.PEHeaders.PEHeader;
                if (peHeader == null)
                {
                    WriteTraceLine($"SymbolResolver[FindBinary]: Local file '{moduleLookupRequest.FileName}' has no PE header.");
                    return null;
                }

                uint timeDateStamp = unchecked((uint)peReader.PEHeaders.CoffHeader.TimeDateStamp);
                uint sizeOfImage = unchecked((uint)peHeader.SizeOfImage);
                if (timeDateStamp != moduleLookupRequest.TimeDateStamp || sizeOfImage != moduleLookupRequest.SizeOfImage)
                {
                    WriteTraceLine($"SymbolResolver[FindBinary]: Local file '{moduleLookupRequest.FileName}' mismatched. Expected TimeDateStamp=0x{moduleLookupRequest.TimeDateStamp:X8} SizeOfImage=0x{moduleLookupRequest.SizeOfImage:X}; actual TimeDateStamp=0x{timeDateStamp:X8} SizeOfImage=0x{sizeOfImage:X}.");
                    return null;
                }

                WriteTraceLine($"SymbolResolver[FindBinary]: Local file '{moduleLookupRequest.FileName}' found locally with matching TimeDateStamp=0x{moduleLookupRequest.TimeDateStamp:X8} SizeOfImage=0x{moduleLookupRequest.SizeOfImage:X}.");
                return moduleLookupRequest.FileName;
            }
            catch (Exception ex)
            {
                WriteTraceLine($"SymbolResolver[FindBinary]: Failed to open or parse '{moduleLookupRequest.FileName}': {ex.Message}");
                return null;
            }
        }

        private static IDisposable BeginCurrentModuleDataScope(ModuleData? moduleData)
        {
            return new CurrentModuleDataScope(moduleData);
        }

        private static void WriteTraceLine(string message)
        {
            Trace.WriteLine(message);
            ResolveModuleTraceSink.WriteLine(message);
        }

        private ModuleData? TryGetModuleData(in Module moduleLookupRequest)
        {
            lock (_binaryLookupCache)
            {
                return _binaryLookupCache.TryGetValue(moduleLookupRequest, out ModuleData? moduleData) ? moduleData : null;
            }
        }

        private string GetSelectedSymbolManagerModuleLog()
        {
            if (!_selectedSymbolManagerModule.HasValue)
            {
                return string.Empty;
            }

            ModuleData? moduleData = TryGetModuleData(_selectedSymbolManagerModule.Value);
            if (moduleData == null)
            {
                return string.Empty;
            }

            lock (moduleData)
            {
                return moduleData.ResolvedBinaryPathLog.ToString();
            }
        }

        private ModuleData GetOrAddModuleData(in Module moduleLookupRequest)
        {
            lock (_binaryLookupCache)
            {
                return GetOrAddModuleDataLocked(moduleLookupRequest);
            }
        }

        private ModuleData GetOrAddModuleDataLocked(in Module moduleLookupRequest)
        {
            if (!_binaryLookupCache.TryGetValue(moduleLookupRequest, out ModuleData? moduleData))
            {
                moduleData = new();
                _binaryLookupCache.Add(moduleLookupRequest, moduleData);
            }

            return moduleData;
        }

        private static string StringFromNullTerminated(ReadOnlySpan<char> buffer)
        {
            int length = buffer.IndexOf('\0');
            if (length < 0)
            {
                length = buffer.Length;
            }

            return new string(buffer[..length]);
        }

        /// <summary>
        /// Dbghelp uses a pseudo-handle to identify symbol sessions in its APIs.
        /// Since we want to have multiple independent instances of SymbolResolver that don't interfere with each other's caches,
        /// we create a separate session for each instance by using a unique pseudo-handle value.
        /// The actual value of the handle is not important as long as it's unique, so we use an incrementing long value to generate it.
        /// </summary>
        internal sealed class DbgHelpSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public DbgHelpSessionHandle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                return true;
            }
        }
    }
}
