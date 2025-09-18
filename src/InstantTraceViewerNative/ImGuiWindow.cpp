#include "imgui.h"
#include "imgui_internal.h"
#include "backends/imgui_impl_win32.h"
#include "backends/imgui_impl_dx11.h"
#include <d3d11.h>
#include <dxgi1_3.h>
#include <shellapi.h>

static constexpr UINT DefaultX = 100;
static constexpr UINT DefaultY = 100;
static constexpr UINT DefaultWidth = 1200;
static constexpr UINT DefaultHeight = 800;
static constexpr UINT SwapchainFlags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

static HWND g_hwnd{ nullptr };
static WNDCLASSEXW g_windowClass{};
static bool g_swapChainOccluded{ false };
static ID3D11Device* g_d3dDevice = nullptr;
static ID3D11DeviceContext* g_d3dDeviceContext = nullptr;
static IDXGISwapChain* g_swapChain = nullptr;
static HANDLE g_swapChainWaitableObject = nullptr;
static UINT g_resizeWidth = 0, g_resizeHeight = 0;
static ID3D11RenderTargetView* g_mainRenderTargetView = nullptr;
static bool g_msaaEnabled = false;

// Forward declarations of helper functions
bool CreateDeviceD3D(HWND hWnd);
void CleanupDeviceD3D();
HRESULT CreateRenderTarget();
void CleanupRenderTarget();
LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

