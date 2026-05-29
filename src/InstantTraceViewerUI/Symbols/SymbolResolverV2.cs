using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    internal sealed class SymbolResolverV2 : IDisposable
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

            public int ReferenceCount { get; set; }
        }

        internal static readonly object DbgHelpLock = new();
        private static long NextHandleValue = 1;

        private readonly DbgHelpSessionHandle _sessionHandle;

        private readonly Dictionary<Module, ModuleData> _binaryLookupCache = new();

        /// <summary>
        /// Creates a new resolver with its own dbghelp session and caches.
        /// </summary>
        /// <param name="searchPath">
        /// The dbghelp search path used to locate binaries and PDBs (local directories and/or <c>srv*</c> entries
        /// for symbol servers). See <see cref="CreateDefaultSearchPath"/> for a sensible default.
        /// </param>
        /// <exception cref="InvalidOperationException">Thrown if dbghelp fails to initialize the session.</exception>
        public SymbolResolverV2(string searchPath)
        {
            ArgumentNullException.ThrowIfNull(searchPath);
            _sessionHandle = new DbgHelpSessionHandle(new IntPtr(Interlocked.Increment(ref NextHandleValue)));

            Trace.WriteLine($"SymbolResolver: Initializing with search path '{searchPath}'.");
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

        public ResolvedSymbol? Resolve(in Module moduleLookupRequest, ulong relativeVirtualAddress)
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
                if (!_binaryLookupCache.TryGetValue(moduleLookupRequest, out var value))
                {
                    value = new();
                    _binaryLookupCache.Add(moduleLookupRequest, value);
                }
                value.ReferenceCount++;
                return new RegisteredModuleRevoker(value);
            }
        }

        public unsafe string? FindBinary(in Module moduleLookupRequest, FindBinaryMethod findMethod)
        {
            // First try the cache.
            if ((findMethod == FindBinaryMethod.CacheOnly || findMethod == FindBinaryMethod.Default) &&
                _binaryLookupCache.TryGetValue(moduleLookupRequest, out ModuleData? moduleData) &&
                !string.IsNullOrEmpty(moduleData.ResolvedBinaryPath))
            {
                return moduleData.ResolvedBinaryPath;
            }

            if (findMethod == FindBinaryMethod.CacheOnly)
            {
                return null;
            }

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
                        Trace.WriteLine($"SymbolResolver[FindBinary]: '{moduleLookupRequest.FileName}' not found.");
                    }
                    else
                    {
                        Trace.WriteLine($"SymbolResolver[FindBinary]: Unexpected failure. LastError={Marshal.GetLastPInvokeError()}.");
                    }
                    return null;
                }

                foundBinary = StringFromNullTerminated(foundFile);
                Trace.WriteLine($"SymbolResolver[FindBinary]: {foundBinary} found via SymFindFileInPathW.");
            }

            lock (_binaryLookupCache)
            {
                if (!_binaryLookupCache.TryGetValue(moduleLookupRequest, out var value))
                {
                    value = new();
                    _binaryLookupCache.Add(moduleLookupRequest, value);
                }
                value.ResolvedBinaryPath = foundBinary;
            }

            return foundBinary;
        }

        public void RenderSymbolManagerWindow(IUiCommands uiCommands, ref bool isOpen)
        {
        }

        public void Dispose()
        {
            if (_sessionHandle.IsClosed)
            {
                return;
            }

            lock (DbgHelpLock)
            {
                // TODO: PInvoke.SymUnloadModule64(_sessionHandle, BaseAddress);
                // TODO: PInvoke.SymCleanup(_sessionHandle);
            }

            _sessionHandle.Dispose();
        }

        private string? FindBinaryLocal(in Module moduleLookupRequest)
        {
            if (!Path.IsPathFullyQualified(moduleLookupRequest.FileName))
            {
                return null;
            }

            try
            {
                using FileStream fileStream = File.OpenRead(moduleLookupRequest.FileName);
                using PEReader peReader = new(fileStream);
                PEHeader? peHeader = peReader.PEHeaders.PEHeader;
                if (peHeader == null)
                {
                    Trace.WriteLine($"SymbolResolver[FindBinary]: Local file '{moduleLookupRequest.FileName}' has no PE header.");
                    return null;
                }

                uint timeDateStamp = unchecked((uint)peReader.PEHeaders.CoffHeader.TimeDateStamp);
                uint sizeOfImage = unchecked((uint)peHeader.SizeOfImage);
                if (timeDateStamp != moduleLookupRequest.TimeDateStamp || sizeOfImage != moduleLookupRequest.SizeOfImage)
                {
                    Trace.WriteLine($"SymbolResolver[FindBinary]: Local file '{moduleLookupRequest.FileName}' mismatched. Expected TimeDateStamp=0x{moduleLookupRequest.TimeDateStamp:X8} SizeOfImage=0x{moduleLookupRequest.SizeOfImage:X}; actual TimeDateStamp=0x{timeDateStamp:X8} SizeOfImage=0x{sizeOfImage:X}.");
                    return null;
                }

                Trace.WriteLine($"SymbolResolver[FindBinary]: Local file '{moduleLookupRequest.FileName}' found locally with matching TimeDateStamp=0x{moduleLookupRequest.TimeDateStamp:X8} SizeOfImage=0x{moduleLookupRequest.SizeOfImage:X}.");
                return moduleLookupRequest.FileName;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"SymbolResolver[FindBinary]: Failed to open or parse '{moduleLookupRequest.FileName}': {ex.Message}");
                return null;
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
