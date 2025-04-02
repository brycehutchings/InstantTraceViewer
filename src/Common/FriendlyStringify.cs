namespace InstantTraceViewer
{
    public static class FriendlyStringify
    {
        public static string ToString(TimeSpan timeSpan)
        {
            if (timeSpan.Ticks == 0)
            {
                return "0s";
            }
            else if (timeSpan.TotalMilliseconds < 1)
            {
                return $"{timeSpan.TotalMicroseconds:0.000}us";
            }
            else if (timeSpan.TotalSeconds >= 60)
            {
                return $"{(int)timeSpan.TotalMinutes}m {timeSpan.TotalSeconds - (int)timeSpan.TotalMinutes * 60:0.000}s";
            }
            else if (timeSpan.TotalSeconds >= 1)
            {
                return $"{timeSpan.TotalSeconds:0.000}s";
            }
            else
            {
                return $"{timeSpan.TotalMilliseconds:0.000}ms";
            }
        }

        public static string ToString(DateTime dateTime, IFormatProvider? formatProvider = null) => dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", formatProvider);

        public static string ToString(DateTimeOffset dateTime, IFormatProvider? formatProvider = null) => dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", formatProvider);
    }
}