extern "C" int __declspec(dllexport) __stdcall WindowInitialize(ImGuiContext** imguiContext) noexcept
{
    if (g_hwnd)
    {
        return 1; // Already initialized. Error.
    }

    g_windowClass.cbSize = sizeof(g_windowClass);
    g_windowClass.style = CS_CLASSDC;
    g_windowClass.lpfnWndProc = WndProc;
    g_windowClass.hInstance = GetModuleHandle(nullptr);
    g_windowClass.lpszClassName = L"Instant Trace Viewer";

    // Use the icon for the exe as the window icon. Dotnet embeds the icon with ID 32512.
    g_windowClass.hIcon = LoadIcon(GetModuleHandle(nullptr), MAKEINTRESOURCE(32512));

    ::RegisterClassExW(&g_windowClass);

    g_hwnd = ::CreateWindowW(
        g_windowClass.lpszClassName, L"Instant Trace Viewer", WS_OVERLAPPEDWINDOW,
        DefaultX, DefaultY, DefaultWidth, DefaultHeight,
        nullptr, nullptr, g_windowClass.hInstance, nullptr);

    // Scale the size of the window based on the DPI of the monitor. This won't happen "for free" because this application has DPI awareness turned on.
    if (g_hwnd)
    {
        if (UINT dpi = GetDpiForWindow(g_hwnd); dpi != 0)
        {
            float scaleFactor = dpi / 96.0f; // 96 is the default DPI
            ::SetWindowPos(g_hwnd, nullptr, 0, 0, (int)(DefaultWidth * scaleFactor), (int)(DefaultHeight * scaleFactor), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    if (!CreateDeviceD3D(g_hwnd))
    {
        CleanupDeviceD3D();
        ::UnregisterClassW(g_windowClass.lpszClassName, g_windowClass.hInstance);
        return 1; // Error
    }

    ::ShowWindow(g_hwnd, SW_SHOWDEFAULT);
    ::UpdateWindow(g_hwnd);

    IMGUI_CHECKVERSION();

    *imguiContext = ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;
    io.ConfigFlags |= ImGuiConfigFlags_DockingEnable;
    io.ConfigFlags |= ImGuiConfigFlags_ViewportsEnable;

    if (g_msaaEnabled)
    {
        ImGui::GetStyle().AntiAliasedLines = false;
        ImGui::GetStyle().AntiAliasedFill = false;
    }

    ImGui_ImplWin32_Init(g_hwnd);
    ImGui_ImplDX11_Init(g_d3dDevice, g_d3dDeviceContext);

    // Load Fonts
    // - If no fonts are loaded, dear imgui will use the default font. You can also load multiple fonts and use ImGui::PushFont()/PopFont() to select them.
    // - AddFontFromFileTTF() will return the ImFont* so you can store it if you need to select the font among multiple.
    // - If the file cannot be loaded, the function will return a nullptr. Please handle those errors in your application (e.g. use an assertion, or display an error and quit).
    // - The fonts will be rasterized at a given size (w/ oversampling) and stored into a texture when calling ImFontAtlas::Build()/GetTexDataAsXXXX(), which ImGui_ImplXXXX_NewFrame below will call.
    // - Use '#define IMGUI_ENABLE_FREETYPE' in your imconfig file to use Freetype for higher quality font rendering.
    // - Read 'docs/FONTS.md' for more instructions and details.
    // - Remember that in C/C++ if you want to include a backslash \ in a string literal you need to write a double backslash \\ !
    //io.Fonts->AddFontDefault();
    //io.Fonts->AddFontFromFileTTF("c:\\Windows\\Fonts\\segoeui.ttf", 18.0f);
    //io.Fonts->AddFontFromFileTTF("../../misc/fonts/DroidSans.ttf", 16.0f);
    //io.Fonts->AddFontFromFileTTF("../../misc/fonts/Roboto-Medium.ttf", 16.0f);
    //io.Fonts->AddFontFromFileTTF("../../misc/fonts/Cousine-Regular.ttf", 15.0f);
    //ImFont* font = io.Fonts->AddFontFromFileTTF("c:\\Windows\\Fonts\\ArialUni.ttf", 18.0f, nullptr, io.Fonts->GetGlyphRangesJapanese());
    //IM_ASSERT(font != nullptr);

    return 0;
}

// Returns true if frame is being presented and False if frame is not being presented (e.g. window is minimized).
extern "C" int __declspec(dllexport) __stdcall WindowBeginNextFrame(int* quit, int* occluded) noexcept
{
    *occluded = 0;  
    *quit = 0;

    if (g_swapChainWaitableObject)
    {
        // It has been observed that waiting on the swapchain waitable object can go unsignaled forever when the window is closed, causing the application to hang.
        // To avoid this, we wait for the swapchain to be signaled with a timeout of 1000ms.
        ::WaitForSingleObject(g_swapChainWaitableObject, 1000 /* 1000ms */);
    }

    // Poll and handle messages (inputs, window resize, etc.)
    // See the WndProc() function below for our to dispatch events to the Win32 backend.
    MSG msg;
    while (::PeekMessage(&msg, nullptr, 0U, 0U, PM_REMOVE))
    {
        ::TranslateMessage(&msg);
        ::DispatchMessage(&msg);
        if (msg.message == WM_QUIT)
        {
            *quit = true;
        }
    }

    if (*quit)
    {
        return 0;
    }

    // Handle window being minimized or screen locked
    if (g_swapChainOccluded && g_swapChain->Present(0, DXGI_PRESENT_TEST) == DXGI_STATUS_OCCLUDED)
    {
        *occluded = 1;
        return 0;
    }

    g_swapChainOccluded = false;

    // Handle window resize (we don't resize directly in the WM_SIZE handler)
    if (g_resizeWidth != 0 && g_resizeHeight != 0)
    {
        CleanupRenderTarget();
        HRESULT hr = g_swapChain->ResizeBuffers(0, g_resizeWidth, g_resizeHeight, DXGI_FORMAT_UNKNOWN, SwapchainFlags);
        if (FAILED(hr))
        {
            return 1; // Error
        }
        g_resizeWidth = g_resizeHeight = 0;
        hr = CreateRenderTarget();
        if (FAILED(hr))
        {
            return 1; // Error
        }
    }

    // Start the Dear ImGui frame
    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();

    //static bool s_showDemoWindow = true;
    //ImGui::ShowDemoWindow(&s_showDemoWindow);

    return 0;
}

extern "C" int __declspec(dllexport) __stdcall WindowEndNextFrame() noexcept
{
    ImVec4 clear_color = ImVec4(0.45f, 0.55f, 0.60f, 1.00f);

    // Rendering
    ImGui::Render();
    const float clear_color_with_alpha[4] = { clear_color.x * clear_color.w, clear_color.y * clear_color.w, clear_color.z * clear_color.w, clear_color.w };
    g_d3dDeviceContext->OMSetRenderTargets(1, &g_mainRenderTargetView, nullptr);
    g_d3dDeviceContext->ClearRenderTargetView(g_mainRenderTargetView, clear_color_with_alpha);
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());

    // Update and Render additional Platform Windows
    if (ImGui::GetIO().ConfigFlags & ImGuiConfigFlags_ViewportsEnable)
    {
        ImGui::UpdatePlatformWindows();
        ImGui::RenderPlatformWindowsDefault();
    }

    // Present
    HRESULT hr = g_swapChain->Present(1, 0);   // Present with vsync
    //HRESULT hr = g_swapChain->Present(0, 0); // Present without vsync
    g_swapChainOccluded = (hr == DXGI_STATUS_OCCLUDED);

    return SUCCEEDED(hr) ? 0 : 1;
}

extern "C" bool __declspec(dllexport) __stdcall WindowCleanup() noexcept
{
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();

    CleanupDeviceD3D();
    ::DestroyWindow(g_hwnd);
    ::UnregisterClassW(g_windowClass.lpszClassName, g_windowClass.hInstance);

    return 0;
}

extern "C" void __declspec(dllexport) __stdcall RebuildFontAtlas() noexcept
{
    // This function is marked static and can't be used, so we use a bigger hammer to avoid forking the ImGui backend.
    // ImGui_ImplDX11_CreateFontsTexture();
    ImGui_ImplDX11_CreateDeviceObjects();
}

// Needed because ImGui.NET doesn't expose ImGui's internal data structures.
#pragma warning(disable : 4190)
extern "C" ImGuiPlatformImeData __declspec(dllexport) __stdcall GetPlatformImeData() noexcept
{
    return ImGui::GetCurrentContext()->PlatformImeData;
}
#pragma warning(default : 4190)

// This is a subset of ImGuiInputTextState.
// If you change this struct, you must also change the .NET struct in NativeInterop.cs too.
struct CurrentInputTextState {
    ImGuiID Id;
    int CursorPos;
    float ScrollX;
};

// Needed because ImGui.NET doesn't expose ImGui's internal data structures.
extern "C" CurrentInputTextState __declspec(dllexport) __stdcall GetCurrentInputTextState() noexcept
{
    const ImGuiInputTextState& textState = ImGui::GetCurrentContext()->InputTextState;
    return CurrentInputTextState{ .Id = textState.ID, .CursorPos = textState.GetCursorPos(), .ScrollX = textState.Scroll.x };
}

//
// Helper functions:
//

bool CreateDeviceD3D(HWND hWnd)
{
    // Setup swap chain
    DXGI_SWAP_CHAIN_DESC1 sd;
    ZeroMemory(&sd, sizeof(sd));
    sd.BufferCount = 2;
    sd.Width = 0;
    sd.Height = 0;
    sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.Flags = SwapchainFlags;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    sd.Scaling = DXGI_SCALING_NONE;

    // When MSAA is enabled (by setting this to 2+), the ImGui fringe-based anti-aliasing will be disabled (see where g_msaaEnabled is read).
    // However, the ImGui anti-aliasing looks better than MSAA 8x for the most part. So support for this is only here in case this needs to
    // be revisited later. ImGui fringe-based anti-aliasing will draw 6 additional triangles per triangle to create a smooth edge.
    sd.SampleDesc.Count = 1;
    sd.SampleDesc.Quality = 0;

    g_msaaEnabled = sd.SampleDesc.Count > 1;

    UINT createDeviceFlags = 0;
    //createDeviceFlags |= D3D11_CREATE_DEVICE_DEBUG;
    D3D_FEATURE_LEVEL featureLevel;
    const D3D_FEATURE_LEVEL featureLevelArray[2] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0, };

    HRESULT res = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, createDeviceFlags, featureLevelArray, 2, D3D11_SDK_VERSION, &g_d3dDevice, &featureLevel, &g_d3dDeviceContext);
    if (res == DXGI_ERROR_UNSUPPORTED) // Try high-performance WARP software driver if hardware is not available.
    {
        res = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, createDeviceFlags, featureLevelArray, 2, D3D11_SDK_VERSION, &g_d3dDevice, &featureLevel, &g_d3dDeviceContext);
    }
    if (res != S_OK)
    {
        return false;
    }

    // TODO: https://github.com/ocornut/imgui/pull/5413/files

    IDXGIDevice* dxgiDevice = nullptr;
    res = g_d3dDevice->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDevice);
    if (FAILED(res))
        return false;

    IDXGIAdapter* dxgiAdapter = nullptr;
    res = dxgiDevice->GetAdapter(&dxgiAdapter);
    dxgiDevice->Release();
    if (FAILED(res))
        return false;

    IDXGIFactory2* dxgiFactory = nullptr;
    res = dxgiAdapter->GetParent(__uuidof(IDXGIFactory2), (void**)&dxgiFactory);
    dxgiAdapter->Release();
    if (FAILED(res))
        return false;

    res = dxgiFactory->CreateSwapChainForHwnd(g_d3dDevice, hWnd, &sd, nullptr, nullptr, (IDXGISwapChain1**)&g_swapChain);
    dxgiFactory->Release();
    if (FAILED(res))
        return false;

    if (sd.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT)
    {
        // https://github.com/ocornut/imgui/pull/5413/files
        IDXGISwapChain2* swapChain2 = nullptr;
        g_swapChain->QueryInterface(IID_PPV_ARGS(&swapChain2));
        if (swapChain2) {
            // If your application is CPU or GPU heavy you may decide to increase maximum frame latency to 2. Then the CPU will start work
            // on the next frame before the GPU finishes the previous frame, allowing CPU and GPU work to overlap more. This increases
            // throughput at the expense of latency, allowing you more time to render but making your application feel less responsive.
            swapChain2->SetMaximumFrameLatency(1);
            g_swapChainWaitableObject = swapChain2->GetFrameLatencyWaitableObject();
            swapChain2->Release();
        }
    }

    return SUCCEEDED(CreateRenderTarget());
}

