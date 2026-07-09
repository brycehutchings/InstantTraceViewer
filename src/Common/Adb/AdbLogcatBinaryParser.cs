using System.Buffers.Binary;
using System.Text;

namespace InstantTraceViewer.Adb
{
    public sealed class AdbLogcatBinaryParser
    {
        private const int MinimumHeaderSize = 20;
        private const int MaximumHeaderSize = 512;

        public async ValueTask<AdbLogEntry?> ReadEntryAsync(Stream stream, CancellationToken cancellationToken)
        {
            var prefix = new byte[4];
            if (!await ReadExactOrEndAsync(stream, prefix, cancellationToken))
            {
                return null;
            }

            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(0, 2));
            var headerSizeOrPadding = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(2, 2));
            var headerSize = headerSizeOrPadding >= MinimumHeaderSize && headerSizeOrPadding <= MaximumHeaderSize ? headerSizeOrPadding : MinimumHeaderSize;

            var header = new byte[headerSize];
            prefix.CopyTo(header.AsSpan(0, prefix.Length));
            await ReadExactAsync(stream, header.AsMemory(prefix.Length), cancellationToken);

            var payload = new byte[payloadLength];
            await ReadExactAsync(stream, payload, cancellationToken);

            return ParseEntry(header, payload);
        }

        public static AdbLogEntry ParseEntry(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
        {
            if (header.Length < MinimumHeaderSize)
            {
                throw new InvalidDataException($"Logcat header length {header.Length} is too small.");
            }

            var processId = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
            var threadId = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
            var seconds = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
            var nanoSeconds = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
            var logId = AdbLogId.Unknown;
            uint? uid = null;

            // v2/v3 headers (>= 24 bytes) add the log-id field.
            if (header.Length >= 24)
            {
                var rawLogId = BinaryPrimitives.ReadInt32LittleEndian(header[20..24]);
                if (Enum.IsDefined(typeof(AdbLogId), rawLogId))
                {
                    logId = (AdbLogId)rawLogId;
                }
            }

            // v4 headers (>= 28 bytes, Android 7.0+) add the emitting process's UID.
            if (header.Length >= 28)
            {
                uid = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
            }

            ParsePayload(payload, out var priority, out var tag, out var message);

            var timeStamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanoSeconds / TimeSpan.NanosecondsPerTick);

            return new AdbLogEntry
            {
                ProcessId = processId,
                ThreadId = threadId,
                TimeStamp = timeStamp,
                Priority = priority,
                Id = logId,
                Uid = uid,
                Tag = tag,
                Message = message,
            };
        }

        // Text-format payload layout: [priority][tag]\0[message]\0. Binary event buffers
        // (Events/Stats and older Security) use a different, typed format and are not decoded here;
        // requesting those buffers will produce empty or garbled Tag/Message fields.
        private static void ParsePayload(ReadOnlySpan<byte> payload, out AdbLogPriority priority, out string tag, out string message)
        {
            priority = AdbLogPriority.Unknown;
            tag = string.Empty;
            message = string.Empty;

            if (payload.Length == 0)
            {
                return;
            }

            priority = ParsePriority(payload[0]);

            var tagStart = 1;
            var tagLength = payload[tagStart..].IndexOf((byte)0);
            if (tagLength < 0)
            {
                // No null-terminator; payload is not text-formatted (e.g. binary event log).
                return;
            }

            var messageStart = tagStart + tagLength + 1;
            var messageLength = payload[messageStart..].IndexOf((byte)0);
            if (messageLength < 0)
            {
                messageLength = payload.Length - messageStart;
            }

            tag = Encoding.UTF8.GetString(payload.Slice(tagStart, tagLength));
            message = Encoding.UTF8.GetString(payload.Slice(messageStart, messageLength));
        }

        private static AdbLogPriority ParsePriority(byte priority)
            => priority == 0 ? AdbLogPriority.Unknown :
               priority == 1 ? AdbLogPriority.Default :
               priority == 2 ? AdbLogPriority.Verbose :
               priority == 3 ? AdbLogPriority.Debug :
               priority == 4 ? AdbLogPriority.Info :
               priority == 5 ? AdbLogPriority.Warn :
               priority == 6 ? AdbLogPriority.Error :
               priority == 7 ? AdbLogPriority.Fatal :
               priority == 8 ? AdbLogPriority.Silent : AdbLogPriority.Unknown;

        private static async ValueTask<bool> ReadExactOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var totalBytesRead = 0;
            while (totalBytesRead < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(buffer[totalBytesRead..], cancellationToken);
                if (bytesRead == 0)
                {
                    if (totalBytesRead == 0)
                    {
                        return false;
                    }

                    throw new EndOfStreamException();
                }

                totalBytesRead += bytesRead;
            }

            return true;
        }

        private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await ReadExactOrEndAsync(stream, buffer, cancellationToken))
            {
                throw new EndOfStreamException();
            }
        }
    }
}