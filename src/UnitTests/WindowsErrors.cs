namespace InstantTraceViewerTests
{
    /// <summary>
    /// Ok so this isn't really a test. I just needed a place to stash a small program to generate a
    /// Win32/HRESULT mapping table generator that could be rerun periodically.
    /// 
    /// WinError.h is a pretty messy file but I think this gets like 99.9% of things.
    /// 
    /// https://www.hresult.info/ is a nice website to sanity check the results.
    /// </summary>
    [TestClass]
    public class WindowsErrors
    {
        private static readonly char[] Separators = new char[] { ' ', '(', ')', ',' };
        private const string DefinePrefix = "#define ";

        [TestMethod]
        public void ParseWinError()
        {
            string[] winErrorLines = File.ReadAllLines(@"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared\winerror.h");

            List<Tuple<uint, string>> hrMap = new();
            List<Tuple<uint, string>> win32Map = new();

            foreach (string rawLine in winErrorLines)
            {
                if (!rawLine.StartsWith(DefinePrefix))
                {
                    continue;
                }

                // Remove any '//' comments on line
                string line = rawLine;
                int commentIndex = rawLine.IndexOf("//");
                if (commentIndex != -1)
                {
                    line = rawLine.Substring(0, commentIndex);
                }

                string[] tokens = line.Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3)
                {
                    Console.WriteLine($"Unusual winerror.h line being skipped: {rawLine}");
                    continue;
                }

                string name = tokens[1];
                bool isHResult = tokens[2] == "_HRESULT_TYPEDEF_" ||
                    tokens[2] == "HRESULT" ||
                    tokens[2] == "HRESULT_FROM_WIN32" ||
                    tokens[2] == "_NDIS_ERROR_TYPEDEF_";
                if (isHResult && tokens.Length < 4)
                {
                    Console.WriteLine($"Unusual winerror.h HRESULT line being skipped: {rawLine}");
                    continue;
                }

                string rawErrorValueString = isHResult ? tokens[3] : tokens[2];

                if (name.StartsWith("FACILITY_") || name.StartsWith("SEVERITY_"))
                {
                    continue; // Facility/severity codes are not error codes.
                }

                if (rawErrorValueString.EndsWith("L")) // L is a long constant, which we don't care about.
                {
                    rawErrorValueString = rawErrorValueString.Substring(0, rawErrorValueString.Length - 1);
                }

                uint errorCode;
                if (rawErrorValueString.StartsWith("0x"))
                {
                    if (!uint.TryParse(rawErrorValueString.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out errorCode))
                    {
                        Console.WriteLine("Skipping winerror.h line with unknown error code: " + rawLine);
                        continue;
                    }
                }
                else
                {
                    if (!uint.TryParse(rawErrorValueString, out errorCode))
                    {
                        Console.WriteLine("Skipping winerror.h line with unknown error code: " + rawLine);
                        continue;
                    }
                }

                if (isHResult || errorCode >= 0x80000000)
                {
                    hrMap.Add(Tuple.Create(errorCode, name));
                }
                else
                {
                    win32Map.Add(Tuple.Create(errorCode, name));
                    hrMap.Add(Tuple.Create((uint)unchecked((int)0x80070000 + errorCode), $"HRESULT_FROM_WIN32({name})")); // FACILITY_WIN32 is 7, so we need to add 0x80070000 to the error code to make it an HRESULT.
                }
            }

            string[] ntStatusLines = File.ReadAllLines(@"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared\ntstatus.h");
            List<Tuple<uint, string>> ntStatusMap = new();

            foreach (string rawLine in ntStatusLines)
            {
                if (!rawLine.StartsWith(DefinePrefix))
                {
                    continue;
                }

                // Remove any '//' comments on line
                string line = rawLine;
                int commentIndex = rawLine.IndexOf("//");
                if (commentIndex != -1)
                {
                    line = rawLine.Substring(0, commentIndex);
                }

                string[] tokens = line.Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 4)
                {
                    Console.WriteLine($"Unusual ntstatus.h line being skipped: {rawLine}");
                    continue;
                }

                string name = tokens[1];
                bool isNTStatus = tokens[2] == "NTSTATUS";
                if (!isNTStatus)
                {
                    Console.WriteLine($"Unusual ntstatus.h line being skipped: {rawLine}");
                    continue;
                }

                string rawErrorValueString = tokens[3];
                if (rawErrorValueString.EndsWith("L"))
                {
                    rawErrorValueString = rawErrorValueString.Substring(0, rawErrorValueString.Length - 1);
                }

                uint errorCode;
                if (rawErrorValueString.StartsWith("0x"))
                {
                    if (!uint.TryParse(rawErrorValueString.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out errorCode))
                    {
                        Console.WriteLine("Skipping ntstatus.h line with unknown error code: " + rawLine);
                        continue;
                    }
                }
                else
                {
                    if (!uint.TryParse(rawErrorValueString, out errorCode))
                    {
                        Console.WriteLine("Skipping ntstatus.h line with unknown error code: " + rawLine);
                        continue;
                    }
                }

                ntStatusMap.Add(Tuple.Create(errorCode, name));
                hrMap.Add(Tuple.Create(errorCode | 0x10000000, $"HRESULT_FROM_NT({name})"));
            }

            using StreamWriter hrMapWriter = new StreamWriter("FieldMap_HResults.txt");
            hrMapWriter.WriteLine("hr\thresult\terror\tresult"); // First row are field names
            foreach (var (errorCode, name) in hrMap.OrderBy(t => t.Item1))
            {
                hrMapWriter.WriteLine($"{(int)errorCode}\t{name}");
            }

            using StreamWriter win32MapWriter = new StreamWriter("FieldMap_Win32Errors.txt");
            win32MapWriter.WriteLine("error\tstatus\twin32error\tlasterror\tgetlasterror\tresult"); // First row are field names
            foreach (var (errorCode, name) in win32Map.OrderBy(t => t.Item1))
            {
                win32MapWriter.WriteLine($"{(int)errorCode}\t{name}");
            }

            using StreamWriter ntStatusWriter = new StreamWriter("FieldMap_NTStatus.txt");
            ntStatusWriter.WriteLine("error\tstatus\tntstatus"); // First row are field names
            foreach (var (errorCode, name) in ntStatusMap.OrderBy(t => t.Item1))
            {
                ntStatusWriter.WriteLine($"{(int)errorCode}\t{name}");
            }
        }
    }
}