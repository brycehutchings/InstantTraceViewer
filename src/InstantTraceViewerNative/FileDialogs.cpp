// This provides a native open and save file dialog experience for Windows rather than using some ImGui-based solution which will not be as good.
// This is done through P/Invoke instead of using WinForms to avoid the WinForms dependency (which does not support self-contained trimming when publishing).

#include <Windows.h>
#include <commdlg.h>
#include <objbase.h>
#include <string>
#include <vector>

extern "C" int __declspec(dllexport) __stdcall
OpenFileDialog(const wchar_t* filter, const wchar_t* initialDirectory, wchar_t* outFileBuffer, int outFileBufferLength, int multiSelect) {
    OPENFILENAMEW openFilename{};
    openFilename.lStructSize = sizeof(OPENFILENAMEW);
    openFilename.lpstrFilter = filter;
    openFilename.lpstrInitialDir = initialDirectory;
    openFilename.lpstrFile = outFileBuffer;
    openFilename.nMaxFile = (DWORD)outFileBufferLength;
    openFilename.Flags = OFN_NOCHANGEDIR | OFN_FILEMUSTEXIST | OFN_EXPLORER | (multiSelect ? OFN_ALLOWMULTISELECT : 0);
    if (GetOpenFileNameW(&openFilename) == 0) {
        return 1;
    }

    return 0;
}

extern "C" int __declspec(dllexport) __stdcall
SaveFileDialog(const wchar_t* filter, const wchar_t* initialDirectory, wchar_t* outFileBuffer, int outFileBufferLength) {
    OPENFILENAMEW openFilename{};
    openFilename.lStructSize = sizeof(OPENFILENAMEW);
    openFilename.lpstrFilter = filter;
    openFilename.lpstrInitialDir = initialDirectory;
    openFilename.lpstrFile = outFileBuffer;
    openFilename.nMaxFile = (DWORD)outFileBufferLength;
    openFilename.Flags = OFN_NOCHANGEDIR | OFN_OVERWRITEPROMPT | OFN_EXPLORER;
    if (GetSaveFileNameW(&openFilename) == 0) {
        return 1;
    }
    return 0;
}