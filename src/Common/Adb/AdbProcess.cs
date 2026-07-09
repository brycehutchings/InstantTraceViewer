namespace InstantTraceViewer.Adb
{
    public sealed class AdbProcess
    {
        public required int ProcessId { get; init; }

        public required string Name { get; init; }
    }
}