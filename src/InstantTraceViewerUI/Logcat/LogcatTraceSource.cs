using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Logs;
using AdvancedSharpAdbClient.Models;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using InstantTraceViewer;

namespace InstantTraceViewerUI.Logcat
{
    internal class LogcatTraceSource : ITraceSource
    {
        public static readonly TraceSourceSchemaColumn ColumnProcess = new TraceSourceSchemaColumn { Name = "Process", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnThread = new TraceSourceSchemaColumn { Name = "Thread", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnBufferId = new TraceSourceSchemaColumn { Name = "BufferId", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnTag = new TraceSourceSchemaColumn { Name = "Tag", Colorize = true, DefaultColumnSize = 8.75f };
        public static readonly TraceSourceSchemaColumn ColumnPriority = new TraceSourceSchemaColumn { Name = "Priority", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnTime = new TraceSourceSchemaColumn { Name = "Time", DefaultColumnSize = 9.00f };
        public static readonly TraceSourceSchemaColumn ColumnMessage = new TraceSourceSchemaColumn { Name = "Message", DefaultColumnSize = null };

        private static readonly TraceTableSchema _schema = new TraceTableSchema
        {
            Columns = [ColumnProcess, ColumnThread, ColumnBufferId, ColumnTag, ColumnPriority, ColumnTime, ColumnMessage],
            TimestampColumn = ColumnTime,
            UnifiedLevelColumn = ColumnPriority,
            ProcessIdColumn = ColumnProcess,
            ThreadIdColumn = ColumnThread,
            ProviderColumn = ColumnBufferId,
            NameColumn = ColumnTag,
        };

        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private ListBuilder<LogcatRecord> _traceRecords = new ListBuilder<LogcatRecord>();
        private int _generationId = 0;
        private readonly CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private readonly AdbClient _adbClient;
        private readonly DeviceData _device;
        private readonly Thread _readLogcatThread;

        private readonly ConcurrentDictionary<int, string> _processNames = new ConcurrentDictionary<int, string>();

        public LogcatTraceSource(AdbClient adbClient, DeviceData device)
        {
            _adbClient = adbClient;
            _device = device;

            // TODO: Package manager can be useful for filtering to 3rd party apps.
            // -3 to filter to 3rd party
            // -I to include UID (appended on the 'key' with this API)
            // var packages = _adbClient.CreatePackageManager(device, "-3", "-U").Packages.OrderBy(p => p.Value).ToList();

            // Start with a snapshot of pids to package names.
            foreach (var process in _adbClient.ListProcesses(_device))
            {
                _processNames.AddOrUpdate(process.ProcessId, _ => process.Name, (_, _) => process.Name);
            }

            _readLogcatThread = new Thread(() => ReadLogcatThread(adbClient, device));
            _readLogcatThread.Start();
        }

        public string DisplayName => $"{_device.Product} {_device.Model} {_device.Serial} (Logcat)";

        public bool CanClear => true;

        public void Clear()
        {
            _adbClient.ExecuteShellCommand(_device, "logcat -c");

            // Refresh the process name map.
            foreach (var process in _adbClient.ListProcesses(_device))
            {
                _processNames.AddOrUpdate(process.ProcessId, _ => process.Name, (_, _) => process.Name);
            }

            _traceRecordsLock.EnterWriteLock();
            try
            {
                _traceRecords = new();
                _generationId++;
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }
        }

        public bool CanPause => true;
        public bool IsPaused { get; private set; }
        public void TogglePause()
        {
            IsPaused = !IsPaused;
        }

        public int LostEvents => 0;

        public ITraceTableSnapshot CreateSnapshot()
        {
            _traceRecordsLock.EnterReadLock();
            try
            {
                return new LogcatTraceTableSnapshot
                {
                    RecordSnapshot = _traceRecords.CreateSnapshot(),
                    GenerationId = _generationId,
                    Schema = _schema,
                };
            }
            finally
            {
                _traceRecordsLock.ExitReadLock();
            }
        }

        public void Dispose()
        {
            _tokenSource.Cancel();
        }

        private async void ReadLogcatThread(AdbClient adbClient, DeviceData device)
        {
            try
            {
                // FIXME: LogId.Kernel can include log entries which break the 'LogService' background processor in AdvancedSharpAdbClient. This causes it to stop reading messages.
                //        So until this issue is understood and fixed, kernel messages are not included.
                await foreach (LogEntry logEntry in adbClient.RunLogServiceAsync(device, _tokenSource.Token, [LogId.Main, LogId.Crash, /*LogId.Kernel,*/ LogId.System, LogId.Security, LogId.Radio]))
                {
                    if (IsPaused)
                    {
                        continue;
                    }

                    if (logEntry is AndroidLogEntry androidLogEntry)
                    {
                        ProcessSystemMessage(androidLogEntry);

                        _processNames.TryGetValue(androidLogEntry.ProcessId, out string processName);
                        var preciseTimestamp = androidLogEntry.TimeStamp.ToLocalTime() + TimeSpan.FromTicks(androidLogEntry.NanoSeconds / TimeSpan.NanosecondsPerTick);
                        var traceRecord = new LogcatRecord
                        {
                            ProcessId = androidLogEntry.ProcessId,
                            ProcessName = processName,
                            ThreadId = (int)androidLogEntry.ThreadId,
                            Timestamp = preciseTimestamp.DateTime,
                            Priority = androidLogEntry.Priority,
                            Message = androidLogEntry.Message,
                            Tag = androidLogEntry.Tag,
                            LogId = androidLogEntry.Id,
                        };

                        _traceRecordsLock.EnterWriteLock();
                        try
                        {
                            _traceRecords.Add(traceRecord);
                        }
                        finally
                        {
                            _traceRecordsLock.ExitWriteLock();
                        }
                    }
                    else
                    {
                        // Sometimes there are pure "LogEntry" objects. Not sure what to do with them...
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Trace source is being disposed.
            }
        }

        private static readonly Regex ActivityManagerStartProc = new Regex(@"Start proc (?<pid>\d+).*\{(?<packageName>[^\s/]+)[/].*}");

        private void ProcessSystemMessage(AndroidLogEntry androidLogEntry)
        {
            if (androidLogEntry.Tag == "ActivityManager")
            {
                if (androidLogEntry.Message.StartsWith("Start proc"))
                {
                    var match = ActivityManagerStartProc.Match(androidLogEntry.Message);
                    if (match.Success)
                    {
                        _processNames.AddOrUpdate(
                            int.Parse(match.Groups["pid"].Value),
                            _ => match.Groups["packageName"].Value,
                            (_, _) => match.Groups["packageName"].Value);
                    }
                    else
                    {
                        Debug.WriteLine($"Regex failed to parse ActivityManager 'Start proc' message: {androidLogEntry.Message}");
                    }
                }
            }
        }
    }
}
