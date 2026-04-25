using System;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;
using Hexa.NET.ImGui.Backends.Win32;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using VID3D11Device = Vortice.Direct3D11.ID3D11Device;
using VID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using VID3D11RenderTargetView = Vortice.Direct3D11.ID3D11RenderTargetView;
using VID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using VD3D11 = Vortice.Direct3D11.D3D11;
using BID3D11Device = Hexa.NET.ImGui.Backends.D3D11.ID3D11Device;
using BID3D11DeviceContext = Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContext;

namespace InstantTraceViewerUI
{
    /// <summary>
    /// Managed Win32 + D3D11 host that drives Hexa.NET.ImGui via its Win32 + D3D11 backends.
    /// Replaces the legacy native InstantTraceViewerNative/cimgui DLLs.
    /// </summary>
    internal static unsafe class Win32ImGuiHost
    {
        private const uint DefaultX = 100;
        private const uint DefaultY = 100;
        private const uint DefaultWidth = 1200;
        private const uint DefaultHeight = 800;

        private const SwapChainFlags SwapchainFlags =
            SwapChainFlags.AllowModeSwitch | SwapChainFlags.FrameLatencyWaitableObject;

        private const string WindowClassName = "Instant Trace Viewer";
        private const string WindowTitle = "Instant Trace Viewer";

        private static HWND s_hwnd;

        public static IntPtr MainWindowHandle => (IntPtr)s_hwnd.Value;
        private static WNDPROC? s_wndProc; // Held to prevent GC collection of the delegate.
        private static ushort s_classAtom;
        private static bool s_swapChainOccluded;

        private static VID3D11Device? s_device;
        private static VID3D11DeviceContext? s_context;
        private static IDXGISwapChain1? s_swapChain;
        private static IntPtr s_swapChainWaitableObject;
        private static VID3D11RenderTargetView? s_renderTargetView;
        private static uint s_resizeWidth;
        private static uint s_resizeHeight;

        // Forwarded by the WndProc callback so that ImGuiImplWin32 sees its messages.
        // ImGuiImplWin32.WndProcHandler signature uses IntPtr WPARAM / LPARAM.

        public static int WindowInitialize(out IntPtr imguiContext)
        {
            imguiContext = IntPtr.Zero;
            if (!s_hwnd.IsNull)
            {
                return 1;
            }

            HMODULE hInstance = PInvoke.GetModuleHandle((PCWSTR)null);
            HICON hIcon = PInvoke.LoadIcon(hInstance, (PCWSTR)(char*)32512); // App icon embedded by dotnet at ID 32512.
            HCURSOR hCursor = PInvoke.LoadCursor((HINSTANCE)IntPtr.Zero, PInvoke.IDC_ARROW);

            s_wndProc = WndProc;

            fixed (char* classNamePtr = WindowClassName)
            {
                WNDCLASSEXW wc = new()
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    style = WNDCLASS_STYLES.CS_CLASSDC,
                    lpfnWndProc = s_wndProc,
                    hInstance = hInstance,
                    hIcon = hIcon,
                    hCursor = hCursor,
                    lpszClassName = classNamePtr,
                };
                s_classAtom = PInvoke.RegisterClassEx(in wc);
            }

            if (s_classAtom == 0)
            {
                return 1;
            }

            fixed (char* classNamePtr = WindowClassName)
            fixed (char* titlePtr = WindowTitle)
            {
                s_hwnd = PInvoke.CreateWindowEx(
                    0,
                    classNamePtr,
                    titlePtr,
                    WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
                    (int)DefaultX, (int)DefaultY, (int)DefaultWidth, (int)DefaultHeight,
                    HWND.Null, (HMENU)IntPtr.Zero, hInstance, null);
            }

            if (s_hwnd.IsNull)
            {
                PInvoke.UnregisterClass(WindowClassName, hInstance);
                return 1;
            }

