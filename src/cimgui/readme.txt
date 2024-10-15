This is a special hybrid DLL that contains both:
* ImGui configured to export all symbols
* cimgui

ImGui.NET requires a "cimgui.dll" file to P/Invoke into so this project must be called cimgui.dll to avoid forking ImGui.NET.
By compiling ImGui to export all symbols, InstantTraceViewerNative can also use ImGui with C++ as it was intended without having to degrade to using cimgui.