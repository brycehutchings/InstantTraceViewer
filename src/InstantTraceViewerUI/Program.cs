using Veldrid;
using Veldrid.StartupUtilities;
using ImGuiNET;
using System.Diagnostics;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        public static int Main(string[] args)
        {
            VeldridStartup.CreateWindowAndGraphicsDevice(
                new WindowCreateInfo(100, 100, 1280, 720, Veldrid.WindowState.Normal, "Instant Trace Viewer"),
                out var window,
                out var gd);
            ImGuiRenderer imguiRenderer = new ImGuiRenderer(
                gd, gd.MainSwapchain.Framebuffer.OutputDescription,
                (int)gd.MainSwapchain.Framebuffer.Width, (int)gd.MainSwapchain.Framebuffer.Height);
            var cl = gd.ResourceFactory.CreateCommandList();

            var frameTiming = Stopwatch.StartNew();
            while (window.Exists)
            {
                var input = window.PumpEvents();
                if (!window.Exists)
                {
                    break;
                }


                float deltaSeconds = (float)frameTiming.Elapsed.TotalSeconds;
                frameTiming.Restart();
                imguiRenderer.Update(deltaSeconds, input);

                // Draw stuff
                ImGui.Text("Hello World");

                cl.Begin();
                cl.SetFramebuffer(gd.MainSwapchain.Framebuffer);
                cl.ClearColorTarget(0, RgbaFloat.Black);
                imguiRenderer.Render(gd, cl);
                cl.End();
                gd.SubmitCommands(cl);
                gd.SwapBuffers(gd.MainSwapchain);
            }

            return 0;
        }
    }
}
