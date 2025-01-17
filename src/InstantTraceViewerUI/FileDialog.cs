﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace InstantTraceViewerUI
{
    internal static class FileDialog
    {
        public static string OpenFile(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            var dialog = new OpenFileDialog();
            dialog.InitialDirectory = initialDirectory;
            dialog.Filter = filter;
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                persistDirectory(Path.GetDirectoryName(dialog.FileName));

                return dialog.FileName;
            }

            return null;
        }
        public static IReadOnlyList<string> OpenMultipleFiles(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            var dialog = new OpenFileDialog();
            dialog.InitialDirectory = initialDirectory;
            dialog.Filter = filter;
            dialog.Multiselect = true;
            if (dialog.ShowDialog() == DialogResult.OK && dialog.FileNames.Length > 0)
            {
                persistDirectory(Path.GetDirectoryName(dialog.FileNames.First()));

                return dialog.FileNames;
            }

            return Array.Empty<string>();
        }

        public static string SaveFile(string filter, string initialDirectory, Action<string> persistDirectory)
        {
            var dialog = new SaveFileDialog();
            dialog.InitialDirectory = initialDirectory;
            dialog.Filter = filter;
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                persistDirectory(Path.GetDirectoryName(dialog.FileName));

                return dialog.FileName;
            }

            return null;
        }
    }
}
