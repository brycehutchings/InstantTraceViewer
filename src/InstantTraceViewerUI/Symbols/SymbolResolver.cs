using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace InstantTraceViewerUI.Symbols
{
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
        private const int MaxPathBufferLength = 32768;
        private const int MaxSymbolNameLength = 1024;
        private const ulong SyntheticBaseStart = 0x1000000000;
        private const ulong SyntheticBaseAlignment = 0x10000000;

        private static readonly object DbgHelpLock = new();
        private static long NextHandleValue = 1;

        private readonly DbgHelpSessionHandle _sessionHandle;
        private readonly ConcurrentDictionary<ModuleLookupRequest, Lazy<Task<string?>>> _binaryLookupCache = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<DbgHelpFileIndexInfo?>>> _indexInfoCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<PdbLookupRequest, Lazy<Task<string?>>> _pdbLookupCache = new();
        private readonly ConcurrentDictionary<ModuleLookupRequest, Lazy<Task<LoadedModule?>>> _loadedModuleCache = new();
        private readonly ConcurrentDictionary<(ModuleLookupRequest Module, ulong RelativeVirtualAddress), Lazy<Task<ResolvedSymbol?>>> _symbolCache = new();

        private long _nextSyntheticBase = unchecked((long)SyntheticBaseStart);
        private bool _disposed;

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

            Trace.WriteLine($"SymbolResolver: initializing with search path '{searchPath}'.");
            lock (DbgHelpLock)
            {
                if (!PInvoke.SymInitializeW(_sessionHandle, searchPath, false))
                {
                    throw new InvalidOperationException($"SymInitializeW failed. LastError={Marshal.GetLastPInvokeError()}");
                }

                unsafe
                {
                    if (!PInvoke.SymRegisterCallbackW64(_sessionHandle, &DbgHelpCallback, 0))
                    {
                        Trace.WriteLine($"SymbolResolver: SymRegisterCallbackW64 failed. LastError={Marshal.GetLastPInvokeError()}.");
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
                    Trace.WriteLine("DbgHelp: " + message.TrimEnd());
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
            lock (DbgHelpLock)
            {
                PInvoke.SymSetOptions(symbolOptions | PInvoke.SYMOPT_DEBUG);
            }
        }

        public static void SetParentWindow(HWND hwnd)
        {
            lock (DbgHelpLock)
            {
                PInvoke.SymSetParentWindow(hwnd);
            }
        }

        /// <summary>
        /// Returns a default dbghelp search path that caches downloaded symbols under the user's local app data
        /// folder and falls back to the public Microsoft symbol server.
        /// </summary>
        public static string CreateDefaultSearchPath()
        {
            string symbolCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InstantTraceViewer",
                "Symbols");

            return $"srv*{symbolCache}*https://msdl.microsoft.com/download/symbols";
        }

        /// <summary>
        /// Locates a binary (exe/dll) on disk or downloads it from a symbol server using its file name, timestamp,
        /// and image size as the index key. Returns the full path to the local copy on success, or null if it
        /// cannot be found. Results are cached per <see cref="ModuleLookupRequest"/>.
        /// 
        /// If binary is in the symbol search path, it will be used regardless of timestamp/size. Timestamp/size is
        /// only used to locate a binary on a symbol server.
        /// </summary>
        public Task<string?> FindBinaryAsync(ModuleLookupRequest module)
        {
            ThrowIfDisposed();
            return _binaryLookupCache.GetOrAdd(module, key => new Lazy<Task<string?>>(() => Task.Run(() => FindBinary(key)))).Value;
        }

        /// <summary>
        /// Reads the symbol-server index information embedded in a binary (timestamp, image size, associated PDB
        /// name, PDB GUID/age/signature). Useful for taking a binary on disk and discovering the identity of the
        /// PDB that needs to be downloaded for it. Returns null if the file cannot be parsed. Results are cached
        /// per file path (case-insensitive).
        /// </summary>
        public Task<DbgHelpFileIndexInfo?> GetFileIndexInfoAsync(string filePath)
        {
            ThrowIfDisposed();
            return _indexInfoCache.GetOrAdd(filePath, key => new Lazy<Task<DbgHelpFileIndexInfo?>>(() => Task.Run(() => GetFileIndexInfo(key)))).Value;
        }

        /// <summary>
        /// Locates a PDB on disk or downloads it from a symbol server using the PDB file name, GUID, and age as
        /// the index key. Returns the full path to the local copy on success, or null if it cannot be found.
        /// Results are cached per <see cref="PdbLookupRequest"/>.
        /// </summary>
        public Task<string?> FindPdbAsync(PdbLookupRequest pdb)
        {
            ThrowIfDisposed();
            return _pdbLookupCache.GetOrAdd(pdb, key => new Lazy<Task<string?>>(() => Task.Run(() => FindPdb(key)))).Value;
        }

        /// <summary>
        /// Resolves a module-relative virtual address to a symbol name and displacement. On first use for a given
        /// module this will (lazily and as needed) locate the binary, fetch the matching PDB, and load the module
        /// into the dbghelp session. Returns null if the binary or PDB cannot be obtained or no symbol covers the
        /// requested address. Results are cached per (module, RVA) pair.
        /// </summary>
        public Task<ResolvedSymbol?> ResolveAsync(ModuleLookupRequest module, ulong relativeVirtualAddress)
        {
            ThrowIfDisposed();
            return _symbolCache.GetOrAdd((module, relativeVirtualAddress), key => new Lazy<Task<ResolvedSymbol?>>(() => Task.Run(() => Resolve(key.Module, key.RelativeVirtualAddress)))).Value;
        }

        private string? FindBinary(ModuleLookupRequest module)
        {
            string fileName = Path.GetFileName(module.FileName);

            Trace.WriteLine($"SymbolResolver: finding binary '{fileName}' timestamp=0x{module.TimeDateStamp:X8} size=0x{module.ImageSize:X}.");
            unsafe
            {
                Span<char> foundFile = stackalloc char[MaxPathBufferLength];
                lock (DbgHelpLock)
                {
                    bool found = PInvoke.SymFindFileInPathW(
                        _sessionHandle,
                        null,
                        fileName,
                        (void*)(nuint)module.TimeDateStamp,
                        checked((uint)module.ImageSize),
                        0,
                        SYM_FIND_ID_OPTION.SSRVOPT_DWORD,
                        foundFile,
                        null,
                        null);

                    if (!found)
                    {
                        Trace.WriteLine($"SymbolResolver: binary lookup failed for '{fileName}'. LastError={Marshal.GetLastPInvokeError()}.");
                        return null;
                    }
                }

                string result = StringFromNullTerminated(foundFile);
                Trace.WriteLine($"SymbolResolver: found binary '{result}'.");
                return result;
            }
        }

        private unsafe DbgHelpFileIndexInfo? GetFileIndexInfo(string filePath)
        {
            Trace.WriteLine($"SymbolResolver: reading symbol index info from '{filePath}'.");

            SYMSRV_INDEX_INFOW info = default;
            info.sizeofstruct = (uint)sizeof(SYMSRV_INDEX_INFOW);

            lock (DbgHelpLock)
            {
                if (!PInvoke.SymSrvGetFileIndexInfoW(filePath, out info, 0))
                {
                    Trace.WriteLine($"SymbolResolver: SymSrvGetFileIndexInfoW failed for '{filePath}'. LastError={Marshal.GetLastPInvokeError()}.");
                    return null;
                }
            }

            DbgHelpFileIndexInfo result = new(
                info.file.ToString(),
                info.timestamp,
                info.size,
                info.pdbfile.ToString(),
                info.guid,
                info.age,
                info.sig);

            Trace.WriteLine($"SymbolResolver: index info file='{result.FileName}' timestamp=0x{result.TimeDateStamp:X8} size=0x{result.Size:X} pdb='{result.PdbFileName}' guid={result.PdbGuid} age={result.PdbAge}.");
            return result;
        }

        private string? FindPdb(PdbLookupRequest pdb)
        {
            Trace.WriteLine($"SymbolResolver: finding PDB '{pdb.PdbFileName}' guid={pdb.Guid} age={pdb.Age}.");
            unsafe
            {
                Span<char> foundFile = stackalloc char[MaxPathBufferLength];
                Guid guid = pdb.Guid;
                lock (DbgHelpLock)
                {
                    bool found = PInvoke.SymFindFileInPathW(
                        _sessionHandle,
                        null,
                        pdb.PdbFileName,
                        &guid,
                        pdb.Age,
                        0,
                        SYM_FIND_ID_OPTION.SSRVOPT_GUIDPTR,
                        foundFile,
                        null,
                        null);

                    if (!found)
                    {
                        Trace.WriteLine($"SymbolResolver: PDB lookup failed for '{pdb.PdbFileName}'. LastError={Marshal.GetLastPInvokeError()}.");
                        return null;
                    }
                }

                string result = StringFromNullTerminated(foundFile);
                Trace.WriteLine($"SymbolResolver: found PDB '{result}'.");
                return result;
            }
        }

        private ResolvedSymbol? Resolve(ModuleLookupRequest module, ulong relativeVirtualAddress)
        {
            LoadedModule? loadedModule = _loadedModuleCache.GetOrAdd(module, key => new Lazy<Task<LoadedModule?>>(() => Task.Run(() => LoadModule(key)))).Value.GetAwaiter().GetResult();
            if (loadedModule == null)
            {
                return null;
            }

            ulong address = loadedModule.Value.BaseAddress + relativeVirtualAddress;
            unsafe
            {
                // SYMBOL_INFOW has a trailing inline 1-char Name buffer; allocate extra space for the rest.
                int symbolInfoSize = sizeof(SYMBOL_INFOW) + (MaxSymbolNameLength - 1) * sizeof(char);
                byte[] symbolInfoBuffer = new byte[symbolInfoSize];

                fixed (byte* symbolInfoBufferPtr = symbolInfoBuffer)
                {
                    SYMBOL_INFOW* symbolInfo = (SYMBOL_INFOW*)symbolInfoBufferPtr;
                    symbolInfo->SizeOfStruct = (uint)sizeof(SYMBOL_INFOW);
                    symbolInfo->MaxNameLen = MaxSymbolNameLength;

                    ulong displacement;
                    lock (DbgHelpLock)
                    {
                        if (!PInvoke.SymFromAddrW(_sessionHandle, address, out displacement, symbolInfo))
                        {
                            Trace.WriteLine($"SymbolResolver: symbol lookup failed for '{module.FileName}+0x{relativeVirtualAddress:X}'. LastError={Marshal.GetLastPInvokeError()}.");
                            return null;
                        }
                    }

                    int nameLen = Math.Min((int)symbolInfo->NameLen, (int)symbolInfo->MaxNameLen);
                    string symbolName = nameLen == 0 ? string.Empty : new string((char*)&symbolInfo->Name, 0, nameLen);
                    Trace.WriteLine($"SymbolResolver: resolved '{module.FileName}+0x{relativeVirtualAddress:X}' to '{symbolName}+0x{displacement:X}'.");
                    return new ResolvedSymbol(Path.GetFileName(module.FileName), relativeVirtualAddress, symbolName, displacement, true);
                }
            }
        }

        private LoadedModule? LoadModule(ModuleLookupRequest module)
        {
            string? binaryPath = FindBinary(module);
            if (binaryPath == null)
            {
                return null;
            }

            DbgHelpFileIndexInfo? indexInfo = GetFileIndexInfo(binaryPath);
            if (indexInfo?.HasPdbIdentity == true)
            {
                _ = FindPdb(new PdbLookupRequest(indexInfo.Value.PdbFileName, indexInfo.Value.PdbGuid, indexInfo.Value.PdbAge));
            }

            string moduleName = Path.GetFileNameWithoutExtension(module.FileName);
            ulong baseAddress = AllocateSyntheticBase(module.ImageSize);

            lock (DbgHelpLock)
            {
                ulong loadedBase = PInvoke.SymLoadModuleExW(
                    _sessionHandle,
                    null,
                    binaryPath,
                    moduleName,
                    baseAddress,
                    checked((uint)module.ImageSize),
                    null,
                    SYM_LOAD_FLAGS.SLMFLAG_NONE);

                if (loadedBase == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    Trace.WriteLine($"SymbolResolver: failed to load module '{binaryPath}'. LastError={error}.");
                    return null;
                }

                Trace.WriteLine($"SymbolResolver: loaded module '{binaryPath}' at 0x{loadedBase:X}.");
                return new LoadedModule(loadedBase, module.ImageSize);
            }
        }

        private ulong AllocateSyntheticBase(ulong imageSize)
        {
            ulong allocationSize = Align(Math.Max(imageSize, SyntheticBaseAlignment), SyntheticBaseAlignment);
            long next = Interlocked.Add(ref _nextSyntheticBase, unchecked((long)allocationSize));
            return unchecked((ulong)next) - allocationSize;
        }

        private static ulong Align(ulong value, ulong alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
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

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SymbolResolver));
            }
        }

        /// <summary>
        /// Releases the dbghelp session, unloading all modules previously loaded by this instance.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (DbgHelpLock)
            {
                foreach (Lazy<Task<LoadedModule?>> loadedModuleTask in _loadedModuleCache.Values)
                {
                    if (!loadedModuleTask.IsValueCreated || !loadedModuleTask.Value.IsCompletedSuccessfully || loadedModuleTask.Value.Result == null)
                    {
                        continue;
                    }

                    PInvoke.SymUnloadModule64(_sessionHandle, loadedModuleTask.Value.Result.Value.BaseAddress);
                }

                PInvoke.SymCleanup(_sessionHandle);
            }

            _sessionHandle.Dispose();
            _disposed = true;
        }

        private readonly record struct LoadedModule(ulong BaseAddress, ulong Size);

        /// <summary>
        /// Dbghelp uses a pseudo-handle to identify symbol sessions in its APIs.
        /// Since we want to have multiple independent instances of SymbolResolver that don't interfere with each other's caches,
        /// we create a separate session for each instance by using a unique pseudo-handle value.
        /// The actual value of the handle is not important as long as it's unique, so we use an incrementing long value to generate it.
        /// </summary>
        private sealed class DbgHelpSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
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
