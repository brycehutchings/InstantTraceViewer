using Veldrid;
using Veldrid.StartupUtilities;
using ImGuiNET;
using System.Diagnostics;
using Veldrid.Sdl2;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        public static void DrawTraceSourceWindow(ITraceSource traceSource)
        {
            if (ImGui.Begin("Window"))
            {
                if (ImGui.BeginTable("DebugPanelLogger",
                      7 /* columns */,
                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                          ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                          ImGuiTableFlags.Hideable))
                {
                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                    ImGui.TableSetupColumn("Pid", ImGuiTableColumnFlags.WidthFixed, 40.0f);
                    ImGui.TableSetupColumn("Tid", ImGuiTableColumnFlags.WidthFixed, 40.0f);
                    ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("Provider", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                    ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableHeadersRow();

                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 1)); // Tighten spacing

                    traceSource.ReadUnderLock(traceRecords =>
                    {
                        foreach (var record in traceRecords)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(record.Timestamp.ToString("HH:mm:ss.fff"));
                            ImGui.TableNextColumn();
                            ImGui.Text(record.ProcessId.ToString());
                            ImGui.TableNextColumn();
                            ImGui.Text(record.ThreadId.ToString());
                            ImGui.TableNextColumn();
                            ImGui.Text(record.Level.ToString());
                            ImGui.TableNextColumn();
                            ImGui.Text(record.ProviderName);
                            ImGui.TableNextColumn();
                            ImGui.Text(record.Name);
                            ImGui.TableNextColumn();
                            ImGui.Text(record.Message);
                        }
                    });

                    ImGui.PopStyleVar(); // ItemSpacing

                    ImGui.EndTable();
                }
            }

            ImGui.End();
        }

        public static int Main(string[] args)
        {
            Etw.Wprp wprp = Etw.Wprp.Load("D:\\repos\\cloud1\\bin\\tools\\Tracing\\trace-configs\\WPR\\MixedRealityLinkGeneral.wprp");
            var etwTraceSource = Etw.EtwTraceSource.CreateRealTimeSession(wprp.Profiles[0]);

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

                window.Resized += () =>
                {
                    graphicsDevice.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
                    controller.WindowResized(window.Width, window.Height);
                };

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
#endif
                    uint dockId = ImGui.DockSpaceOverViewport();

                    DrawTraceSourceWindow(etwTraceSource);

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
