using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace InstantTraceViewerUI.Etw
{
    internal class EtwSessionEnabledProvider
    {
        // This may be a GUID or special provider string.
        public required string Name { get; set; }

        // This is not fed to the ETW session. It is only used for display purposes in the "Start real-time" configuration window.
        public required string Description { get; set; }

        public TraceEventLevel Level { get; set; } = TraceEventLevel.Verbose;

        public ulong MatchAnyKeyword { get; set; } = ulong.MaxValue;

        public bool StackwalkEnabled { get; set; }
    }

    internal class EtwSessionProfile
    {
        public required string DisplayName { get; set; }

        public KernelTraceEventParser.Keywords KernelKeywords { get; set; } = KernelTraceEventParser.Keywords.None;

        public KernelTraceEventParser.Keywords KernelStackwalkKeywords { get; set; } = KernelTraceEventParser.Keywords.None;

        public List<EtwSessionEnabledProvider> Providers { get; set; } = new List<EtwSessionEnabledProvider>();
    }
}