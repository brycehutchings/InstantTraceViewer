using InstantTraceViewer;
using System.Collections.Generic;

namespace InstantTraceViewerUI
{
    // Allows multiple log viewer windows to share the same trace source.
    internal class SharedTraceSource
    {
        private readonly HashSet<LogViewerWindow> _windows = new();

        public SharedTraceSource(ITraceSource traceSource)
        {
            TraceSource = traceSource;
        }

        public ITraceSource TraceSource { get; private set; }

        public SharedTraceSource AddRef(LogViewerWindow newWindow)
        {
            _windows.Add(newWindow);
            return this;
        }

        public void ReleaseRef(LogViewerWindow newWindow)
        {
            _windows.Remove(newWindow);
            if (_windows.Count == 0)
            {
                TraceSource.Dispose();
                TraceSource = null;
            }
        }
    }
}
