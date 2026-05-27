using System;

namespace InstantTraceViewerUI
{
    internal interface IWindow : IDisposable
    {
        bool DrawWindow(IUiCommands uiCommands);
    }

    internal interface IUiCommands
    {
        void AddWindow(IWindow window);

        void ShowMessageBox(string message, string title, bool isError);
    }

    interface ITraceSourceGuiExtensions
    {
        void RenderToolstripExtras(IUiCommands uiCommands);
    }
}
