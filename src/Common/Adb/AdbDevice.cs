namespace InstantTraceViewer.Adb
{
    public sealed class AdbDevice
    {
        public required string Serial { get; init; }

        /// <summary>
        /// The device codename reported by adb (e.g. "emu64x"). Falls back to the model or serial when unavailable.
        /// </summary>
        public string Codename { get; init; } = string.Empty;

        public string Model { get; init; } = string.Empty;

        public string Product { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;
    }
}