using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace InstantTraceViewer.Adb
{
    internal sealed class AdbProtocolConnection : IDisposable
    {
        private readonly TcpClient _client;

        private AdbProtocolConnection(TcpClient client)
        {
            _client = client;
            Stream = client.GetStream();
        }

        public NetworkStream Stream { get; }

        public static async Task<AdbProtocolConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var client = new TcpClient();

            try
            {
                await client.ConnectAsync(host, port, cancellationToken);
                return new AdbProtocolConnection(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task SendRequestAsync(string request, CancellationToken cancellationToken)
        {
            var requestBytes = Encoding.UTF8.GetBytes(request);
            var lengthBytes = Encoding.ASCII.GetBytes(requestBytes.Length.ToString("X4", CultureInfo.InvariantCulture));

            await Stream.WriteAsync(lengthBytes, cancellationToken);
            await Stream.WriteAsync(requestBytes, cancellationToken);
            await ReadStatusAsync(cancellationToken);
        }

        public async Task<string> ReadLengthPrefixedStringAsync(CancellationToken cancellationToken)
        {
            var responseBytes = await ReadLengthPrefixedBytesAsync(cancellationToken);
            return Encoding.UTF8.GetString(responseBytes);
        }

        public async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];

            while (true)
            {
                var bytesRead = await Stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    return Encoding.UTF8.GetString(output.ToArray());
                }

                output.Write(buffer, 0, bytesRead);
            }
        }

        private async Task ReadStatusAsync(CancellationToken cancellationToken)
        {
            var statusBytes = new byte[4];
            await ReadExactAsync(statusBytes, cancellationToken);

            var status = Encoding.ASCII.GetString(statusBytes);
            if (status == "OKAY")
            {
                return;
            }

            if (status == "FAIL")
            {
                var message = await ReadLengthPrefixedStringAsync(cancellationToken);
                throw new AdbException(message);
            }

            throw new AdbException($"Unexpected ADB status '{status}'.");
        }

        private async Task<byte[]> ReadLengthPrefixedBytesAsync(CancellationToken cancellationToken)
        {
            var lengthBytes = new byte[4];
            await ReadExactAsync(lengthBytes, cancellationToken);

            var lengthText = Encoding.ASCII.GetString(lengthBytes);
            if (!int.TryParse(lengthText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length))
            {
                throw new AdbException($"Invalid ADB response length '{lengthText}'.");
            }

            var responseBytes = new byte[length];
            await ReadExactAsync(responseBytes, cancellationToken);
            return responseBytes;
        }

        private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var totalBytesRead = 0;
            while (totalBytesRead < buffer.Length)
            {
                var bytesRead = await Stream.ReadAsync(buffer[totalBytesRead..], cancellationToken);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException();
                }

                totalBytesRead += bytesRead;
            }
        }

        public void Dispose()
        {
            Stream.Dispose();
            _client.Dispose();
        }
    }
}