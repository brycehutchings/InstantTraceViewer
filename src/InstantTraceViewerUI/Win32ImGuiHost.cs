using System;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;
using Hexa.NET.ImGui.Backends.Win32;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;
using BID3D11Device = Hexa.NET.ImGui.Backends.D3D11.ID3D11Device;
using BID3D11DeviceContext = Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContext;
using WID3D11Device = Windows.Win32.Graphics.Direct3D11.ID3D11Device;
using WID3D11DeviceContext = Windows.Win32.Graphics.Direct3D11.ID3D11DeviceContext;
using WID3D11RenderTargetView = Windows.Win32.Graphics.Direct3D11.ID3D11RenderTargetView;
using WID3D11Texture2D = Windows.Win32.Graphics.Direct3D11.ID3D11Texture2D;

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

        private const DXGI_SWAP_CHAIN_FLAG SwapchainFlags =
            DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH | DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

        private const string WindowClassName = "Instant Trace Viewer";
        private const string WindowTitle = "Instant Trace Viewer";

        private static HWND s_hwnd;

        public static IntPtr MainWindowHandle => (IntPtr)s_hwnd;
        private static ushort s_classAtom;
        private static bool s_swapChainOccluded;

        private static WID3D11Device* s_device;
        private static WID3D11DeviceContext* s_context;
        private static IDXGISwapChain1* s_swapChain;
        private static HANDLE s_swapChainWaitableObject;
        private static WID3D11RenderTargetView* s_renderTargetView;
        private static uint s_resizeWidth;
        private static uint s_resizeHeight;

        /// <summary>
        /// Creates the application window, the D3D11 device + swap chain, and the ImGui context
        /// (with the Win32 + D3D11 backends initialized). Must be called once before any other method.
        /// </summary>
        /// <param name="imguiContext">Out: the newly created ImGui context handle, suitable for ImGui.SetCurrentContext.</param>
        public static void WindowInitialize(out IntPtr imguiContext)
        {
            imguiContext = IntPtr.Zero;
            if (!s_hwnd.IsNull)
            {
                throw new InvalidOperationException("Window is already initialized.");
            }

            HMODULE hInstance = PInvoke.GetModuleHandle((PCWSTR)null);
            HICON hIcon = PInvoke.LoadIcon(hInstance, PInvoke.IDI_APPLICATION);
            HCURSOR hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW);

            fixed (char* classNamePtr = WindowClassName)
            {
                WNDCLASSEXW wc = new()
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    style = WNDCLASS_STYLES.CS_CLASSDC,
                    lpfnWndProc = &WndProc,
                    hInstance = hInstance,
                    hIcon = hIcon,
                    hCursor = hCursor,
                    lpszClassName = classNamePtr,
                };
                s_classAtom = PInvoke.RegisterClassEx(in wc);
            }

            if (s_classAtom == 0)
            {
                throw new InvalidOperationException("Failed to register window class.");
            }

            s_hwnd = PInvoke.CreateWindowEx(
                0,
                WindowClassName,
                WindowTitle,
                WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
                (int)DefaultX, (int)DefaultY, (int)DefaultWidth, (int)DefaultHeight,
                HWND.Null, HMENU.Null, hInstance, null);

            if (s_hwnd.IsNull)
            {
                PInvoke.UnregisterClass(WindowClassName, hInstance);
                throw new InvalidOperationException("Failed to create application window.");
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

            try
            {
                CreateDeviceD3D();
            }
            catch
            {
                CleanupDeviceD3D();
                PInvoke.DestroyWindow(s_hwnd);
                PInvoke.UnregisterClass(WindowClassName, hInstance);
                s_hwnd = HWND.Null;
                throw;
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

            ImGuiImplWin32.Init((IntPtr)s_hwnd);
            ImGuiImplD3D11.Init(
                new ID3D11DevicePtr((BID3D11Device*)s_device),
                new ID3D11DeviceContextPtr((BID3D11DeviceContext*)s_context));

            imguiContext = (IntPtr)ctx.Handle;
        }

        /// <summary>
        /// Pumps Win32 messages, applies any pending swap-chain resize, and starts a new ImGui frame.
        /// Call once per iteration of the main loop, then issue ImGui draw calls, then call <see cref="WindowEndNextFrame"/>.
        /// Also blocks (with a 1s timeout) on the swap chain's frame-latency waitable object to pace rendering to the display.
        /// </summary>
        /// <param name="quit">Out: true if a WM_QUIT was received and the app should exit.</param>
        /// <param name="occluded">Out: true if the swap chain is currently occluded (e.g. minimized / locked screen) and the caller should idle this frame.</param>
        public static void WindowBeginNextFrame(out bool quit, out bool occluded)
        {
            quit = false;
            occluded = false;

            if (!s_swapChainWaitableObject.IsNull)
            {
                // Avoid hang on close: wait with a 1000 ms timeout.
                PInvoke.WaitForSingleObject(s_swapChainWaitableObject, 1000);
            }

            MSG msg;
            while (PInvoke.PeekMessage(out msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(&msg);
                PInvoke.DispatchMessage(&msg);
                if (msg.message == PInvoke.WM_QUIT)
                {
                    quit = true;
                }
            }

            if (quit)
            {
                return;
            }

            if (s_swapChainOccluded)
            {
                HRESULT presentResult = s_swapChain->Present(0, DXGI_PRESENT.DXGI_PRESENT_TEST);
                if (presentResult == HRESULT.DXGI_STATUS_OCCLUDED)
                {
                    occluded = true;
                    return;
                }
            }

            s_swapChainOccluded = false;

            if (s_resizeWidth != 0 && s_resizeHeight != 0)
            {
                CleanupRenderTarget();
                s_swapChain->ResizeBuffers(0, s_resizeWidth, s_resizeHeight, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, (uint)SwapchainFlags);
                s_resizeWidth = s_resizeHeight = 0;
                CreateRenderTarget();
            }

            ImGuiImplD3D11.NewFrame();
            ImGuiImplWin32.NewFrame();
            ImGui.NewFrame();
        }

        /// <summary>
        /// Renders the ImGui draw data to the back buffer, renders any secondary viewports (if multi-viewport is enabled),
        /// and presents with vsync. Must be paired with <see cref="WindowBeginNextFrame"/>.
        /// </summary>
        public static void WindowEndNextFrame()
        {
            ImGui.Render();

            float[] clearColor =
            {
                0.45f * 1.00f,
                0.55f * 1.00f,
                0.60f * 1.00f,
                1.00f,
            };
            WID3D11RenderTargetView* renderTargetView = s_renderTargetView;
            s_context->OMSetRenderTargets(1, &renderTargetView, null);
            s_context->ClearRenderTargetView(s_renderTargetView, clearColor);

            ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());

            if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                ImGui.UpdatePlatformWindows();
                ImGui.RenderPlatformWindowsDefault();
            }

            HRESULT hr = s_swapChain->Present(1, 0);
            s_swapChainOccluded = hr == HRESULT.DXGI_STATUS_OCCLUDED;
            hr.ThrowOnFailure();
        }

        /// <summary>
        /// Tears down the ImGui backends and context, releases the D3D11 device + swap chain,
        /// destroys the window, and unregisters the window class. Safe to call once at shutdown.
        /// </summary>
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

        /// <summary>
        /// Re-uploads the ImGui font atlas to the GPU. Call this after changing fonts (e.g. font size or family)
        /// while the application is running, after rebuilding the atlas via ImGui's font APIs.
        /// </summary>
        public static void RebuildFontAtlas()
        {
            // Equivalent to ImGui_ImplDX11_CreateDeviceObjects() in the native code: rebuilds the font texture on the GPU.
            ImGuiImplD3D11.InvalidateDeviceObjects();
            ImGuiImplD3D11.CreateDeviceObjects();
        }

        /// <summary>
        /// Creates the D3D11 device + immediate context (Hardware, falling back to WARP) and a flip-discard
        /// swap chain bound to the application window. Also queries for IDXGISwapChain2 to enable the
        /// frame-latency waitable object used by <see cref="WindowBeginNextFrame"/> for smoother pacing.
        /// </summary>
        private static void CreateDeviceD3D()
        {
            ReadOnlySpan<D3D_FEATURE_LEVEL> featureLevels = stackalloc[]
            {
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0,
            };

            const D3D11_CREATE_DEVICE_FLAG flags = 0;

            WID3D11Device* device = null;
            WID3D11DeviceContext* context = null;
            HRESULT hr = default;
            foreach (D3D_DRIVER_TYPE driverType in new[] { D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP })
            {
                hr = PInvoke.D3D11CreateDevice(
                    pAdapter: null,
                    DriverType: driverType,
                    Software: HMODULE.Null,
                    Flags: flags,
                    pFeatureLevels: featureLevels,
                    SDKVersion: PInvoke.D3D11_SDK_VERSION,
                    ppDevice: &device,
                    pFeatureLevel: out _,
                    ppImmediateContext: &context);
                if (hr.Succeeded)
                {
                    break;
                }
            }
            hr.ThrowOnFailure();

            s_device = device;
            s_context = context;

            IDXGIDevice* dxgiDevice = null;
            IDXGIAdapter* adapter = null;
            IDXGIFactory2* factory = null;
            try
            {
                ((IUnknown*)s_device)->QueryInterface(out dxgiDevice).ThrowOnFailure();
                dxgiDevice->GetAdapter(&adapter);

                Guid factoryIid = IDXGIFactory2.IID_Guid;
                adapter->GetParent(in factoryIid, out void* factoryPtr);
                factory = (IDXGIFactory2*)factoryPtr;

                DXGI_SWAP_CHAIN_DESC1 sd = new()
                {
                    Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                    BufferCount = 2,
                    Scaling = DXGI_SCALING.DXGI_SCALING_NONE,
                    SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                    Flags = SwapchainFlags,
                };

                IDXGISwapChain1* swapChain = null;
                factory->CreateSwapChainForHwnd((IUnknown*)s_device, s_hwnd, &sd, null, null, &swapChain);
                s_swapChain = swapChain;

                // Frame-latency waitable swap chain (https://github.com/ocornut/imgui/pull/5413).
                IDXGISwapChain2* swapChain2 = null;
                if (((IUnknown*)s_swapChain)->QueryInterface(out swapChain2).Succeeded)
                {
                    swapChain2->SetMaximumFrameLatency(1);
                    s_swapChainWaitableObject = swapChain2->GetFrameLatencyWaitableObject();
                    SafeRelease(ref swapChain2);
                }
            }
            finally
            {
                SafeRelease(ref factory);
                SafeRelease(ref adapter);
                SafeRelease(ref dxgiDevice);
            }

            CreateRenderTarget();
        }

        /// <summary>
        /// Creates the render-target view bound to the swap chain's back buffer.
        /// Called during initial setup and after every swap-chain resize.
        /// </summary>
        private static void CreateRenderTarget()
        {
            WID3D11Texture2D* backBuffer = null;
            try
            {
                Guid textureIid = ID3D11Texture2D.IID_Guid;
                s_swapChain->GetBuffer(0, in textureIid, out void* surface);
                backBuffer = (WID3D11Texture2D*)surface;

                WID3D11RenderTargetView* renderTargetView = null;
                s_device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, &renderTargetView);
                s_renderTargetView = renderTargetView;
            }
            finally
            {
                SafeRelease(ref backBuffer);
            }
        }

        /// <summary>
        /// Releases the current render-target view. Called before swap-chain resize and on shutdown.
        /// </summary>
        private static void CleanupRenderTarget()
        {
            SafeRelease(ref s_renderTargetView);
        }

        /// <summary>
        /// Releases all D3D11 / DXGI resources: render target, frame-latency waitable handle, swap chain,
        /// device context, and device.
        /// </summary>
        private static void CleanupDeviceD3D()
        {
            CleanupRenderTarget();
            if (!s_swapChainWaitableObject.IsNull)
            {
                PInvoke.CloseHandle(s_swapChainWaitableObject);
                s_swapChainWaitableObject = HANDLE.Null;
            }
            SafeRelease(ref s_swapChain);
            SafeRelease(ref s_context);
            SafeRelease(ref s_device);
        }

        private static void SafeRelease<T>(ref T* p) where T : unmanaged
        {
            if (p != null)
            {
                ((IUnknown*)p)->Release();
                p = null;
            }
        }

        /// <summary>
        /// Window procedure for the application window. First forwards the message to the ImGui Win32 backend
        /// (so it can update its input state); if ImGui doesn't claim the message, handles WM_SIZE (queues a swap-chain resize),
        /// suppresses the SC_KEYMENU system command (to disable the ALT application menu), and posts a quit on WM_DESTROY.
        /// All other messages fall through to DefWindowProc.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static LRESULT WndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            // Forward to ImGui first.
            IntPtr handled = ImGuiImplWin32.WndProcHandler((IntPtr)hWnd, msg, (UIntPtr)wParam, (IntPtr)lParam);
            if (handled != IntPtr.Zero)
            {
                return new LRESULT((nint)handled);
            }

            switch (msg)
            {
                case PInvoke.WM_SIZE:
                    if ((nuint)wParam == PInvoke.SIZE_MINIMIZED)
                    {
                        return new LRESULT(0);
                    }
                    nint lp = (nint)lParam;
                    s_resizeWidth = (uint)(ushort)lp;
                    s_resizeHeight = (uint)(ushort)(lp >> 16);
                    return new LRESULT(0);

                case PInvoke.WM_SYSCOMMAND:
                    if (((nuint)wParam & 0xFFF0) == PInvoke.SC_KEYMENU)
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