void CleanupDeviceD3D()
{
    CleanupRenderTarget();
    if (g_swapChainWaitableObject) { CloseHandle(g_swapChainWaitableObject); g_swapChainWaitableObject = NULL; }
    if (g_swapChain) { g_swapChain->Release(); g_swapChain = nullptr; }
    if (g_d3dDeviceContext) { g_d3dDeviceContext->Release(); g_d3dDeviceContext = nullptr; }
    if (g_d3dDevice) { g_d3dDevice->Release(); g_d3dDevice = nullptr; }
}

HRESULT CreateRenderTarget()
{
    ID3D11Texture2D* backBuffer;
    HRESULT res = g_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
    if (SUCCEEDED(res))
    {
        res = g_d3dDevice->CreateRenderTargetView(backBuffer, nullptr, &g_mainRenderTargetView);
        backBuffer->Release();
    }

    return res;
}

void CleanupRenderTarget()
{
    if (g_mainRenderTargetView)
    {
        g_mainRenderTargetView->Release();
        g_mainRenderTargetView = nullptr;
    }
}

// Forward declare message handler from imgui_impl_win32.cpp
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

// Win32 message handler
// You can read the io.WantCaptureMouse, io.WantCaptureKeyboard flags to tell if dear imgui wants to use your inputs.
// - When io.WantCaptureMouse is true, do not dispatch mouse input data to your main application, or clear/overwrite your copy of the mouse data.
// - When io.WantCaptureKeyboard is true, do not dispatch keyboard input data to your main application, or clear/overwrite your copy of the keyboard data.
// Generally you may always pass all inputs to dear imgui, and hide them from your application based on those two flags.
LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
    {
        return true;
    }

    switch (msg)
    {
    case WM_SIZE:
        if (wParam == SIZE_MINIMIZED)
            return 0;
        g_resizeWidth = (UINT)LOWORD(lParam); // Queue resize
        g_resizeHeight = (UINT)HIWORD(lParam);
        return 0;
    case WM_SYSCOMMAND:
        if ((wParam & 0xfff0) == SC_KEYMENU) // Disable ALT application menu
            return 0;
        break;
    case WM_DESTROY:
        ::PostQuitMessage(0);
        return 0;
    }

    return ::DefWindowProcW(hWnd, msg, wParam, lParam);
}