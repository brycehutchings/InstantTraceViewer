using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    internal abstract class RegisteredModule : IDisposable
    {
        public abstract SymbolKey Key { get; init; }

        public abstract SymbolResolver.Module Module { get; init; }

        public abstract void Dispose();
    }

    /// <summary>
    /// Resolves module+offset addresses to symbol names using the Windows Debug Help library (dbghelp.dll).
    /// Supports symbol server downloads (e.g. the Microsoft public symbol server) and local symbol/binary stores
    /// via the search path passed to <see cref="Initialize(uint, string)"/>.
    /// </summary>
    internal sealed class SymbolResolver
    {
        private abstract class SymbolData : SymbolKey
        {
            public HashSet<Module> Modules { get; } = new();

            // public string? ResolvedBinaryPath { get; set; }

            public string? PdbPath { get; set; }

            public StringBuilder DiagnosticLog { get; } = new();

            public int ReferenceCount { get; set; }
        }

        private sealed class SymbolDataPdbSig : SymbolData
        {
            public readonly Guid PdbSig;
            public readonly int PdbAge;

            public SymbolDataPdbSig(Guid pdbSig, int pdbAge)
            {
                PdbSig = pdbSig;
                PdbAge = pdbAge;
            }

            public override int GetHashCode() => HashCode.Combine(PdbSig, PdbAge);

            public override bool Equals(object? obj)
                => obj is SymbolDataPdbSig other && PdbSig == other.PdbSig && PdbAge == other.PdbAge;
        }

        private sealed class SymbolDataPESig : SymbolData
        {
            public readonly string FileName;
            public readonly ulong SizeOfImage;
            public readonly uint TimeDateStamp;

            public SymbolDataPESig(string fileName, ulong sizeOfImage, uint timeDateStamp)
            {
                FileName = fileName;
                SizeOfImage = sizeOfImage;
                TimeDateStamp = timeDateStamp;
            }

            public override int GetHashCode() => HashCode.Combine(FileName, SizeOfImage, TimeDateStamp);

            public override bool Equals(object? obj)
                => obj is SymbolDataPESig other &&
                        string.Equals(FileName, other.FileName, StringComparison.OrdinalIgnoreCase) &&
                        SizeOfImage == other.SizeOfImage &&
                        TimeDateStamp == other.TimeDateStamp;
        }

        private class RegisteredModuleRevoker : RegisteredModule
        {
            public override SymbolKey Key { get; init; }
            public override Module Module { get; init; }
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

        public readonly record struct Module(string FileName, ulong SizeOfImage, uint TimeDateStamp, string PdbFileName, int PdbAge, Guid PdbSig);


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

        private DbgHelpSessionHandle? _sessionHandle;

        // A SymbolData also serves as its own SymbolKey, so the set dedups loaded modules by symbol identity.
        private readonly HashSet<SymbolData> _symbolCache = new();

        /// <summary>The process-wide singleton instance. Call <see cref="Initialize(uint, string)"/> before use.</summary>
        public static readonly SymbolResolver Instance = new();

        private SymbolResolver()
        {
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
        /// Initializes the single process-wide dbghelp session: applies the symbol options (<c>SYMOPT_*</c>), creates the
        /// session, and registers the diagnostic callback. Must be called once at startup before any other member is used.
        /// </summary>
        /// <param name="symbolOptions">The dbghelp symbol options to apply via <c>SymSetOptions</c>.</param>
        /// <param name="searchPath">
        /// The dbghelp search path used to locate binaries and PDBs (local directories and/or <c>srv*</c> entries for
        /// symbol servers).
        /// </param>
        /// <exception cref="InvalidOperationException">Thrown if dbghelp fails to initialize the session.</exception>
        public unsafe void Initialize(uint symbolOptions, string searchPath)
        {
            ArgumentNullException.ThrowIfNull(searchPath);

            using (BeginCurrentModuleDataScope(null))
            {
                lock (DbgHelpLock)
                {
                    PInvoke.SymSetOptions(symbolOptions | PInvoke.SYMOPT_DEBUG);

                    _sessionHandle = new DbgHelpSessionHandle(new IntPtr(Interlocked.Increment(ref NextHandleValue)));

                    WriteTraceLine($"SymbolResolver: Initializing with search path '{searchPath}'.");
                    if (!PInvoke.SymInitializeW(_sessionHandle, searchPath, fInvadeProcess: false))
                    {
                        throw new InvalidOperationException($"SymInitializeW failed. LastError={Marshal.GetLastPInvokeError()}");
                    }

                    if (!PInvoke.SymRegisterCallbackW64(_sessionHandle, &DbgHelpCallback, 0))
                    {
                        WriteTraceLine($"SymbolResolver: SymRegisterCallbackW64 failed. LastError={Marshal.GetLastPInvokeError()}.");
                    }
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
                SymbolData candidate = (module.PdbSig == Guid.Empty) ?
                    new SymbolDataPESig(module.FileName, module.SizeOfImage, module.TimeDateStamp) :
                    new SymbolDataPdbSig(module.PdbSig, module.PdbAge);

                if (!_symbolCache.TryGetValue(candidate, out SymbolData? symbolData))
                {
                    symbolData = candidate;
                    _symbolCache.Add(symbolData);
                }

                symbolData.Modules.Add(module);
                symbolData.ReferenceCount++;
                return new RegisteredModuleRevoker { Key = symbolData, Module = module, ModuleData = symbolData };
            }
        }

        public void FindSymbols(SymbolKey key, in Module module)
        {
            FindPdb(module, (SymbolData)key);
        }

        public string? GetPdbPath(SymbolKey key)
        {
            SymbolData symbolData = (SymbolData)key;
            lock (symbolData)
            {
                return symbolData.PdbPath;
            }
        }

        public bool HasDiagnosticLog(SymbolKey key)
        {
            SymbolData symbolData = (SymbolData)key;
            lock (symbolData)
            {
                return symbolData.DiagnosticLog.Length > 0;
            }
        }

        public string GetDiagnosticLog(SymbolKey key)
        {
            SymbolData symbolData = (SymbolData)key;
            lock (symbolData)
            {
                return symbolData.DiagnosticLog.ToString();
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
                        var localPdbIdentity = GetPdbSignatureAndAge(module.PdbFileName);
                        if (localPdbIdentity == null)
                        {
                            WriteTraceLine($"[FindPdb]: Local file '{module.PdbFileName}' exists, but failed to read PDB index info. LastError={Marshal.GetLastPInvokeError()}.");
                        }
                        else if (localPdbIdentity.Value.PdbSig == module.PdbSig && localPdbIdentity.Value.PdbAge == (uint)module.PdbAge)
                        {
                            WriteTraceLine($"[FindPdb]: Local file '{module.PdbFileName}' found with matching PdbSig={localPdbIdentity.Value.PdbSig} PdbAge={localPdbIdentity.Value.PdbAge}.");
                            symbolData.PdbPath = module.PdbFileName;
                            return;
                        }
                        else
                        {
                            WriteTraceLine($"[FindPdb]: Local file '{module.PdbFileName}' mismatched. Expected PdbSig={module.PdbSig} PdbAge={module.PdbAge}; actual PdbSig={localPdbIdentity.Value.PdbSig} PdbAge={localPdbIdentity.Value.PdbAge}.");
                        }
                    }

                    unsafe
                    {
                        Span<char> foundFile = stackalloc char[MaxPathBufferLength];
                        Guid pdbSig = module.PdbSig;

                        // dbghelp only matches files in plain (non-symbol-server) directories by name, and SYMOPT_EXACT_SYMBOLS
                        // does not reliably reject a name-matching but signature-mismatching PDB there. We pass a callback that
                        // validates each candidate's signature/age and keeps the search going past mismatches (e.g. a stale PDB in
                        // a build output directory) so dbghelp advances to later path entries (such as the symbol servers).
                        FindPdbContext findContext = new() { ExpectedSig = module.PdbSig, ExpectedAge = (uint)module.PdbAge };
                        bool found;
                        lock (DbgHelpLock)
                        {
                            found = PInvoke.SymFindFileInPathW(
                                _sessionHandle,
                                null,
                                module.PdbFileName,
                                &pdbSig,
                                (uint)module.PdbAge,
                                0,
                                SYM_FIND_ID_OPTION.SSRVOPT_GUIDPTR,
                                foundFile,
                                &FindFileInPathCallback,
                                &findContext);
                        }

                        if (!found)
                        {
                            WriteTraceLine($"[FindPdb]: No matching PDB found by SymFindFileInPathW. LastError={Marshal.GetLastPInvokeError()}.");
                            return;
                        }

                        string pdbPath = StringFromNullTerminated(foundFile);
                        WriteTraceLine($"[FindPdb]: Found matching PDB at '{pdbPath}'.");
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

        private struct FindPdbContext
        {
            public Guid ExpectedSig;
            public uint ExpectedAge;
        }

        // SymFindFileInPathW invokes this for each candidate file it locates. Returning TRUE continues the search to the next
        // entry in the symbol path; returning FALSE accepts the candidate and ends the search. dbghelp matches files in plain
        // directories by name only and does not reliably reject a signature mismatch there (even with SYMOPT_EXACT_SYMBOLS), so
        // we validate each candidate here and keep searching past mismatches until the correct PDB is found (e.g. on a symbol server).
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe BOOL FindFileInPathCallback(PCWSTR filename, void* context)
        {
            string candidatePath = filename.ToString();
            FindPdbContext* expected = (FindPdbContext*)context;

            (Guid PdbSig, uint PdbAge)? candidateIdentity = GetPdbSignatureAndAge(candidatePath);
            if (candidateIdentity == null)
            {
                WriteTraceLine($"[FindPdb]: Candidate '{candidatePath}' rejected; failed to read PDB index info. LastError={Marshal.GetLastPInvokeError()}.");
                return true; // Continue searching.
            }

            if (candidateIdentity.Value.PdbSig == expected->ExpectedSig && candidateIdentity.Value.PdbAge == expected->ExpectedAge)
            {
                WriteTraceLine($"[FindPdb]: Candidate '{candidatePath}' accepted with PdbSig={candidateIdentity.Value.PdbSig} PdbAge={candidateIdentity.Value.PdbAge}.");
                return false; // Accept and end search.
            }

            WriteTraceLine($"[FindPdb]: Candidate '{candidatePath}' rejected. Expected PdbSig={expected->ExpectedSig} PdbAge={expected->ExpectedAge}; actual PdbSig={candidateIdentity.Value.PdbSig} PdbAge={candidateIdentity.Value.PdbAge}.");
            return true; // Continue searching.
        }

        private static unsafe (Guid PdbSig, uint PdbAge)? GetPdbSignatureAndAge(string pdbPath)
        {
            SYMSRV_INDEX_INFOW indexInfo = new()
            {
                sizeofstruct = (uint)sizeof(SYMSRV_INDEX_INFOW),
            };

            fixed (char* pdbPathLocal = pdbPath)
            {
                lock (DbgHelpLock)
                {
                    if (!PInvoke.SymSrvGetFileIndexInfoW(pdbPathLocal, &indexInfo, 0))
                    {
                        return null;
                    }
                }
            }

            return (indexInfo.guid, indexInfo.age);
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
        /// The actual value of the handle is not important as long as it's unique and nonzero, so we generate it from an
        /// incrementing long value.
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
