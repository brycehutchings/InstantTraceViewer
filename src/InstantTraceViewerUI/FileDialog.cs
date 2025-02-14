using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InstantTraceViewerUI
{
    internal static class FileDialog
    {
        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static unsafe extern int OpenFileDialog(string filter, string initialDirectory, char* outFileBuffer, int outFileBufferLength, int multiSelect);

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static unsafe extern int SaveFileDialog(string filter, string initialDirectory, char* outFileBuffer, int outFileBufferLength);

        public static string OpenFile(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            string outFileBuffer = new string('\0', 8192);
            unsafe
            {
                fixed (char* outFilePtr = outFileBuffer)
                {
                    if (OpenFileDialog(ReformatFilter(filter), initialDirectory, outFilePtr, outFileBuffer.Length, 0) != 0)
                    {
                        return null;
                    }

                    string outFileTrimmed = new string(outFilePtr); // Trim off null terminators
                    persistDirectory(Path.GetDirectoryName(outFileTrimmed));
                    return outFileTrimmed;
                }
            }
        }

        public static IReadOnlyList<string> OpenMultipleFiles(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            string outFileBuffer = new string('\0', 8192);
            unsafe
            {
                fixed (char* outFilePtr = outFileBuffer)
                {
                    if (OpenFileDialog(ReformatFilter(filter), initialDirectory, outFilePtr, outFileBuffer.Length, 1 /* multiselect */) != 0)
                    {
                        return Array.Empty<string>();
                    }

                    // Break the buffer which is null separated into individual strings
                    List<string> paths = new List<string>();
                    int start = 0;
                    for (int i = 0; i < outFileBuffer.Length; i++)
                    {
                        if (outFileBuffer[i] == '\0')
                        {
                            if (i == start)
                            {
                                break; // Double null terminator indicates the end of the list.
                            }
                            paths.Add(outFileBuffer.Substring(start, i - start));
                            start = i + 1;
                        }
                    }

                    // The first path is the folder, the remaining paths are files in that folder, so combine them.
                    persistDirectory(paths[0]);
                    return paths.Skip(1).Select(p => Path.Combine(paths[0], p)).ToArray();
                }
            }
        }

        public static string SaveFile(string filter, string initialDirectory, string defaultExtension, Action<string> persistDirectory)
        {
            string outFileBuffer = new string('\0', 8192);
            unsafe
            {
                fixed (char* outFilePtr = outFileBuffer)
                {
                    if (SaveFileDialog(ReformatFilter(filter), initialDirectory, outFilePtr, outFileBuffer.Length) != 0)
                    {
                        return null;
                    }

                    string outFileTrimmed = new string(outFilePtr); // Trim off null terminators
                    if (!Path.HasExtension(outFileTrimmed))
                    {
                        outFileTrimmed = Path.ChangeExtension(outFileTrimmed, defaultExtension);
                    }

                    persistDirectory(Path.GetDirectoryName(outFileTrimmed));
                    return outFileTrimmed;
                }
            }
        }

        private static string ReformatFilter(string filter) => filter.Replace('|', '\0') + '\0';
    }
}
