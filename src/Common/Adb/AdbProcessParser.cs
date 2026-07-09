namespace InstantTraceViewer.Adb
{
    public static class AdbProcessParser
    {
        public static IReadOnlyList<AdbProcess> Parse(string processList)
        {
            var processes = new List<AdbProcess>();
            var seenProcessIds = new HashSet<int>();

            foreach (var line in processList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                if (TryParseModernPsLine(parts, out var process) || TryParseLegacyPsLine(parts, out process))
                {
                    if (seenProcessIds.Add(process!.ProcessId))
                    {
                        processes.Add(process);
                    }
                }
            }

            return processes;
        }

        // Modern layout from `ps -A -o PID,NAME`: exactly two columns.
        private static bool TryParseModernPsLine(string[] parts, out AdbProcess? process)
        {
            process = null;

            if (parts.Length != 2 || !int.TryParse(parts[0], out var processId))
            {
                return false;
            }

            process = new AdbProcess { ProcessId = processId, Name = parts[1] };
            return true;
        }

        // Legacy toolbox layout: `USER PID PPID VSIZE RSS WCHAN ADDR S NAME` (9+ columns). Requires
        // enough columns to distinguish it from the modern two-column layout so we don't accept
        // arbitrary lines whose second token happens to be numeric.
        private static bool TryParseLegacyPsLine(string[] parts, out AdbProcess? process)
        {
            process = null;

            if (parts.Length < 9 || !int.TryParse(parts[1], out var processId))
            {
                return false;
            }

            process = new AdbProcess { ProcessId = processId, Name = parts[^1] };
            return true;
        }
    }
}