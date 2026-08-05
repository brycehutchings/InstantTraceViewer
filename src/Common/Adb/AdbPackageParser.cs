namespace InstantTraceViewer.Adb
{
    public static class AdbPackageParser
    {
        // Parses output of `pm list packages -U`, lines like:
        //   package:com.example.app uid:10123
        // Some devices emit trailing whitespace or omit the uid on very old builds; those lines are skipped.
        public static IReadOnlyList<AdbPackage> Parse(string packageList)
        {
            var packages = new List<AdbPackage>();

            foreach (var line in packageList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("package:", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                var packageName = parts[0]["package:".Length..];
                if (packageName.Length == 0)
                {
                    continue;
                }

                uint? uid = null;
                foreach (var part in parts.AsSpan(1))
                {
                    if (part.StartsWith("uid:", StringComparison.Ordinal) &&
                        uint.TryParse(part.AsSpan("uid:".Length), out var parsedUid))
                    {
                        uid = parsedUid;
                        break;
                    }
                }

                if (uid == null)
                {
                    continue;
                }

                packages.Add(new AdbPackage { PackageName = packageName, Uid = uid.Value });
            }

            return packages;
        }
    }
}
