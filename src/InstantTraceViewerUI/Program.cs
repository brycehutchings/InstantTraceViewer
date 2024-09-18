using Veldrid;
using Veldrid.StartupUtilities;
using ImGuiNET;
using System.Diagnostics;
using Veldrid.Sdl2;
using System.Numerics;
using System.Collections.Generic;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        private static HashSet<int> selectedRowIndices = new HashSet<int>();
        private static int? lastSelectedIndex;

        private static Vector4 LevelToColor(TraceLevel level)
        {
            return level == TraceLevel.Verbose ? new Vector4(0.75f, 0.75f, 0.75f, 1.0f)     // Gray
                   : level == TraceLevel.Warning ? new Vector4(1.0f, 0.65f, 0.0f, 1.0f)     // Orange
                   : level == TraceLevel.Error ? new Vector4(0.75f, 0.0f, 0.0f, 1.0f)       // Red
                   : level == TraceLevel.Critical ? new Vector4(0.60f, 0.0f, 0.0f, 1.0f)    // Dark Red
                                                   : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);   // White
        }

        public static unsafe void DrawTraceSourceWindow(ITraceSource traceSource)
        {
            if (ImGui.Begin("Window"))
            {
                if (ImGui.BeginTable("DebugPanelLogger",
                      8 /* columns */,
                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                          ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                          ImGuiTableFlags.Hideable))
                {
                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                    ImGui.TableSetupColumn("Pid", ImGuiTableColumnFlags.WidthFixed, 40.0f);
                    ImGui.TableSetupColumn("Tid", ImGuiTableColumnFlags.WidthFixed, 40.0f);
                    ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("OpCode", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("Provider", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                    ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableHeadersRow();

                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 0)); // Tighten spacing

                    TraceLevel? lastLevel = null;
                    var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                    traceSource.ReadUnderLock(traceRecords =>
                    {
                        clipper.Begin(traceRecords.Count);
                        while (clipper.Step())
                        {
                            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                            {
                                ImGui.TableNextRow();

                                // Update StyleColor if level changed.
                                if (lastLevel != traceRecords[i].Level)
                                {
                                    if (lastLevel.HasValue)
                                    {
                                        ImGui.PopStyleColor(); // ImGuiCol_Text changed.
                                    }
                                    ImGui.PushStyleColor(ImGuiCol.Text, LevelToColor(traceRecords[i].Level));
                                    lastLevel = traceRecords[i].Level;
                                }

                                ImGui.TableNextColumn();
                                bool isSelected = selectedRowIndices.Contains(i);
                                if (ImGui.Selectable(traceRecords[i].Timestamp.ToString("HH:mm:ss.ffffff"), isSelected, ImGuiSelectableFlags.SpanAllColumns))
                                {
                                    if (ImGui.GetIO().KeyShift)
                                    {
                                        if (lastSelectedIndex.HasValue)
                                        {
                                            selectedRowIndices.Clear();
                                            for (int j = System.Math.Min(i, lastSelectedIndex.Value); j <= System.Math.Max(i, lastSelectedIndex.Value); j++)
                                            {
                                                selectedRowIndices.Add(j);
                                            }
                                        }
                                    }
                                    else if (ImGui.GetIO().KeyCtrl)
                                    {
                                        if (isSelected)
                                        {
                                            selectedRowIndices.Remove(i);
                                        }
                                        else
                                        {
                                            selectedRowIndices.Add(i);
                                        }

                                        lastSelectedIndex = i;
                                    }
                                    else
                                    {
                                        selectedRowIndices.Clear();
                                        selectedRowIndices.Add(i);
                                        lastSelectedIndex = i;
                                    }
                                }

                                ImGui.TableNextColumn();
                                ImGui.Text(traceSource.GetProcessName(traceRecords[i].ProcessId));
                                ImGui.TableNextColumn();
                                ImGui.Text(traceSource.GetThreadName(traceRecords[i].ThreadId));
                                ImGui.TableNextColumn();
                                ImGui.Text(traceRecords[i].Level.ToString());
                                ImGui.TableNextColumn();
                                ImGui.Text(traceSource.GetOpCodeName(traceRecords[i].OpCode));
                                ImGui.TableNextColumn();
                                ImGui.Text(traceRecords[i].ProviderName);
                                ImGui.TableNextColumn();
                                ImGui.Text(traceRecords[i].Name);
                                ImGui.TableNextColumn();
                                ImGui.Text(traceRecords[i].Message);
                            }
                        }
                        clipper.End();
                    });

                    if (lastLevel.HasValue)
                    {
                        ImGui.PopStyleColor(); // ImGuiCol_Text
                    }

                    ImGui.PopStyleVar(); // ItemSpacing

                    ImGui.EndTable();
                }
            }

            ImGui.End();
        }

        public static int Main(string[] args)
        {
            Etw.Wprp wprp = Etw.Wprp.Load("D:\\repos\\cloud1\\bin\\tools\\Tracing\\trace-configs\\WPR\\MixedRealityLinkGeneral.wprp");
            // Etw.Wprp wprp = Etw.Wprp.Load("D:\\repos\\cloud1\\tools\\Tracing\\Configs\\WPR\\BuildTrace.wprp");
            using var etwTraceSource = Etw.EtwTraceSource.CreateRealTimeSession(wprp.Profiles[0]);

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
