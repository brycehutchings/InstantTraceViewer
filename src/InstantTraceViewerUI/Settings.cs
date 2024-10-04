using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InstantTraceViewerUI
{
    internal static class Settings
    {
        private static RegistryKey Key = Registry.CurrentUser.CreateSubKey(@"Software\InstantTraceViewerUI", true /* writable */);

        public static string WprpOpenLocation
        {
            get
            {
                return Key.GetValue("WprpOpenLocation", null) as string;
            }
            set
            {
                Key.SetValue("WprpOpenLocation", value);
            }
        }

        public static string EtlOpenLocation
        {
            get
            {
                return Key.GetValue("EtlOpenLocation") as string;
            }
            set
            {
                Key.SetValue("EtlOpenLocation", value);
            }
        }

        public static void AddRecentlyOpenedWprp(string file)
        {
            AddMru("RecentlyOpenedWprp", file);
        }

        public static IReadOnlyList<string> GetRecentlyOpenedWprp()
        {
            return GetMru("RecentlyOpenedWprp");
        }

        public static IReadOnlyList<string> GetMru(string settingsName)
        {
            var recentlyOpenedStr = Key.GetValue(settingsName, "") as string;
            var recentlyOpenedList = recentlyOpenedStr.Split(';').Where(File.Exists).ToList();
            return recentlyOpenedList;
        }

        public static void AddMru(string settingsName, string recentlyOpenedFile)
        {
            var recentlyOpenedStr = Key.GetValue(settingsName, "") as string;
            var recentlyOpenedList = recentlyOpenedStr.Split(';').ToList();
            recentlyOpenedList.Insert(0, recentlyOpenedFile);

            recentlyOpenedList = recentlyOpenedList.Distinct().ToList();
            if (recentlyOpenedList.Count > 10)
            {
                recentlyOpenedList.RemoveRange(10, recentlyOpenedList.Count - 10);
            }

            recentlyOpenedStr = string.Join(";", recentlyOpenedList);
            Key.SetValue(settingsName, recentlyOpenedStr);
        }
    }
}
