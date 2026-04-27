using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls.Dialogs;

namespace InstantTraceViewerUI
{
    internal static class FileDialog
    {
        private const int BufferLength = 8192;

        public static unsafe string OpenFile(string filter, string initialDirectory)
        {
            char[] buffer = new char[BufferLength];
            string reformattedFilter = ReformatFilter(filter);
            fixed (char* bufferPtr = buffer)
            fixed (char* filterPtr = reformattedFilter)
            fixed (char* initialDirPtr = initialDirectory)
            {
                OPENFILENAMEW ofn = new()
                {
                    lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = new HWND(Win32ImGuiHost.MainWindowHandle),
                    lpstrFilter = filterPtr,
                    lpstrInitialDir = initialDirPtr,
                    lpstrFile = bufferPtr,
                    nMaxFile = BufferLength,
                    Flags = OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR | OPEN_FILENAME_FLAGS.OFN_FILEMUSTEXIST | OPEN_FILENAME_FLAGS.OFN_EXPLORER,
                };

                if (!PInvoke.GetOpenFileName(ref ofn))
                {
                    return null;
                }

                return new string(bufferPtr);
            }
        }

        public static unsafe IReadOnlyList<string> OpenMultipleFiles(string filter, string initialDirectory)
        {
            char[] buffer = new char[BufferLength];
            string reformattedFilter = ReformatFilter(filter);
            fixed (char* bufferPtr = buffer)
            fixed (char* filterPtr = reformattedFilter)
            fixed (char* initialDirPtr = initialDirectory)
            {
                OPENFILENAMEW ofn = new()
                {
                    lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = new HWND(Win32ImGuiHost.MainWindowHandle),
                    lpstrFilter = filterPtr,
                    lpstrInitialDir = initialDirPtr,
                    lpstrFile = bufferPtr,
                    nMaxFile = BufferLength,
                    Flags = OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR | OPEN_FILENAME_FLAGS.OFN_FILEMUSTEXIST | OPEN_FILENAME_FLAGS.OFN_EXPLORER | OPEN_FILENAME_FLAGS.OFN_ALLOWMULTISELECT,
                };

                if (!PInvoke.GetOpenFileName(ref ofn))
                {
                    return Array.Empty<string>();
                }

                // Buffer is null-separated; double-null terminates the list.
                List<string> paths = new();
                int start = 0;
                for (int i = 0; i < BufferLength; i++)
                {
                    if (buffer[i] == '\0')
                    {
                        if (i == start)
                        {
                            break;
                        }
                        paths.Add(new string(buffer, start, i - start));
                        start = i + 1;
                    }
                }

                if (paths.Count == 1)
                {
                    return paths;
                }

                // First entry is the directory, rest are file names.
                string directoryName = paths[0];
                return paths.Skip(1).Select(p => Path.Combine(directoryName, p)).ToList();
            }
        }

        public static unsafe string SaveFile(string filter, string initialDirectory, string defaultExtension)
        {
            char[] buffer = new char[BufferLength];
            string reformattedFilter = ReformatFilter(filter);
            fixed (char* bufferPtr = buffer)
            fixed (char* filterPtr = reformattedFilter)
            fixed (char* initialDirPtr = initialDirectory)
            {
                OPENFILENAMEW ofn = new()
                {
                    lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = new HWND(Win32ImGuiHost.MainWindowHandle),
                    lpstrFilter = filterPtr,
                    lpstrInitialDir = initialDirPtr,
                    lpstrFile = bufferPtr,
                    nMaxFile = BufferLength,
                    Flags = OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR | OPEN_FILENAME_FLAGS.OFN_OVERWRITEPROMPT | OPEN_FILENAME_FLAGS.OFN_EXPLORER,
                };

                if (!PInvoke.GetSaveFileName(ref ofn))
                {
                    return null;
                }

                string outFile = new(bufferPtr);
                if (!Path.HasExtension(outFile))
                {
                    outFile = Path.ChangeExtension(outFile, defaultExtension);
                }
                return outFile;
            }
        }

        private static string ReformatFilter(string filter) => filter.Replace('|', '\0') + '\0';
    }
}
