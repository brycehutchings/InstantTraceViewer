namespace InstantTraceViewer
{
    public static class FriendlyStringify
    {
        public static string ToString(TimeSpan timeSpan, bool includePositiveSign = false)
        {
            if (timeSpan.Ticks == 0)
            {
                return "0";
            }

            string sign = timeSpan.Ticks < 0 ? "-" : (includePositiveSign ? "+" : "");

            double totalMilliseconds = Math.Abs(timeSpan.TotalMilliseconds);
            if (totalMilliseconds < 1)
            {
                double totalMicroseconds = Math.Abs(timeSpan.TotalMicroseconds);
                return $"{sign}{totalMicroseconds:0.000}us";
            }

            double totalSeconds = Math.Abs(timeSpan.TotalSeconds);
            if (totalSeconds >= 60)
            {
                int totalMinutes = (int)(totalSeconds / 60);
                return $"{sign}{totalMinutes}m {totalSeconds - totalMinutes * 60:0.000}s";
            }
            else if (totalSeconds >= 1)
            {
                return $"{sign}{totalSeconds:0.000}s";
            }

            return $"{sign}{totalMilliseconds:0.000}ms";
        }

        // This version is 100ns precision and should have no rounding issues (DateTime is like FILETIME which uses 100ns units).
        // This is needed for comparison to work correctly (e.g. >= includes the datetime in question).
        public static string ToStringFull(DateTime dateTime, IFormatProvider? formatProvider = null) => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", formatProvider);

        public static string ToString(DateTime dateTime, IFormatProvider? formatProvider = null) => dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", formatProvider);

        // This version is 100ns precision and should have no rounding issues (DateTimeOffset is like FILETIME which uses 100ns units).
        // This is needed for comparison to work correctly (e.g. >= includes the datetime in question).
        public static string ToStringFull(DateTimeOffset dateTime, IFormatProvider? formatProvider = null) => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", formatProvider);

        public static string ToString(DateTimeOffset dateTime, IFormatProvider? formatProvider = null) => dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", formatProvider);
    }
}
