using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal enum TraceRowRuleAction
    {
        Include,
        Exclude
    }

    internal struct TraceRowRule
    {
        // TODO: Needed later for editing rules.
        // public string Rule { get; }

        public Func<int, bool> IsMatch { get; set; }
    }

    internal record TraceRowVisibleRule(TraceRowRule Rule, TraceRowRuleAction Action);
    internal record TraceRecordHighlightRule(TraceRowRule Rule, Vector4 Color);

    internal class ViewerRules
    {
        public List<TraceRowVisibleRule> VisibleRules { get; set; } = new List<TraceRowVisibleRule>();

        public List<TraceRecordHighlightRule> HighlightRules { get; set; } = new List<TraceRecordHighlightRule>();

        public int GenerationId { get; set; } = 1;

        public TraceRowRuleAction GetVisibleAction(int unfilteredRowIndex)
        {
            TraceRowRuleAction defaultAction = TraceRowRuleAction.Include;
            foreach (var rule in VisibleRules)
            {
                if (rule.Rule.IsMatch(unfilteredRowIndex))
                {
                    return rule.Action;
                }

                // If user is explicitly including things, then exclude anything unmatched.
                // Thus if user only excludes things, then include anything unmatched.
                if (rule.Action == TraceRowRuleAction.Include)
                {
                    defaultAction = TraceRowRuleAction.Exclude;
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
