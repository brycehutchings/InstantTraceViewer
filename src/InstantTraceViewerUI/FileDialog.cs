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
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public unsafe struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public void* lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        public static string OpenFile(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            /*
            var dialog = new OpenFileDialog();
            dialog.InitialDirectory = initialDirectory;
            dialog.Filter = filter;
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                persistDirectory(Path.GetDirectoryName(dialog.FileName));

                return dialog.FileName;
            }

            return null;
            */
            unsafe
            {
                OpenFileName ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf(typeof(OpenFileName)),
                    lpstrFilter = filter.Replace('|', '\0') + '\0',
                    lpstrFile = NativeMemory.AllocZeroed(260, (nuint)Marshal.SystemDefaultCharSize), //new string(new char[256]),
                    nMaxFile = 260,
                    lpstrInitialDir = initialDirectory,
                    // Flags = 0x8 /* OFN_NOCHANGEDIR */ | 0x1000 /* OFN_FILEMUSTEXIST */
                    Flags = 0x1000 /* OFN_FILEMUSTEXIST */
                };
                if (GetOpenFileName(ofn))
                {
                    string file = Marshal.PtrToStringUni(new nint(ofn.lpstrFile));
                    NativeMemory.Free(ofn.lpstrFile);
                    persistDirectory(Path.GetDirectoryName(file));
                    return file;
                }
            }

            return null;
        }
        public static IReadOnlyList<string> OpenMultipleFiles(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            /*
            var dialog = new OpenFileDialog();
            dialog.InitialDirectory = initialDirectory;
            dialog.Filter = filter;
            dialog.Multiselect = true;
            if (dialog.ShowDialog() == DialogResult.OK && dialog.FileNames.Length > 0)
            {
                persistDirectory(Path.GetDirectoryName(dialog.FileNames.First()));

                return dialog.FileNames;
            }*/

            return Array.Empty<string>();
        }

        public static string SaveFile(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            /*var dialog = new SaveFileDialog();
            dialog.InitialDirectory = initialDirectory;
            dialog.Filter = filter;
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                persistDirectory(Path.GetDirectoryName(dialog.FileName));

                return dialog.FileName;
            }*/

            return null;
        }
    }
}
