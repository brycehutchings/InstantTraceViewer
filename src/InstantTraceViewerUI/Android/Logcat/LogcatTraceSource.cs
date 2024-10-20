using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Logs;
using AdvancedSharpAdbClient.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

namespace InstantTraceViewerUI
{
    internal class LogcatTraceSource : ITraceSource
    {
        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private ImmutableList<TraceRecord>.Builder _traceRecords = ImmutableList.CreateBuilder<TraceRecord>();
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
                _traceRecords.Clear();
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
                    Records = _traceRecords.ToImmutable()
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

        public string GetOpCodeName(byte opCode)
        {
            return string.Empty;
        }

        public string GetProcessName(int processId)
        {
            return _processNames.TryGetValue(processId, out string name) && !string.IsNullOrEmpty(name) ? $"{processId} ({name})" : processId.ToString();

        }

        public string GetThreadName(int threadId)
        {
            return threadId.ToString();
        }

        private async void ReadLogcatThread(AdbClient adbClient, DeviceData device)
        {
            try
            {
                // RunLogServiceAsync will complete when logcat is cleared, so it is ran in a loop.
                while (true)
                {
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
                                Message = androidLogEntry.Message,
                                Name = androidLogEntry.Tag,
                                ProviderName = ""
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
