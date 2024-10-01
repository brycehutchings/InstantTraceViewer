using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal enum TraceRecordRuleAction
    {
        Include,
        Exclude
    }

    internal struct TraceRecordRule
    {
        // TODO: Needed later for editing rules.
        // public string Rule { get; }

        public Func<TraceRecord, bool> IsMatch { get; set; }
    }

    internal record TraceRecordVisibleRule(TraceRecordRule Rule, TraceRecordRuleAction Action);
    internal record TraceRecordHighlightRule(TraceRecordRule Rule, Vector4 Color);

    internal class ViewerRules
    {
        public List<TraceRecordVisibleRule> VisibleRules { get; set; } = new List<TraceRecordVisibleRule>();

        public List<TraceRecordHighlightRule> HighlightRules { get; set; } = new List<TraceRecordHighlightRule>();

        public TraceRecordRuleAction GetVisibleAction(TraceRecord record)
        {
            TraceRecordRuleAction defaultAction = TraceRecordRuleAction.Include;
            foreach (var rule in VisibleRules)
            {
                if (rule.Rule.IsMatch(record))
                {
                    return rule.Action;
                }

                // If user is explicitly including things, then exclude anything unmatched.
                // Thus if user only excludes things, then include anything unmatched.
                if (rule.Action == TraceRecordRuleAction.Include)
                {
                    defaultAction = TraceRecordRuleAction.Exclude;
                }
            }
            return defaultAction;
        }

        public ViewerRules Clone()
        {
            return new ViewerRules
            {
                VisibleRules = VisibleRules.ToList(),
                HighlightRules = HighlightRules.ToList()
            };
        }
    }
}
