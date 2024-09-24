using System;
using System.Diagnostics;
using ImGuiNET;
using InstantTraceViewerUI.ImGuiRendering;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        [STAThread] // For WinForms file browser usage :-\
        public static int Main(string[] args)
        {
            Sdl2Window window;
            GraphicsDevice graphicsDevice;
            VeldridStartup.CreateWindowAndGraphicsDevice(
                new WindowCreateInfo(100, 100, 1280, 720, Veldrid.WindowState.Normal, "Instant Trace Viewer"),
                out window,
                out graphicsDevice);

            using (graphicsDevice)
            {
                using CommandList commandLine = graphicsDevice.ResourceFactory.CreateCommandList();

                using ImGuiController controller = new ImGuiController(graphicsDevice, window, graphicsDevice.MainSwapchain.Framebuffer.OutputDescription, window.Width, window.Height);

                // Increase scrollbar size to make it easier to use.
                ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 18.0f);

                window.Resized += () =>
                {
                    graphicsDevice.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
                    controller.WindowResized(window.Width, window.Height);
                };

                var frameTiming = Stopwatch.StartNew();
                using var mainWindow = new MainWindow();

                while (window.Exists)
                {
                    InputSnapshot input = window.PumpEvents();
                    if (!window.Exists)
                    {
                        break;
                    }

                    float deltaSeconds = (float)frameTiming.Elapsed.TotalSeconds;
                    frameTiming.Restart();
                    controller.Update(deltaSeconds, input);

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

                    commandLine.Begin();
                    commandLine.SetFramebuffer(graphicsDevice.MainSwapchain.Framebuffer);
                    commandLine.ClearColorTarget(0, RgbaFloat.Black);
                    controller.Render(graphicsDevice, commandLine);
                    commandLine.End();
                    graphicsDevice.SubmitCommands(commandLine);
                    graphicsDevice.SwapBuffers(graphicsDevice.MainSwapchain);
                    controller.SwapExtraWindows(graphicsDevice);
                }

                ImGui.PopStyleVar(); // ScrollbarSize

                graphicsDevice.WaitForIdle();
            }

            return 0;
        }
    }
}
