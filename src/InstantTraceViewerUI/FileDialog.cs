using System;
using System.IO;
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
