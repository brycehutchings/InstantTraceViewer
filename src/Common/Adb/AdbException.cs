namespace InstantTraceViewer.Adb
{
    public sealed class AdbException : Exception
    {
        public AdbException(string message)
            : base(message)
        {
        }
    }
}