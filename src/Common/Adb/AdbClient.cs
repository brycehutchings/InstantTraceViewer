using System.Runtime.CompilerServices;
using System.Text;

namespace InstantTraceViewer.Adb
{
    public sealed class AdbClient
    {
        private const string DefaultHost = "127.0.0.1";
        private const int DefaultPort = 5037;

        private readonly string _host;
        private readonly int _port;

        public AdbClient()
            : this(DefaultHost, DefaultPort)
        {
        }

        public AdbClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            using var connection = await AdbProtocolConnection.ConnectAsync(_host, _port, cancellationToken);
            await connection.SendRequestAsync("host:devices-l", cancellationToken);

            var response = await connection.ReadLengthPrefixedStringAsync(cancellationToken);
            return AdbDeviceListParser.Parse(response);
        }

        public async IAsyncEnumerable<IReadOnlyList<AdbDevice>> TrackDevicesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var connection = await AdbProtocolConnection.ConnectAsync(_host, _port, cancellationToken);
            await connection.SendRequestAsync("host:track-devices-l", cancellationToken);

            while (true)
            {
                string response;
                try
                {
                    response = await connection.ReadLengthPrefixedStringAsync(cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    yield break;
                }

                yield return AdbDeviceListParser.Parse(response);
            }
        }

        /// <summary>
        /// Runs a shell command on the device and returns its combined output.
        /// The command string is passed verbatim to <c>sh -c</c>; callers are responsible for
        /// quoting/escaping any user-supplied arguments to prevent shell injection.
        /// </summary>
        public async Task<string> ExecuteShellCommandAsync(AdbDevice device, string command, CancellationToken cancellationToken)
        {
            using var connection = await OpenDeviceServiceAsync(device, $"shell:{command}", cancellationToken);
            return await connection.ReadToEndAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<AdbProcess>> ListProcessesAsync(AdbDevice device, CancellationToken cancellationToken)
        {
            var processList = await ExecuteShellCommandAsync(device, "ps -A -o PID,NAME 2>/dev/null || ps", cancellationToken);
            return AdbProcessParser.Parse(processList);
        }

        public async Task<IReadOnlyList<AdbPackage>> ListPackagesAsync(AdbDevice device, CancellationToken cancellationToken)
        {
            var packageList = await ExecuteShellCommandAsync(device, "pm list packages -U", cancellationToken);
            return AdbPackageParser.Parse(packageList);
        }

        public async IAsyncEnumerable<AdbLogEntry> RunLogServiceAsync(
            AdbDevice device,
            IReadOnlyList<AdbLogId> logIds,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Use the "exec:" service (raw pipe, no PTY) instead of "shell:" for the binary "-B"
            // stream. "shell:" merges the command's stderr into stdout and can apply PTY newline
            // translation, both of which corrupt the binary output.
            using var connection = await OpenDeviceServiceAsync(device, $"exec:{BuildLogcatCommand(logIds)}", cancellationToken);
            var parser = new AdbLogcatBinaryParser();

            while (true)
            {
                var entry = await parser.ReadEntryAsync(connection.Stream, cancellationToken);
                if (entry == null)
                {
                    yield break;
                }

                yield return entry;
            }
        }

        private async Task<AdbProtocolConnection> OpenDeviceServiceAsync(AdbDevice device, string service, CancellationToken cancellationToken)
        {
            var connection = await AdbProtocolConnection.ConnectAsync(_host, _port, cancellationToken);

            try
            {
                await connection.SendRequestAsync($"host:transport:{device.Serial}", cancellationToken);
                await connection.SendRequestAsync(service, cancellationToken);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static string BuildLogcatCommand(IReadOnlyList<AdbLogId> logIds)
        {
            var command = new StringBuilder("logcat -B");

            foreach (var logId in logIds.Distinct())
            {
                var bufferName = GetLogcatBufferName(logId);
                if (bufferName != null)
                {
                    command.Append(" -b ");
                    command.Append(bufferName);
                }
            }

            return command.ToString();
        }

        // Buffer names accepted by `logcat -b` happen to be the lowercase enum names.
        private static string? GetLogcatBufferName(AdbLogId logId)
            => logId == AdbLogId.Unknown ? null : logId.ToString().ToLowerInvariant();
    }
}