using System;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        [STAThread] // For WinForms file browser usage :-\
        public static int Main(string[] args)
        {
            if (!NativeInterop.WindowInitialize(out nint imguiContext))
            {
                return 1;
            }

            ImGui.SetCurrentContext(imguiContext);

            // Increase scrollbar size to make it easier to use.
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 18.0f);

            using var mainWindow = new MainWindow(args);

            while (true)
            {
                if (!NativeInterop.WindowBeginNextFrame(out bool quit) || quit)
                {
                    break;
                }

#if PRIMARY_DOCKED_WINDOW
                    uint dockId = ImGui.DockSpaceOverViewport(0, new ImGuiViewportPtr(nint.Zero), ImGuiDockNodeFlags.NoDockingOverCentralNode | ImGuiDockNodeFlags.AutoHideTabBar);

                    // Force the next window to be docked.
                    ImGui.SetNextWindowDockID(dockId);
                    ImGuiWindowFlags flags = ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
                    if (ImGui.Begin("Window", flags))
                    {
                        ImGui.Text("Hello World");
                    }
#endif
                mainWindow.Draw();

                if (mainWindow.IsExitRequested)
                {
                    break;
                }

                if (!NativeInterop.WindowEndNextFrame())
                {
                    break;
                }
            }

            ImGui.PopStyleVar(); // ScrollbarSize

            NativeInterop.WindowCleanup();

            return 0;
        }
    }
}
