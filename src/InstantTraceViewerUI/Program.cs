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

                    if (ImGui.Begin("Window"))
                    {
                        ImGui.Text("Hello World");
                        ImGui.Text("lllllllllll");
                        ImGui.Text("WWWWWWWWWWW");
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
