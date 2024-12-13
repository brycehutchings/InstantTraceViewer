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

            using var openWithKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.etl\OpenWithProgids");
            openWithKey.SetValue("", "InstantTraceViewerUI.etl");
            openWithKey.SetValue("InstantTraceViewerUI.etl", "");

            // Autologgers save the etl extensions with a number suffix. Associate a handful of them
            for (int i = 1; i <= 15; i++)
            {
                using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.{i:D3}\OpenWithProgids");
                extKey.SetValue("", "InstantTraceViewerUI.etl");
                extKey.SetValue("InstantTraceViewerUI.etl", "");
            }
        }
    }
}
