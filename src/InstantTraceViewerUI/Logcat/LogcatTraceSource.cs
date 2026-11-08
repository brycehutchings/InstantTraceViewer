using Hexa.NET.ImGui;
using InstantTraceViewer;
using InstantTraceViewer.Adb;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InstantTraceViewerUI.Logcat
{
    internal class LogcatTraceSource : ITraceSource, ITraceSourceGuiExtensions
    {
        public static readonly TraceSourceSchemaColumn ColumnProcess = new TraceSourceSchemaColumn { Name = "Process", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnThread = new TraceSourceSchemaColumn { Name = "Thread", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnUid = new TraceSourceSchemaColumn { Name = "Uid", DefaultColumnSize = 8.75f };
        public static readonly TraceSourceSchemaColumn ColumnBufferId = new TraceSourceSchemaColumn { Name = "BufferId", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnTag = new TraceSourceSchemaColumn { Name = "Tag", Colorize = true, DefaultColumnSize = 8.75f };
        public static readonly TraceSourceSchemaColumn ColumnPriority = new TraceSourceSchemaColumn { Name = "Priority", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnTime = new TraceSourceSchemaColumn { Name = "Time", DefaultColumnSize = 9.00f };
        public static readonly TraceSourceSchemaColumn ColumnMessage = new TraceSourceSchemaColumn { Name = "Message", DefaultColumnSize = null };

        private static readonly TraceTableSchema _schema = new TraceTableSchema
        {
            Columns = [ColumnProcess, ColumnThread, ColumnUid, ColumnBufferId, ColumnTag, ColumnPriority, ColumnTime, ColumnMessage],
            TimestampColumn = ColumnTime,
            UnifiedLevelColumn = ColumnPriority,
            ProcessIdColumn = ColumnProcess,
            ThreadIdColumn = ColumnThread,
            UidColumn = ColumnUid,
            ProviderColumn = ColumnBufferId,
            NameColumn = ColumnTag,
        };

        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private ListBuilder<LogcatRecord> _traceRecords = new ListBuilder<LogcatRecord>();
        private int _generationId = 0;
        private readonly CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private readonly AdbClient _adbClient;
        private readonly AdbDevice _device;
        private DateTimeOffset? _lastReceivedTimestamp;
        private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

        private enum ConnectionState
        {
            Connecting,
            Connected,
            WaitingToReconnect,
        }

        private volatile ConnectionState _connectionState; // Starts as Connecting.
        private volatile string _connectionError;
        private readonly SemaphoreSlim _reconnectRequested = new SemaphoreSlim(0, 1);

        private readonly ConcurrentDictionary<int, string> _processNames = new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<uint, string> _uidPackageNames = new ConcurrentDictionary<uint, string>();

        // Well-known Android UIDs (AIDs) from android_filesystem_config.h. These are shared across
        // many system packages (sharedUserId), so listing every package under them is noise -- we
        // display the canonical AID name instead. Applies only to UIDs below FIRST_APPLICATION_UID
        // (10000); user-installed apps always use their package name.
        internal static readonly IReadOnlyDictionary<uint, string> KnownSystemUidNames = new Dictionary<uint, string>
        {
            [0] = "root",
            [1000] = "system",
            [1001] = "radio",
            [1002] = "bluetooth",
            [1003] = "graphics",
            [1004] = "input",
            [1005] = "audio",
            [1006] = "camera",
            [1007] = "log",
            [1009] = "mount",
            [1010] = "wifi",
            [1011] = "adb",
            [1013] = "media",
            [1014] = "dhcp",
            [1019] = "drm",
            [1020] = "mdnsr",
            [1021] = "gps",
            [1023] = "media_rw",
            [1024] = "mtp",
            [1027] = "nfc",
            [1036] = "logd",
            [1041] = "audioserver",
            [1046] = "mediacodec",
            [1047] = "cameraserver",
            [1053] = "webview_zygote",
            [1058] = "tombstoned",
            [1066] = "statsd",
            [1067] = "incidentd",
            [1069] = "lmkd",
            [1073] = "network_stack",
            [2000] = "shell",
            [9999] = "nobody",
        };

        public LogcatTraceSource(AdbClient adbClient, AdbDevice device)
        {
            _adbClient = adbClient;
            _device = device;
            Task.Run(RunLogcatAsync);
        }

        public string DisplayName => $"{_device.Product} {_device.Model} {_device.Serial} (Logcat)";

        public bool CanClear => _connectionState == ConnectionState.Connected;

        public void Clear()
        {
            _adbClient.ExecuteShellCommandAsync(_device, "logcat -c", CancellationToken.None).GetAwaiter().GetResult();

            // Refresh the process and package name maps.
            RefreshProcessAndPackageNames();

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

        private void RefreshProcessAndPackageNames()
            => RefreshProcessAndPackageNamesAsync(CancellationToken.None).GetAwaiter().GetResult();

        private async Task RefreshProcessAndPackageNamesAsync(CancellationToken cancellationToken)
        {
            foreach (var process in await _adbClient.ListProcessesAsync(_device, cancellationToken))
            {
                _processNames.AddOrUpdate(process.ProcessId, _ => process.Name, (_, _) => process.Name);
            }

            // `pm list packages -U` groups multiple package names under the same UID when a sharedUserId is in use
            // (e.g. UID 1000 covers many system packages). Concatenate them so the UID column still surfaces every mapped name.
            foreach (var package in await _adbClient.ListPackagesAsync(_device, cancellationToken))
            {
                _uidPackageNames.AddOrUpdate(
                    package.Uid,
                    _ => package.PackageName,
                    (_, existing) => ContainsPackageToken(existing, package.PackageName) ? existing : $"{existing},{package.PackageName}");
            }
        }

        // Returns true if commaJoinedPackages already lists candidate as a comma-separated token. Wrapping both strings in commas
        // avoids substring false positives (e.g. "com.example" being considered "contained in" "com.example.foo").
        private static bool ContainsPackageToken(string commaJoinedPackages, string candidate)
            => $",{commaJoinedPackages},".Contains($",{candidate},", StringComparison.Ordinal);

        public bool CanPause => true;
        public bool IsPaused { get; private set; }
        public void TogglePause()
        {
            IsPaused = !IsPaused;
        }

        // Logcat is real-time which means it's expected that data never stops coming in, so no need to indicate to user it is loading.
        public bool IsPreprocessingData => false;

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

        public void RenderToolstripExtras(IUiCommands uiCommands)
        {
            if (_tokenSource.IsCancellationRequested)
            {
                return;
            }

            var state = _connectionState;
            if (state == ConnectionState.Connected)
            {
                return;
            }

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, AppTheme.WarningColor);
            ImGui.BeginDisabled(state == ConnectionState.Connecting);
            if (ImGui.Button(state == ConnectionState.Connecting ? "Connecting to Logcat..." : "Reconnect") && _reconnectRequested.CurrentCount == 0)
            {
                _reconnectRequested.Release();
            }
            ImGui.EndDisabled();
            ImGui.PopStyleColor();
            string error = _connectionError;
            if (error != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(error);
                ImGui.EndTooltip();
            }
        }

        public void RenderActiveWindows(IUiCommands uiCommands)
        {
        }

        // Reads logcat until disposed, reconnecting every ReconnectInterval (or immediately when the user clicks) whenever the stream ends or fails.
        private async Task RunLogcatAsync()
        {
            try
            {
                while (true)
                {
                    _connectionState = ConnectionState.Connecting;
                    using var attemptTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_tokenSource.Token);
                    attemptTokenSource.CancelAfter(ConnectionTimeout);
                    try
                    {
                        await ReadLogcatAsync(attemptTokenSource.Token, () =>
                        {
                            // Limit setup time, but allow a connected stream to remain idle indefinitely.
                            attemptTokenSource.CancelAfter(Timeout.InfiniteTimeSpan);
                            _connectionState = ConnectionState.Connected;
                        });
                        _connectionError = "Logcat stream ended unexpectedly.";
                    }
                    catch (OperationCanceledException) when (!_tokenSource.IsCancellationRequested)
                    {
                        _connectionError = $"Connecting to Logcat timed out after {ConnectionTimeout.TotalSeconds} seconds.";
                    }
                    catch (Exception ex)
                    {
                        // This can happen if the ADB server is killed or the device is unplugged. Disposal is handled by the wait below.
                        _connectionError = $"Unexpected error occurred while reading logcat:\n\n{ex}";
                    }

                    // Discard a click that raced with the previous wait ending.
                    _reconnectRequested.Wait(0);
                    _connectionState = ConnectionState.WaitingToReconnect;
                    await _reconnectRequested.WaitAsync(ReconnectInterval, _tokenSource.Token);
                }
            }
            catch (OperationCanceledException) when (_tokenSource.IsCancellationRequested)
            {
                // Trace source is being disposed because the user is closing us.
            }
        }

        private async Task ReadLogcatAsync(CancellationToken cancellationToken, Action onConnected)
        {
            void AddTraceRecord(LogcatRecord record)
            {
                _traceRecordsLock.EnterWriteLock();
                try
                {
                    _traceRecords.Add(record);
                }
                finally
                {
                    _traceRecordsLock.ExitWriteLock();
                }
            }

            await RefreshProcessAndPackageNamesAsync(cancellationToken);

            await foreach (AdbLogEntry androidLogEntry in _adbClient.RunLogServiceAsync(
                _device,
                [AdbLogId.Main, AdbLogId.Crash, AdbLogId.System, AdbLogId.Security, AdbLogId.Radio],
                cancellationToken,
                afterTimestamp: _lastReceivedTimestamp,
                onConnected: onConnected))
            {
                // Advance the timestamp even while paused so reconnect does not replay intentionally skipped events.
                // Retain this high-water mark across Clear and reconnect to avoid replaying previously received events.
                if (!_lastReceivedTimestamp.HasValue || androidLogEntry.TimeStamp > _lastReceivedTimestamp.Value)
                {
                    _lastReceivedTimestamp = androidLogEntry.TimeStamp;
                }

                if (IsPaused)
                {
                    continue;
                }

                ProcessSystemMessage(androidLogEntry);

                _processNames.TryGetValue(androidLogEntry.ProcessId, out string processName);
                string packageName = null;
                if (androidLogEntry.Uid.HasValue)
                {
                    _uidPackageNames.TryGetValue(androidLogEntry.Uid.Value, out packageName);
                }

                AddTraceRecord(new LogcatRecord
                {
                    ProcessId = androidLogEntry.ProcessId,
                    ProcessName = processName,
                    ThreadId = androidLogEntry.ThreadId,
                    Uid = androidLogEntry.Uid,
                    PackageName = packageName,
                    Timestamp = androidLogEntry.TimeStamp.ToLocalTime().DateTime,
                    Priority = androidLogEntry.Priority,
                    Message = androidLogEntry.Message,
                    Tag = androidLogEntry.Tag,
                    LogId = androidLogEntry.Id,
                });
            }
        }

        // Matches messages like:
        //   Start proc 12345:com.example.app/u0a123 for activity {com.example.app/com.example.app.MainActivity}
        // procName and uidToken come from the "<pid>:<procName>/<uidToken>" prefix (Android 8+); they are
        // optional so pre-8 formats still match, in which case procName falls back to packageName below.
        // procName can differ from packageName when an app declares a process suffix, e.g. "com.example.app:remoteworker".
        private static readonly Regex ActivityManagerStartProc = new Regex(@"Start proc (?<pid>\d+)(?::(?<procName>[^\s/]+)/(?<uidToken>\S+))?.*\{(?<packageName>[^\s/]+)[/].*}");

        private void ProcessSystemMessage(AdbLogEntry androidLogEntry)
        {
            if (androidLogEntry.Tag == "ActivityManager")
            {
                if (androidLogEntry.Message.StartsWith("Start proc"))
                {
                    var match = ActivityManagerStartProc.Match(androidLogEntry.Message);
                    if (match.Success)
                    {
                        var packageName = match.Groups["packageName"].Value;
                        var procName = match.Groups["procName"].Success ? match.Groups["procName"].Value : packageName;

                        _processNames.AddOrUpdate(
                            int.Parse(match.Groups["pid"].Value),
                            _ => procName,
                            (_, _) => procName);

                        if (match.Groups["uidToken"].Success)
                        {
                            var uid = DecodeAndroidUidToken(match.Groups["uidToken"].Value);
                            if (uid.HasValue)
                            {
                                _uidPackageNames.AddOrUpdate(
                                    uid.Value,
                                    _ => packageName,
                                    (_, existing) => ContainsPackageToken(existing, packageName) ? existing : $"{existing},{packageName}");
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"Regex failed to parse ActivityManager 'Start proc' message: {androidLogEntry.Message}");
                    }
                }
            }
        }

        // Decodes Android's UserHandle.formatUid() token into a Linux UID.
        //   bare integer     -> that integer (system UIDs below FIRST_APPLICATION_UID = 10000)
        //   u<user>a<n>      -> user*100000 + 10000 + n    (installed app)
        //   u<user>s<n>      -> user*100000 + n            (shared system UID inside a user profile, n < 10000)
        //   u<user>i<n>      -> user*100000 + 99000 + n    (isolated process)
        internal static uint? DecodeAndroidUidToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            if (uint.TryParse(token, out var bare))
            {
                return bare;
            }

            if (token[0] != 'u' || token.Length < 4)
            {
                return null;
            }

            int userEnd = 1;
            while (userEnd < token.Length && char.IsDigit(token[userEnd]))
            {
                userEnd++;
            }

            if (userEnd == 1 || userEnd >= token.Length - 1)
            {
                return null;
            }

            if (!uint.TryParse(token.AsSpan(1, userEnd - 1), out var userId))
            {
                return null;
            }

            uint offset;
            switch (token[userEnd])
            {
                case 'a': offset = 10000; break;
                case 's': offset = 0; break;
                case 'i': offset = 99000; break;
                default: return null;
            }

            if (!uint.TryParse(token.AsSpan(userEnd + 1), out var appId))
            {
                return null;
            }

            return userId * 100000u + offset + appId;
        }
    }
}
