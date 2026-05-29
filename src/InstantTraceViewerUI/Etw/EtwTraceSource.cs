using Hexa.NET.ImGui;
using InstantTraceViewer;
using InstantTraceViewerUI.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Xml.Linq;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace InstantTraceViewerUI.Etw
{
    // See https://github.com/microsoft/cpp_client_telemetry_modules/blob/master/utc/MicrosoftTelemetry.h
    [Flags]
    internal enum KnownKeywords : ulong
    {
        Telemetry = 0x0000200000000000,
        TelemetryCritical = 0x0000800000000000,
        TelemetryMeasures = 0x0000400000000000,
    }

    internal partial class EtwTraceSource : ITraceSource, ITraceSourceGuiExtensions
    {
        public static readonly TraceSourceSchemaColumn ColumnProcess = new TraceSourceSchemaColumn { Name = "Process", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnThread = new TraceSourceSchemaColumn { Name = "Thread", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnProvider = new TraceSourceSchemaColumn { Name = "Provider", DefaultColumnSize = 11.00f };
        public static readonly TraceSourceSchemaColumn ColumnOpCode = new TraceSourceSchemaColumn { Name = "OpCode", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnKeywords = new TraceSourceSchemaColumn { Name = "Keywords", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnName = new TraceSourceSchemaColumn { Name = "Name", DefaultColumnSize = 8.75f };
        public static readonly TraceSourceSchemaColumn ColumnLevel = new TraceSourceSchemaColumn { Name = "Level", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnTime = new TraceSourceSchemaColumn { Name = "Time", DefaultColumnSize = 9.00f };
        public static readonly TraceSourceSchemaColumn ColumnMessage = new TraceSourceSchemaColumn { Name = "Message", DefaultColumnSize = null };

        private static readonly TraceTableSchema _schema = new TraceTableSchema
        {
            Columns = [ColumnProcess, ColumnThread, ColumnProvider, ColumnOpCode, ColumnKeywords, ColumnName, ColumnLevel, ColumnTime, ColumnMessage],
            TimestampColumn = ColumnTime,
            UnifiedLevelColumn = ColumnLevel,
            UnifiedOpcodeColumn = ColumnOpCode,
            ProcessIdColumn = ColumnProcess,
            ThreadIdColumn = ColumnThread,
            ProviderColumn = ColumnProvider,
            NameColumn = ColumnName,
        };

        private static HashSet<int> SessionNums = new();

        // Fixed name is used because ETW sessions can outlive their processes and there is a low system limit. This way we replace leaked sessions rather than creating new ones.
        private static string SessionNamePrefix = "InstantTraceViewerSession";

        private readonly TraceEventSession? _etwSession;
        private readonly ETWTraceEventSource _etwSource;
        private readonly bool _kernelProcessThreadProviderEnabled;

        private readonly int _sessionNum;
        private readonly Thread _processingThread;

        private readonly ReaderWriterLockSlim _pendingTraceRecordsLock = new ReaderWriterLockSlim();
        private List<EtwRecord> _pendingTraceRecords = new();

        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private ListBuilder<EtwRecord> _traceRecords = new ListBuilder<EtwRecord>();
        private int _generationId = 1;

        private ProcessDatabase _processDatabase = new();
        private List<IDisposable> _moduleRevokers = new();
        private SymbolResolverV2 _symbolResolver = new SymbolResolverV2(
            @"c:\windows\system32;" +
            @"d:\repos\cloud1\binlocal\WinX64;" + 
            @"D:\repos\cloud3\binlocal\Immersive\Desktop\WinX64\MrShell;" + 
            @"d:\repos\cloud1\binlocal\WinX64\Symbols;" +
            @"srv*c:\symcache*https://driver-symbols.nvidia.com/;" +
            @"srv*c:\symcache*https://microsoft.artifacts.visualstudio.com/_apis/Symbol/symsrv;" +
            @"srv*c:\symcache*https://msdl.microsoft.com/download/symbols");

        private bool isDisposed;

        // Stores the original profile for pause/resume functionality
        private readonly EtwSessionProfile? _profile;

        public EtwTraceSource(TraceEventSession etwSession, bool kernelProcessThreadProviderEnabled, string displayName, EtwSessionProfile? profile = null, int sessionNum = -1)
        {
            DisplayName = $"{displayName} (ETW)";
            _etwSession = etwSession;
            _etwSource = etwSession.Source;
            _kernelProcessThreadProviderEnabled = kernelProcessThreadProviderEnabled;
            _sessionNum = sessionNum;
            _profile = profile;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        public EtwTraceSource(ETWTraceEventSource etwSource, string displayName)
        {
            DisplayName = displayName;
            _etwSession = null;
            _etwSource = etwSource;
            _kernelProcessThreadProviderEnabled = false;
            _sessionNum = -1;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        // Autologgers save the etl extensions with a number suffix. Associate a handful of them too.
        public static IEnumerable<string> EtlFileExtensions => new[] { ".etl", ".etlx" }.Concat(Enumerable.Range(1, 15).Select(i => $".{i:D3}"));

        private void AddEvent(EtwRecord record)
        {
            if (IsPaused)
            {
                return;
            }

            _pendingTraceRecordsLock.EnterWriteLock();
            try
            {
                _pendingTraceRecords.Add(record);
            }
            finally
            {
                _pendingTraceRecordsLock.ExitWriteLock();
            }
        }

        private void ProcessThread()
        {
            SubscribeToKernelEvents();
            SubscribeToDynamicEvents();

            try
            {
                // This will block until the ETW session has been disposed.
                _etwSource.Process();
            }
            catch (Exception ex)
            {
                AddEvent(new EtwRecord
                {
                    ProviderName = "Instant Trace Viewer",
                    Name = "Internal Error",
                    Level = TraceEventLevel.Critical,
                    NamedValues = [new NamedValue { Value = $"Failed to process ETW session: {ex.Message}" }]
                });
            }
        }

        static public EtwTraceSource CreateEtlSession(string etlFile)
        {
            var eventSource = new ETWTraceEventSource(etlFile, TraceEventSourceType.FileOnly);
            try
            {
                return new EtwTraceSource(eventSource, Path.GetFileName(etlFile));
            }
            catch
            {
                eventSource.Dispose();
                throw;
            }
        }

        static public EtwTraceSource CreateRealTimeSession(EtwSessionProfile profile)
        {
            int sessionNum = ReserveNextSessionNumber();
            TraceEventSession etwSession = new($"{SessionNamePrefix}{sessionNum}");

            try
            {
                bool kernelProcessThreadProviderEnabled = false;

                // NonOSKeywords is taken from perflib's Microsoft.Diagnostics.Tracing library which disallows use of certain
                // kernel keywords (PMCProfile ReferenceSet ThreadPriority IOQueue Handle) unless the session name is exactly "NT Kernel Logger" (KernelTraceEventParser.KernelSessionName)
                // but then this disallows non-kernel providers being enabled. To avoid the problem we simply remove these special keywords.
                // Oddly WPR can enable these keywords along with non-kernel providers, so the Microsoft.Diagnostics.Tracing library restrictions may be out of date?
                KernelTraceEventParser.Keywords NonOSKeywords = (KernelTraceEventParser.Keywords)unchecked((int)0xf84c0000);
                KernelTraceEventParser.Keywords allowedKernelKeywords = profile.KernelKeywords & ~NonOSKeywords;
                if (allowedKernelKeywords != KernelTraceEventParser.Keywords.None)
                {
                    // EnableKernelProvider will always enable Process and Thread events.
                    kernelProcessThreadProviderEnabled = true;
                    etwSession.EnableKernelProvider(allowedKernelKeywords);
                }

                foreach (var provider in profile.Providers)
                {
                    // Make sure to keep in sync with TogglePause() method

                    // TODO (pass as optional 4th param): var options = new TraceEventProviderOptions() { StacksEnabled = true };
                    etwSession.EnableProvider(provider.Name, provider.Level, provider.MatchAnyKeyword);
                }

                // Example of enabling processor CPU counter support. Not investigated yet. Search perfview codebase for examples of use.
                // Perhaps this is related to the following: https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-pmu-events for WPR support (which we'd want to parse too).
                // TraceEventSession etwSession = new(KernelTraceEventParser.KernelSessionName);
                // kernelProcessThreadProviderEnabled = true;
                // etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.PMCProfile | KernelTraceEventParser.Keywords.Process);
                // var profileSources = TraceEventProfileSources.GetInfo();
                // TraceEventProfileSources.Set(profileSources["Timer"].ID, profileSources["Timer"].Interval);

                return new EtwTraceSource(etwSession, kernelProcessThreadProviderEnabled, profile.DisplayName, profile, sessionNum);
            }
            catch
            {
                etwSession.Dispose();
                SessionNums.Remove(sessionNum);
                throw;
            }
        }

        private static int ReserveNextSessionNumber()
        {
            for (int i = 1; i < 64; i++)
            {
                if (SessionNums.Add(i))
                {
                    return i;
                }
            }

            throw new InvalidOperationException("Too many active ETW sessions have been created.");
        }

        public string DisplayName { get; private set; }

        public int LostEvents => _etwSource.EventsLost;

        public bool CanClear => _etwSource.IsRealTime;

        public void Clear()
        {
            _traceRecordsLock.EnterWriteLock();
            try
            {
                _traceRecords = new();
                _generationId++;

                foreach (var module in _moduleRevokers)
                {
                    module.Dispose();
                }
                _moduleRevokers.Clear();
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }

            GC.Collect();
        }

        public bool CanPause => _etwSource.IsRealTime;
        public bool IsPaused { get; private set; }
        public void TogglePause()
        {
            IsPaused = !IsPaused;

            if (_etwSession == null || _profile == null)
            {
                return; // Can't control collection for file-based sources or without a profile
            }

            // It looks like we can't disable kernel providers once enabled, but that is OK because we still want the process/thread events to flow in for name lookups.
            foreach (var provider in _profile.Providers)
            {
                try
                {
                    if (IsPaused)
                    {
                        _etwSession.DisableProvider(provider.Name);
                    }
                    else
                    {
                        _etwSession.EnableProvider(provider.Name, provider.Level, provider.MatchAnyKeyword);
                    }
                }
                catch (Exception ex)
                {
                    // It's not critical if we fail to toggle collection since we also check the IsPaused flag everywhere.
                    Trace.WriteLine($"Failed to toggle ETW session providers: {ex}");
                    Debug.Fail($"Failed to toggle ETW session providers: {ex.Message}"); // To check if this actually happens in practice when testing.
                }
            }
        }

        // ETW data streams in very quickly, so no need to indicate to user that it is loading.
        public bool IsPreprocessingData => false;

        public ITraceTableSnapshot CreateSnapshot()
        {
            // By moving out the pending records, there is only brief contention on the 'pendingTraceRecords' list.
            // It is important to not block the ETW event callback or events might get dropped.
            List<EtwRecord> pendingTraceRecordsLocal;
            _pendingTraceRecordsLock.EnterWriteLock();
            try
            {
                pendingTraceRecordsLocal = _pendingTraceRecords;
                _pendingTraceRecords = new();
            }
            finally
            {
                _pendingTraceRecordsLock.ExitWriteLock();
            }

            UpdateProcessNameTable(pendingTraceRecordsLocal);

            _traceRecordsLock.EnterWriteLock();
            try
            {
                foreach (var record in pendingTraceRecordsLocal)
                {
                    _traceRecords.Add(record);
                }

                return new EtwTraceTableSnapshot
                {
                    RecordSnapshot = _traceRecords.CreateSnapshot(),
                    ProcessDatabase = _processDatabase,
                    GenerationId = _generationId,
                    Schema = _schema,
                };
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }
        }

        bool _renderSymbolManager = false;


        public void RenderToolstripExtras(IUiCommands uiCommands)
        {
            ImGui.SameLine();
            if (ImGui.Button("\ue697 Symbols"))
            {
                ImGui.OpenPopup("EtwSymbols");
            }
            if (ImGui.BeginPopup("EtwSymbols"))
            {
                if (ImGui.MenuItem("Manage symbols", "", _renderSymbolManager))
                {
                    _renderSymbolManager = !_renderSymbolManager;
                }

                ImGui.EndPopup();
            }
        }

        public void RenderActiveWindows(IUiCommands uiCommands)
        {
            if (_renderSymbolManager)
            {
                _symbolResolver.RenderSymbolManagerWindow(uiCommands, ref _renderSymbolManager);
            }
        }


        private void UpdateProcessNameTable(IReadOnlyList<EtwRecord> traceRecords)
        {
            // Microsoft.Diagnostics.Tracing will track process names when the Kernel provider is enabled, otherwise we need to do it.
            // If this is not a realtime session, then no point in trying to look up process names--they could be from a different machine or be reused at this point.
            if (!_etwSource.IsRealTime || _kernelProcessThreadProviderEnabled)
            {
                return;
            }

            HashSet<int> processNameLookupAttemptedPids = new();
            foreach (var record in traceRecords)
            {
                if (record.ProcessId < 0)
                {
                    continue;
                }

                if (!processNameLookupAttemptedPids.Add(record.ProcessId) ||
                    _processDatabase.GetProcessName(record.ProcessId, record.Timestamp) != null)
                {
                    continue;
                }

                try
                {
                    TryAddLiveProcessToDatabase(record.ProcessId);
                }
                catch
                {
                    Debug.WriteLine($"Failed to get process name for pid {record.ProcessId})");
                }
            }
        }

        private unsafe void TryAddLiveProcessToDatabase(int processId)
        {
            HANDLE processHandle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (!processHandle.IsNull)
            {
                try
                {
                    FILETIME creationTime;
                    FILETIME exitTime;
                    FILETIME kernelTime;
                    FILETIME userTime;
                    using SafeFileHandle safeProcessHandle = new((nint)processHandle.Value, ownsHandle: false);
                    if (!PInvoke.GetProcessTimes(safeProcessHandle, out creationTime, out exitTime, out kernelTime, out userTime))
                    {
                        return;
                    }

                    const int MaxProcessImagePathLength = 32768;
                    char* imagePathBuffer = stackalloc char[MaxProcessImagePathLength];
                    uint imagePathLength = MaxProcessImagePathLength;
                    if (!PInvoke.QueryFullProcessImageName(processHandle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, imagePathBuffer, &imagePathLength))
                    {
                        return;
                    }

                    string imagePath = new string(imagePathBuffer, 0, (int)imagePathLength);
                    string processName = Path.GetFileNameWithoutExtension(imagePath);
                    if (!string.IsNullOrEmpty(processName))
                    {
                        long creationTimeFT = (long)(((ulong)(uint)creationTime.dwHighDateTime << 32) | (uint)creationTime.dwLowDateTime);
                        if (_processDatabase.ProcessStart(processId, processName, DateTime.FromFileTime(creationTimeFT)))
                        {
                            // Process name for existing row(s) with this pid have changed retroactively.
                            _generationId++;
                        }
                    }
                }
                finally
                {
                    PInvoke.CloseHandle(processHandle);
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed)
            {
                if (disposing)
                {
                    _etwSource.Dispose();
                    _etwSession?.Dispose();
                    SessionNums.Remove(_sessionNum);

                    foreach (var module in _moduleRevokers)
                    {
                        module.Dispose();
                    }
                    _moduleRevokers.Clear();
                }

                isDisposed = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}