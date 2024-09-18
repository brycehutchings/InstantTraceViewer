using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace InstantTraceViewerUI.Etw
{
    internal class EtwSessionEnabledProvider
    {
        // This may be a GUID or special provider string.
        public string Name { get; set; }

        public TraceEventLevel Level { get; set; } = TraceEventLevel.Verbose;

        public ulong MatchAnyKeyword { get; set; } = ulong.MaxValue;
    }

    internal class EtwSessionProfile
    {
        public string DisplayName { get; set; }

        public KernelTraceEventParser.Keywords KernelKeywords { get; set; } = KernelTraceEventParser.Keywords.None;

        public List<EtwSessionEnabledProvider> Providers { get; set; } = new List<EtwSessionEnabledProvider>();
    }
}