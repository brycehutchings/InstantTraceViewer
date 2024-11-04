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

namespace InstantTraceViewerUI
{
    internal class LogcatTraceSource : ITraceSource
    {
        private static readonly TraceSourceSchemaColumn ColumnProcess = new TraceSourceSchemaColumn { Name = "Process", DefaultColumnSize = 3.75f };
        private static readonly TraceSourceSchemaColumn ColumnThread = new TraceSourceSchemaColumn { Name = "Thread", DefaultColumnSize = 3.75f };
        private static readonly TraceSourceSchemaColumn ColumnTag = new TraceSourceSchemaColumn { Name = "Tag", DefaultColumnSize = 8.75f };
        private static readonly TraceSourceSchemaColumn ColumnPriority = new TraceSourceSchemaColumn { Name = "Priority", DefaultColumnSize = 3.75f };
        private static readonly TraceSourceSchemaColumn ColumnTime = new TraceSourceSchemaColumn { Name = "Time", DefaultColumnSize = 5.75f };
        private static readonly TraceSourceSchemaColumn ColumnMessage = new TraceSourceSchemaColumn { Name = "Message", DefaultColumnSize = null };

        private static readonly TraceSourceSchema _schema = new TraceSourceSchema
        {
            Columns = [ColumnProcess, ColumnThread, ColumnTag, ColumnPriority, ColumnTime, ColumnMessage]
        };

        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private ListBuilder<TraceRecord> _traceRecords = new ListBuilder<TraceRecord>();
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

        public TraceRecordSnapshot CreateSnapshot()
        {
            // ToImmutable and Add on the ImmutableList appear to not be threadsafe. Once we make an immutable list, it should be safe to continue modifying the builder.
            _traceRecordsLock.EnterReadLock();
            try
            {
                return new TraceRecordSnapshot
                {
                    GenerationId = _generationId,
                    Records = _traceRecords.CreateSnapshot()
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

        public TraceSourceSchema Schema => _schema;

        public string GetColumnString(TraceRecord traceRecord, TraceSourceSchemaColumn column, bool allowMultiline = false)
        {
            if (column == ColumnProcess)
            {
                return
                    traceRecord.ProcessId == -1 ? string.Empty :
                    _processNames.TryGetValue(traceRecord.ProcessId, out string name) && !string.IsNullOrEmpty(name) ? $"{traceRecord.ProcessId} ({name})" : traceRecord.ProcessId.ToString();
            }
            else if (column == ColumnThread)
            {
                return traceRecord.ThreadId.ToString();
            }
            else if (column == ColumnPriority)
            {
                return traceRecord.Level.ToString();
            }
            else if (column == ColumnTime)
            {
                return traceRecord.Timestamp.ToString("HH:mm:ss.ffffff");
            }
            else if (column == ColumnTag)
            {
                return traceRecord.Name;
            }
            else if (column == ColumnMessage)
            {
                Debug.Assert(traceRecord.NamedValues.Length == 1);
                return (string)traceRecord.NamedValues[0].Value;
            }

            throw new NotImplementedException();
        }

        private async void ReadLogcatThread(AdbClient adbClient, DeviceData device)
        {
            try
            {
                // TODO: Which logs to look at?
                await foreach (LogEntry logEntry in adbClient.RunLogServiceAsync(device, _tokenSource.Token, new[] { LogId.Main, LogId.Crash, LogId.Kernel, LogId.System, LogId.Security, LogId.Radio }))
                {
                    if (logEntry is AndroidLogEntry androidLogEntry)
                    {
                        ProcessSystemMessage(androidLogEntry);

                        var preciseTimestamp = androidLogEntry.TimeStamp.ToLocalTime() + TimeSpan.FromTicks(androidLogEntry.NanoSeconds / TimeSpan.NanosecondsPerTick);
                        var traceRecord = new TraceRecord
                        {
                            ProcessId = androidLogEntry.ProcessId,
                            ThreadId = (int)androidLogEntry.ThreadId,
                            Timestamp = preciseTimestamp.DateTime,
                            Level =
                                androidLogEntry.Priority == Priority.Fatal ? TraceLevel.Critical :
                                androidLogEntry.Priority == Priority.Error ? TraceLevel.Error :
                                androidLogEntry.Priority == Priority.Assert ? TraceLevel.Error :
                                androidLogEntry.Priority == Priority.Warn ? TraceLevel.Warning :
                                androidLogEntry.Priority == Priority.Verbose ? TraceLevel.Verbose :
                                androidLogEntry.Priority == Priority.Debug ? TraceLevel.Verbose :       // TODO: Should we add a Debug trace level to map into?
                                                                                TraceLevel.Info,
                            NamedValues = [new NamedValue { Value = androidLogEntry.Message }],
                            Name = androidLogEntry.Tag,
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
