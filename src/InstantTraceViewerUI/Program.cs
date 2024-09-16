using Veldrid;
using Veldrid.StartupUtilities;
using ImGuiNET;
using System.Diagnostics;
using Veldrid.Sdl2;

namespace InstantTraceViewerUI
{
    internal class Program
    {
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

                var frameTiming = Stopwatch.StartNew();
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
#else
                    uint dockId = ImGui.DockSpaceOverViewport();

                    if (ImGui.Begin("Window"))
                    {
                        ImGui.Text("Hello World");
                    }
#endif

                    if (ImGui.Begin("Window2"))
                    {
                        ImGui.Text("Hello World2");
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

                graphicsDevice.WaitForIdle();
            }

            return 0;
        }
    }
}
