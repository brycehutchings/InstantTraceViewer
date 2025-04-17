using InstantTraceViewerUI.Etw;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InstantTraceViewerUI
{
    internal enum FontType
    {
        SegoeUI,
        DroidSans,
        CascadiaMono,
        ProggyClean
    }

    internal static class Settings
    {
        private static RegistryKey Key = Registry.CurrentUser.CreateSubKey(@"Software\InstantTraceViewerUI", true /* writable */);

        private static FontType _cachedFont;
        private static int _cachedFontSize;
        private static ImGuiTheme _imguiTheme;

        static Settings()
        {
            _cachedFont = Enum.TryParse(Key.GetValue("Font", null) as string, out FontType font) ? font : FontType.SegoeUI;
            _cachedFontSize = (int)Key.GetValue("FontSize", 17);
            _imguiTheme = Enum.TryParse(Key.GetValue("Theme", null) as string, out ImGuiTheme theme) ? theme : ImGuiTheme.Light;
        }

        public static ImGuiTheme Theme
        {
            get => _imguiTheme;
            set
            {
                _imguiTheme = value;
                Key.SetValue("Theme", value.ToString());
            }
        }

        public static FontType Font
        {
            get => _cachedFont;
            set
            {
                _cachedFont = value;
                Key.SetValue("Font", value.ToString());
            }
        }

        public static int FontSize
        {
            get => _cachedFontSize;
            set
            {
                _cachedFontSize = value;
                Key.SetValue("FontSize", value);
            }
        }

        public static string? WprpOpenLocation
        {
            get
            {
                return Key.GetValue("WprpOpenLocation", null) as string;
            }
            set
            {
                Key.SetValue("WprpOpenLocation", value!);
            }
        }

        public static string? CsvOpenLocation
        {
            get
            {
                return Key.GetValue("CsvOpenLocation", null) as string;
            }
            set
            {
                Key.SetValue("CsvOpenLocation", value!);
            }
        }

        public static string? TsvOpenLocation
        {
            get
            {
                return Key.GetValue("TsvOpenLocation", null) as string;
            }
            set
            {
                Key.SetValue("TsvOpenLocation", value!);
            }
        }

        public static string? PerfettoOpenLocation
        {
            get
            {
                return Key.GetValue("PerfettoOpenLocation", null) as string;
            }
            set
            {
                Key.SetValue("PerfettoOpenLocation", value!);
            }
        }

        public static string? EtlOpenLocation
        {
            get
            {
                return Key.GetValue("EtlOpenLocation") as string;
            }
            set
            {
                Key.SetValue("EtlOpenLocation", value!);
            }
        }

        public static string? InstantTraceViewerFiltersLocation
        {
            get
            {
                var location = Key.GetValue("ItvfLocation", null) as string;
                if (string.IsNullOrEmpty(location))
                {
                    location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Instant Trace Viewer Filters");
                    try
                    {
                        if (!Path.Exists(location))
                        {
                            Directory.CreateDirectory(location);
                            return location;
                        }
                    }
                    catch
                    {
                    }
                }
                return location;
            }
            set
            {
                Key.SetValue("ItvfLocation", value!);
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

        public static void AssociateWithEtlExtensions()
        {
            string exePath = Path.Combine(AppContext.BaseDirectory, "InstantTraceViewerUI.exe");

            using var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\InstantTraceViewerUI.etl");
            using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
            using var openKey = progIdKey.CreateSubKey("shell\\open");
            using var commandKey = progIdKey.CreateSubKey("shell\\open\\command");

            progIdKey.SetValue("", "ETL Trace file");
            iconKey.SetValue("", Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.ico"));
            openKey.SetValue("Icon", $"\"{exePath}\"");
            commandKey.SetValue("", $"\"{exePath}\" \"%1\"");

            foreach (string ext in EtwTraceSource.EtlFileExtensions)
            {
                using var openWithKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.etl\OpenWithProgids");
                openWithKey.SetValue("", "InstantTraceViewerUI.etl");
                openWithKey.SetValue("InstantTraceViewerUI.etl", "");
            }
        }
    }
}
