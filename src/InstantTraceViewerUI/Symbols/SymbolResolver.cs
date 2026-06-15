using Hexa.NET.ImGui;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public class SymbolKey
    {
    }

    public abstract class RegisteredModule : IDisposable
    {
        public abstract SymbolKey Key { get; init; }

        public abstract void Dispose();
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
        private class SymbolKeyPdbSig : SymbolKey
        {
            public Guid PdbSig;
            public int PdbAge;

            public SymbolKeyPdbSig(Guid pdbSig, int pdbAge)
            {
                PdbSig = pdbSig;
                PdbAge = pdbAge;
            }

            public override int GetHashCode() => HashCode.Combine(PdbSig, PdbAge);

            public override bool Equals(object? obj)
                => obj is SymbolKeyPdbSig other && PdbSig == other.PdbSig && PdbAge == other.PdbAge;
        }

        private class SymbolKeyPESig : SymbolKey
        {
            public string FileName;
            public ulong SizeOfImage;
            public uint TimeDateStamp;

            public SymbolKeyPESig(string fileName, ulong sizeOfImage, uint timeDateStamp)
            {
                FileName = fileName;
                SizeOfImage = sizeOfImage;
                TimeDateStamp = timeDateStamp;
            }

            public override int GetHashCode() => HashCode.Combine(FileName, SizeOfImage, TimeDateStamp);

            public override bool Equals(object? obj)
                => obj is SymbolKeyPESig other &&
                        string.Equals(FileName, other.FileName, StringComparison.OrdinalIgnoreCase) &&
                        SizeOfImage == other.SizeOfImage &&
                        TimeDateStamp == other.TimeDateStamp;
        }

        private class RegisteredModuleRevoker : RegisteredModule
        {
            public override SymbolKey Key { get; init; }
            public SymbolData ModuleData { get; init; }

            public override void Dispose()
            {
                lock (ModuleData)
                {
                    ModuleData.ReferenceCount--;
                    if (ModuleData.ReferenceCount == 0)
                    {
                        // TODO: Unload module or perform cleanup
                    }
                }
            }
        }

        private const int MaxPathBufferLength = 32768;

        public readonly record struct ModuleKey(string FileName, ulong SizeOfImage, uint TimeDateStamp);

        public readonly record struct Module(string FileName, ulong SizeOfImage, uint TimeDateStamp, string PdbFileName, int PdbAge, Guid PdbSig);

        private record class SymbolData
        {
            public HashSet<Module> Modules { get; } = new();

            // public string? ResolvedBinaryPath { get; set; }

            public string? PdbPath { get; set; }

            public StringBuilder DiagnosticLog { get; } = new();

            public int ReferenceCount { get; set; }
        }

        private sealed class CurrentSymbolDataScope : IDisposable
        {
            private readonly SymbolData? _previousSymbolData;

            public CurrentSymbolDataScope(SymbolData? symbolData)
            {
                _previousSymbolData = CurrentSymbolData;
                CurrentSymbolData = symbolData;
            }

            public void Dispose()
            {
                CurrentSymbolData = _previousSymbolData;
            }
        }

        private sealed class SymbolDataTraceSink
        {
            public void WriteLine(string message)
            {
                SymbolData? symbolData = CurrentSymbolData;
                if (symbolData == null || string.IsNullOrEmpty(message))
                {
                    Trace.WriteLine("[No SymbolData] " + message);
                    return;
                }

                lock (symbolData)
                {
                    symbolData.DiagnosticLog.AppendLine(message);
                }
            }
        }

        internal static readonly object DbgHelpLock = new();
        private static readonly SymbolDataTraceSink ResolveModuleTraceSink = new();

        [ThreadStatic]
        private static SymbolData? CurrentSymbolData;

        private static long NextHandleValue = 1;

        private readonly DbgHelpSessionHandle _sessionHandle;

        // SymbolKey can be computed from a Module.
        private readonly Dictionary<SymbolKey, SymbolData> _symbolCache = new();

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

        public enum FindBinaryMethod
        {
            // Use value from cache if available, otherwise search for it.
            Default,

            // Instant. No disk or network hit.
            CacheOnly,

            // Ignore cache and search again. Useful if previously found binary was changed.
            ForceRefresh,
        }

        public RegisteredModule RegisterModule(in Module module)
        {
            lock (_symbolCache)
            {
                SymbolKey symbolKey = (module.PdbSig == Guid.Empty) ? 
                    new SymbolKeyPESig(module.FileName, module.SizeOfImage, module.TimeDateStamp) :
                    new SymbolKeyPdbSig(module.PdbSig, module.PdbAge);

                if (!_symbolCache.TryGetValue(symbolKey, out SymbolData? moduleData))
                {
                    moduleData = new();
                    _symbolCache.Add(symbolKey, moduleData);
                }

                moduleData.Modules.Add(module);
                moduleData.ReferenceCount++;
                return new RegisteredModuleRevoker { Key = symbolKey, ModuleData = moduleData };
            }
        }

#if false
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

        private static string StringFromNullTerminated(ReadOnlySpan<char> buffer)
        {
            int length = buffer.IndexOf('\0');
            if (length < 0)
            {
                length = buffer.Length;
            }

            return new string(buffer[..length]);
        }
#endif

        private SymbolKey? _selectedDiagnosticLogKey = null;

        public void RenderSymbolManagerWindow(IUiCommands uiCommands, ref bool isOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("Symbols", ref isOpen))
            {
                // FIXME: Don't take across whole render?
                lock (_symbolCache)
                {
                    // Although we group things by SymbolKey, we present things to user per module for easy searching/sorting.
                    // That does mean loading symbols for one module may populate symbol information for other modules with same key.
                    List<(SymbolKey Key, SymbolData Data, Module Module)> symbolDataList = new(_symbolCache.Count);
                    foreach ((SymbolKey key, SymbolData data) in _symbolCache)
                    {
                        foreach (var module in data.Modules)
                        {
                            symbolDataList.Add((key, data, module));
                        }
                    }
                    symbolDataList.Sort((left, right) => string.Compare(left.Module.FileName, right.Module.FileName, StringComparison.OrdinalIgnoreCase));

                    if (symbolDataList.Count == 0)
                    {
                        ImGui.TextUnformatted("No modules registered.");
                    }

                    float logHeight = ImGui.GetTextLineHeightWithSpacing() * 8;
                    Vector2 tableSize = new Vector2(-1, -logHeight - ImGui.GetFrameHeightWithSpacing());
                    if (ImGui.BeginTable("SymbolModules", 2,
                        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                        ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable,
                        tableSize))
                    {
                        float dpiBase = ImGui.GetFontSize();

                        ImGui.TableSetupScrollFreeze(0, 1);
                        ImGui.TableSetupColumn("Loaded Module", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                        //ImGui.TableSetupColumn("TimeDateStamp", ImGuiTableColumnFlags.WidthFixed, dpiBase * 8.0f);
                        //ImGui.TableSetupColumn("SizeOfImage", ImGuiTableColumnFlags.WidthFixed, dpiBase * 8.0f);
                        //ImGui.TableSetupColumn("Resolved Module", ImGuiTableColumnFlags.WidthStretch, 0.65f);
                        ImGui.TableSetupColumn("Symbol", ImGuiTableColumnFlags.WidthFixed, dpiBase * 1.0f);
                        ImGui.TableHeadersRow();

                        foreach (var symbolData in symbolDataList)
                        {
                            ImGui.PushID(HashCode.Combine(symbolData.Key, symbolData.Module));

                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();

                            if (ImGui.CollapsingHeader(symbolData.Module.FileName))
                            {
                                string timeDateStampFormatted = symbolData.Module.TimeDateStamp == 0 ? "n/a" : $"0x{symbolData.Module.TimeDateStamp:X8}";
                                ImGui.Indent();
                                ImGui.TextUnformatted($"SizeOfImage: {symbolData.Module.SizeOfImage} TimeDateStamp: {timeDateStampFormatted}");
                                string pdbSigFormatted = symbolData.Module.PdbSig == Guid.Empty ? "n/a" : symbolData.Module.PdbSig.ToString("D");
                                string pdbAgeFormatted = symbolData.Module.PdbAge == 0 ? "n/a" : $"{symbolData.Module.PdbAge}";
                                string pdbFileNameFormatted = string.IsNullOrEmpty(symbolData.Module.PdbFileName) ? "n/a" : symbolData.Module.PdbFileName;
                                ImGui.TextUnformatted($"PdbSig: {pdbSigFormatted} PdbAge: {pdbAgeFormatted} PdbFileName: {pdbFileNameFormatted}");
                                string resolvedBinaryFormatted = string.IsNullOrEmpty(symbolData.Data.PdbPath) ? "n/a" : symbolData.Data.PdbPath;
                                ImGui.TextUnformatted($"Pdb: {resolvedBinaryFormatted}");
                                ImGui.Unindent();

                                //ImGui.TableNextColumn();
                                //ImGui.TextUnformatted(row.ResolvedBinaryPath ?? "");
                            }

                            ImGui.TableNextColumn();

                            if (string.IsNullOrEmpty(symbolData.Data.PdbPath))
                            {
                                if (ImGui.Button("Find Symbols"))
                                {
                                    FindPdb(symbolData.Module, symbolData.Data);
                                    _selectedDiagnosticLogKey = symbolData.Key;
                                }
                            }
                            else
                            {
                                // TODO: Render success icon
                            }

                            if (symbolData.Data.DiagnosticLog.Length > 0)
                            {
                                ImGui.SameLine();
                                if (ImGui.Button("Show Logs"))
                                {
                                    _selectedDiagnosticLogKey = symbolData.Key;
                                }
                            }

                            ImGui.PopID();
                        }

                        ImGui.EndTable();
                    }

                    string selectedLog = string.Empty;
                    if (_selectedDiagnosticLogKey != null && _symbolCache.TryGetValue(_selectedDiagnosticLogKey, out SymbolData? selectedSymbolData))
                    {
                        selectedLog = selectedSymbolData.DiagnosticLog.ToString();
                    }
                    ImGui.InputTextMultiline(
                        "##SelectedSymbolModuleLog",
                        ref selectedLog,
                        ImGuiWidgets.GetInputTextBufferSize(selectedLog, 1),
                        new Vector2(-1, logHeight),
                        ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
                }
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
                WriteTraceLine($"[FindBinary]: Local file '{moduleLookupRequest.FileName}' does not exist.");
                return null;
            }

            try
            {
                using FileStream fileStream = File.OpenRead(moduleLookupRequest.FileName);
                using PEReader peReader = new(fileStream);
                PEHeader? peHeader = peReader.PEHeaders.PEHeader;
                if (peHeader == null)
                {
                    WriteTraceLine($"[FindBinary]: Local file '{moduleLookupRequest.FileName}' has no PE header.");
                    return null;
                }

                uint timeDateStamp = unchecked((uint)peReader.PEHeaders.CoffHeader.TimeDateStamp);
                uint sizeOfImage = unchecked((uint)peHeader.SizeOfImage);
                if (timeDateStamp != moduleLookupRequest.TimeDateStamp || sizeOfImage != moduleLookupRequest.SizeOfImage)
                {
                    WriteTraceLine($"[FindBinary]: Local file '{moduleLookupRequest.FileName}' mismatched. Expected TimeDateStamp=0x{moduleLookupRequest.TimeDateStamp:X8} SizeOfImage=0x{moduleLookupRequest.SizeOfImage:X}; actual TimeDateStamp=0x{timeDateStamp:X8} SizeOfImage=0x{sizeOfImage:X}.");
                    return null;
                }

                WriteTraceLine($"[FindBinary]: Local file '{moduleLookupRequest.FileName}' found locally with matching TimeDateStamp=0x{moduleLookupRequest.TimeDateStamp:X8} SizeOfImage=0x{moduleLookupRequest.SizeOfImage:X}.");
                return moduleLookupRequest.FileName;
            }
            catch (Exception ex)
            {
                WriteTraceLine($"[FindBinary]: Failed to open or parse '{moduleLookupRequest.FileName}': {ex.Message}");
                return null;
            }
        }

        private void FindPdb(Module module, SymbolData symbolData)
        {
            using (BeginCurrentModuleDataScope(symbolData))
            {
                if (module.PdbSig != Guid.Empty && !string.IsNullOrEmpty(module.PdbFileName))
                {
                    WriteTraceLine($"[FindPdb]: Searching for PDB using PdbSig={module.PdbSig} PdbAge={module.PdbAge} PdbFileName='{module.PdbFileName}'.");

                    if (Path.Exists(module.PdbFileName))
                    {
                        // TODO: Use SymSrvGetFileIndexInfoW to check guid and age. If match, use this path and skip search.
                        // Docs say SymFindFileInPathW strips and path from the FileName (3rd) argument. So if the PDB is local but not in the search path, presumably SymFindFileInPathW won't find it.
                    }

                    unsafe
                    {
                        Span<char> foundFile = stackalloc char[MaxPathBufferLength];
                        Guid pdbSig = module.PdbSig;
                        lock (DbgHelpLock)
                        {
                            bool found = PInvoke.SymFindFileInPathW(
                                _sessionHandle,
                                null,
                                module.PdbFileName,
                                &pdbSig,
                                (uint)module.PdbAge,
                                0,
                                SYM_FIND_ID_OPTION.SSRVOPT_GUIDPTR,
                                foundFile,
                                null,
                                null);

                            if (!found)
                            {
                                WriteTraceLine($"[FindPdb]: PDB lookup failed. LastError={Marshal.GetLastPInvokeError()}.");
                                return;
                            }
                        }

                        string pdbPath = StringFromNullTerminated(foundFile);
                        WriteTraceLine($"[FindPdb]: Found at '{pdbPath}'.");
                        symbolData.PdbPath = pdbPath;
                    }
                }
                else
                {
                    // In theory we can use SymFindFileInPathW to find the module using TimeDateStamp+SizeOfImage, and then get the PdbSig+PdbAge+PdbFileName. Not sure it is needed yet.
                    WriteTraceLine($"[FindPdb]: Searching for PDB based on module TimeDateStamp and SizeOfImage is not yet supported.");
                }
            }
        }

        private static IDisposable BeginCurrentModuleDataScope(SymbolData? moduleData)
        {
            return new CurrentSymbolDataScope(moduleData);
        }

        private static void WriteTraceLine(string message)
        {
            Trace.WriteLine(message);
            ResolveModuleTraceSink.WriteLine(message);
        }

#if false
        private SymbolData? TryGetModuleData(in Module moduleLookupRequest)
        {
            lock (_binaryLookupCache)
            {
                return _binaryLookupCache.TryGetValue(moduleLookupRequest, out SymbolData? moduleData) ? moduleData : null;
            }
        }

        private SymbolData GetOrAddModuleData(in Module module)
        {
            lock (_binaryLookupCache)
            {
                return GetOrAddModuleDataLocked(module);
            }
        }

        private SymbolData GetOrAddModuleDataLocked(in Module module)
        {
        }
#endif

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
