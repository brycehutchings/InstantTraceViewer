using System;
using System.Collections.Generic;
using System.Linq;
using InstantTraceViewer;

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

        public Func<ITraceTableSnapshot, int, bool> IsMatch { get; set; }
    }

    internal record TraceRowVisibleRule(TraceRowRule Rule, TraceRowRuleAction Action);

    internal class ViewerRules
    {
        public List<TraceRowVisibleRule> VisibleRules { get; set; } = new List<TraceRowVisibleRule>();

        public int GenerationId { get; set; } = 1;

        public TraceRowRuleAction GetVisibleAction(ITraceTableSnapshot traceTable, int unfilteredRowIndex)
        {
            TraceRowRuleAction defaultAction = TraceRowRuleAction.Include;
            foreach (var rule in VisibleRules)
            {
                if (rule.Rule.IsMatch(traceTable, unfilteredRowIndex))
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
                VisibleRules = VisibleRules.ToList()
            };
        }
    }
}
