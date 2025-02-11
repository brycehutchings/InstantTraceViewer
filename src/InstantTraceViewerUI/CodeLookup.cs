using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InstantTraceViewerUI
{
    /// <summary>
    /// Look up frieldly names for error codes.
    /// </summary>
    internal static class CodeLookup
    {
        private static Dictionary<string /* field name */, ILookup<int /* error code */, string /* error code friendly name */>> _nameMappings;

        static CodeLookup()
        {
            List<(string fieldName, int errorCode, string friendlyName)> mappings = new();

            foreach (string filename in new[] { "FieldMap_HResults.tsv", "FieldMap_NTStatus.tsv", "FieldMap_Win32Errors.tsv" })
            {
                string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, filename));

                // First line of these files are field names that they may apply to.
                string[] fieldNames = lines[0].Split('\t');

                foreach (string line in lines.Skip(1))
                {
                    string[] fields = line.Split('\t');
                    if (fields.Length != 2)
                    {
                        throw new InvalidDataException($"Invalid row: {line}");
                    }

                    int code = int.Parse(fields[0]);
                    string friendlyName = fields[1];

                    foreach (var fieldName in fieldNames)
                    {
                        mappings.Add(new(fieldName, code, friendlyName));
                    }
                }
            }

            _nameMappings = mappings
                .GroupBy(m => m.fieldName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToLookup(m => m.errorCode, m => m.friendlyName), StringComparer.OrdinalIgnoreCase);
        }


        public static bool TryGetFriendlyName(string fieldName, int code, out string friendlyName)
        {
            ILookup<int /* error code */, string /* error code friendly name */> friendlyNameMap;
            if (!_nameMappings.TryGetValue(fieldName, out friendlyNameMap))
            {
                friendlyName = null;
                return false;
            }

            IEnumerator<string> friendlyNames = friendlyNameMap[code].GetEnumerator();
            if (!friendlyNames.MoveNext())
            {
                friendlyName = null;
                return false;
            }

            StringBuilder sb = new();
            do
            {
                if (sb.Length > 0)
                {
                    sb.Append(";");
                }
                sb.Append(friendlyNames.Current);
            }
            while (friendlyNames.MoveNext());

            friendlyName = sb.ToString();
            return true;
        }
    }
}
