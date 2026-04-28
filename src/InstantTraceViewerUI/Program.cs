using Hexa.NET.ImGui;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        public static unsafe int Main(string[] args)
        {
            Win32ImGuiHost.WindowInitialize();

            ImGuiContextPtr imguiContext = ImGui.CreateContext();
            ImGui.SetCurrentContext(imguiContext);
            Win32ImGuiHost.InitializeImGuiBackends(imguiContext);

            ImGuiIOPtr io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;

            FontType? lastSetFont = null;
            int? lastSetFontSize = null;
            ImGuiTheme? lastThemeSet = null;
            float lastDpiScale = 0;

            using (MainWindow mainWindow = new(args))
            {
                while (true)
                {
                    // Font can only change outside of Begin/End frame.
                    if (lastSetFont != Settings.Font)
                    {
                        ImGuiFontManager.LoadFontSources();
                        ImGuiFontManager.ApplyFontSize();
                        lastSetFont = Settings.Font;
                        lastSetFontSize = Settings.FontSize;
                    }
                    else if (lastSetFontSize != Settings.FontSize)
                    {
                        ImGuiFontManager.ApplyFontSize();
                        lastSetFontSize = Settings.FontSize;
                    }

                    if (lastThemeSet != Settings.Theme)
                    {
                        lastDpiScale = ApplyDpiScaledStyle(Win32ImGuiHost.GetDpiScale());
                        lastThemeSet = Settings.Theme;
                    }

                    float dpiScale = Win32ImGuiHost.GetDpiScale();
                    if (dpiScale != lastDpiScale)
                    {
                        lastDpiScale = ApplyDpiScaledStyle(dpiScale);
                    }

                    Win32ImGuiHost.WindowBeginNextFrame(out bool quit, out bool occluded);

                    if (quit)
                    {
                        break;
                    }

                    if (occluded)
                    {
                        System.Threading.Thread.Sleep(10);
                        continue;
                    }

#if PRIMARY_DOCKED_WINDOW
                    uint dockId = ImGui.DockSpaceOverViewport(0, new ImGuiViewportPtr(nint.Zero), ImGuiDockNodeFlags.NoDockingOverCentralNode | ImGuiDockNodeFlags.AutoHideTabBar);

                    // Force the next window to be docked.
                    ImGui.SetNextWindowDockID(dockId);
                    ImGuiWindowFlags flags = ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
                    if (ImGui.Begin("Window", flags))
                    {
                        ImGui.TextUnformatted("Hello World");
                    }
#endif
                    mainWindow.Draw();

                    if (mainWindow.IsExitRequested)
                    {
                        break;
                    }

                    Win32ImGuiHost.WindowEndNextFrame();
                }
            }

            Win32ImGuiHost.ShutdownImGuiBackends();
            ImGui.DestroyContext();
            ImGuiFontManager.FreePinnedFontData();
            Win32ImGuiHost.WindowCleanup();

            return 0;
        }

        private static unsafe float ApplyDpiScaledStyle(float dpiScale)
        {
            // Create a new default style and copy it over the current style to reset all fields to their default values before applying DPI scaling and theme colors.
            // This is necessary to avoid cumulative scaling when switching themes or when DPI changes while the app is running.
            ImGuiStylePtr defaultStyle = ImGui.ImGuiStyle();
            *ImGui.GetStyle().Handle = *defaultStyle.Handle;
            defaultStyle.Destroy();

            ImGui.GetStyle().ScrollbarSize = 18;
            AppTheme.UpdateTheme();
            ImGui.GetStyle().ScaleAllSizes(dpiScale);
            ImGuiFontManager.ApplyFontSize();
            return dpiScale;
        }
    }
}