            // Scale window size by the monitor DPI.
            uint dpi = PInvoke.GetDpiForWindow(s_hwnd);
            if (dpi != 0)
            {
                float scale = dpi / 96.0f;
                PInvoke.SetWindowPos(s_hwnd, HWND.Null, 0, 0,
                    (int)(DefaultWidth * scale), (int)(DefaultHeight * scale),
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            }

            if (!CreateDeviceD3D())
            {
                CleanupDeviceD3D();
                PInvoke.DestroyWindow(s_hwnd);
                PInvoke.UnregisterClass(WindowClassName, hInstance);
                s_hwnd = HWND.Null;
                return 1;
            }

            PInvoke.ShowWindow(s_hwnd, SHOW_WINDOW_CMD.SW_SHOWDEFAULT);
            PInvoke.UpdateWindow(s_hwnd);

            ImGuiContextPtr ctx = ImGui.CreateContext();
            ImGui.SetCurrentContext(ctx);
            ImGuiImplWin32.SetCurrentContext(ctx);

            ImGuiIOPtr io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;

            ImGuiImplWin32.Init((IntPtr)s_hwnd.Value);
            ImGuiImplD3D11.Init(
                new ID3D11DevicePtr((BID3D11Device*)s_device!.NativePointer),
                new ID3D11DeviceContextPtr((BID3D11DeviceContext*)s_context!.NativePointer));

            imguiContext = (IntPtr)ctx.Handle;
            return 0;
        }

        public static int WindowBeginNextFrame(out int quit, out int occluded)
        {
            quit = 0;
            occluded = 0;

            if (s_swapChainWaitableObject != IntPtr.Zero)
            {
                // Avoid hang on close: wait with a 1000 ms timeout.
                PInvoke.WaitForSingleObject(new HANDLE(s_swapChainWaitableObject), 1000);
            }

            MSG msg;
            while (PInvoke.PeekMessage(out msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(&msg);
                PInvoke.DispatchMessage(&msg);
                if (msg.message == PInvoke.WM_QUIT)
                {
                    quit = 1;
                }
            }

            if (quit != 0)
            {
                return 0;
            }

            if (s_swapChainOccluded)
            {
                Result presentResult = s_swapChain!.Present(0, PresentFlags.Test);
                if (presentResult.Code == unchecked((int)0x087A0001) /* DXGI_STATUS_OCCLUDED */)
                {
                    occluded = 1;
                    return 0;
                }
            }

            s_swapChainOccluded = false;

            if (s_resizeWidth != 0 && s_resizeHeight != 0)
            {
                CleanupRenderTarget();
                Result hr = s_swapChain!.ResizeBuffers(0, s_resizeWidth, s_resizeHeight, Format.Unknown, SwapchainFlags);
                if (hr.Failure)
                {
                    return 1;
                }
                s_resizeWidth = s_resizeHeight = 0;
                if (!CreateRenderTarget())
                {
                    return 1;
                }
            }

            ImGuiImplD3D11.NewFrame();
            ImGuiImplWin32.NewFrame();
            ImGui.NewFrame();
            return 0;
        }

        public static int WindowEndNextFrame()
        {
            ImGui.Render();

            Color4 clearColor = new(
                0.45f * 1.00f,
                0.55f * 1.00f,
                0.60f * 1.00f,
                1.00f);
            s_context!.OMSetRenderTargets(s_renderTargetView!);
            s_context.ClearRenderTargetView(s_renderTargetView!, clearColor);

            ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());

            if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                ImGui.UpdatePlatformWindows();
                ImGui.RenderPlatformWindowsDefault();
            }

            Result hr = s_swapChain!.Present(1, PresentFlags.None);
            s_swapChainOccluded = hr.Code == unchecked((int)0x087A0001) /* DXGI_STATUS_OCCLUDED */;
            return hr.Success ? 0 : 1;
        }

        public static void WindowCleanup()
        {
            ImGuiImplD3D11.Shutdown();
            ImGuiImplWin32.Shutdown();
            ImGui.DestroyContext();

            CleanupDeviceD3D();
            if (!s_hwnd.IsNull)
            {
                PInvoke.DestroyWindow(s_hwnd);
                s_hwnd = HWND.Null;
            }
            if (s_classAtom != 0)
            {
                PInvoke.UnregisterClass(WindowClassName, PInvoke.GetModuleHandle((PCWSTR)null));
                s_classAtom = 0;
            }
        }

