namespace InstantTraceViewer.Adb
{
    public sealed class AdbLogEntry
    {
        public int ProcessId { get; init; }

        public int ThreadId { get; init; }

        // UTC timestamp with sub-second precision. The logcat header's nanosecond field is folded in and rounded to the nearest 100ns tick.
        public DateTimeOffset TimeStamp { get; init; }

        public AdbLogPriority Priority { get; init; }

        public AdbLogId Id { get; init; }

        // The Linux UID of the process that emitted this log entry, or null if the device
        // sent a pre-v4 logger header (Android < 7.0) that did not include the UID field.
        public uint? Uid { get; init; }

        public string Tag { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;
    }
}