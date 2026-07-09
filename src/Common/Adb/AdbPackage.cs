namespace InstantTraceViewer.Adb
{
    public sealed class AdbPackage
    {
        public required string PackageName { get; init; }

        public required uint Uid { get; init; }
    }
}