        public static void RebuildFontAtlas()
        {
            // Equivalent to ImGui_ImplDX11_CreateDeviceObjects() in the native code: rebuilds the font texture on the GPU.
            ImGuiImplD3D11.InvalidateDeviceObjects();
            ImGuiImplD3D11.CreateDeviceObjects();
        }

        private static bool CreateDeviceD3D()
        {
            FeatureLevel[] featureLevels = { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };

            DeviceCreationFlags flags = DeviceCreationFlags.None;
            // flags |= DeviceCreationFlags.Debug;

            Result hr = VD3D11.D3D11CreateDevice(
                adapter: null, DriverType.Hardware, flags, featureLevels,
                out s_device, out FeatureLevel _, out s_context);
            if (hr.Failure)
            {
                hr = VD3D11.D3D11CreateDevice(
                    adapter: null, DriverType.Warp, flags, featureLevels,
                    out s_device, out FeatureLevel _, out s_context);
            }
            if (hr.Failure)
            {
                return false;
            }

            using IDXGIDevice dxgiDevice = s_device!.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

            SwapChainDescription1 sd = new()
            {
                Width = 0,
                Height = 0,
                Format = Format.R8G8B8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.None,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Unspecified,
                Flags = SwapchainFlags,
            };

            IDXGISwapChain1 swapChain = factory.CreateSwapChainForHwnd(s_device, s_hwnd, sd);
            s_swapChain = swapChain;

            // Frame-latency waitable swap chain (https://github.com/ocornut/imgui/pull/5413).
            using (IDXGISwapChain2? swapChain2 = swapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
            {
                if (swapChain2 != null)
                {
                    swapChain2.MaximumFrameLatency = 1;
                    s_swapChainWaitableObject = swapChain2.FrameLatencyWaitableObject;
                }
            }

            return CreateRenderTarget();
        }

        private static bool CreateRenderTarget()
        {
            using VID3D11Texture2D backBuffer = s_swapChain!.GetBuffer<VID3D11Texture2D>(0);
            s_renderTargetView = s_device!.CreateRenderTargetView(backBuffer);
            return s_renderTargetView != null;
        }

        private static void CleanupRenderTarget()
        {
            s_renderTargetView?.Dispose();
            s_renderTargetView = null;
        }

        private static void CleanupDeviceD3D()
        {
            CleanupRenderTarget();
            if (s_swapChainWaitableObject != IntPtr.Zero)
            {
                PInvoke.CloseHandle(new HANDLE(s_swapChainWaitableObject));
                s_swapChainWaitableObject = IntPtr.Zero;
            }
            s_swapChain?.Dispose();
            s_swapChain = null;
            s_context?.Dispose();
            s_context = null;
            s_device?.Dispose();
            s_device = null;
        }

        private static LRESULT WndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            // Forward to ImGui first.
            IntPtr handled = ImGuiImplWin32.WndProcHandler((IntPtr)hWnd.Value, msg, (UIntPtr)wParam.Value, (IntPtr)lParam.Value);
            if (handled != IntPtr.Zero)
            {
                return new LRESULT((nint)handled);
            }

            switch (msg)
            {
                case PInvoke.WM_SIZE:
                    if ((nuint)wParam.Value == PInvoke.SIZE_MINIMIZED)
                    {
                        return new LRESULT(0);
                    }
                    s_resizeWidth = (uint)((ulong)lParam.Value & 0xFFFF);
                    s_resizeHeight = (uint)(((ulong)lParam.Value >> 16) & 0xFFFF);
                    return new LRESULT(0);

                case PInvoke.WM_SYSCOMMAND:
                    if (((nuint)wParam.Value & 0xFFF0) == PInvoke.SC_KEYMENU)
                    {
                        return new LRESULT(0); // Disable ALT application menu.
                    }
                    break;

                case PInvoke.WM_DESTROY:
                    PInvoke.PostQuitMessage(0);
                    return new LRESULT(0);
            }

            return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }
}
