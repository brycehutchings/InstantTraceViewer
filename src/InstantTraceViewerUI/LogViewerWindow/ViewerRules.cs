using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using InstantTraceViewer;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace InstantTraceViewerUI
{
    internal enum TraceRowRuleAction
    {
        Include,
        Exclude
    }

    internal class ViewerRules
    {
        private List<(string Rule, TraceRowRuleAction Action)> _visibleRules = new();

        private IReadOnlyList<(TraceTableRowPredicate Predicate, TraceRowRuleAction Action)> _visibleRulePredicates;
        private int _visibleRulePredicatesRuleGenerationId = -1;
        private int _visibleRulePredicatesTableGenerationId = -1;

        // Bumping this id will trigger a complete rebuild of the filtered trace table.
        public int GenerationId { get; private set; }

        public int RuleCount => _visibleRules.Count;

        public void ClearRules()
        {
            _visibleRules.Clear();
            GenerationId++;
        }

        public void AddIncludeRule(string rule)
        {
            // Include rules go last to ensure anything already excluded stays excluded.
            _visibleRules.Add((rule, TraceRowRuleAction.Include));
            GenerationId++;
        }

        public void AddExcludeRule(string rule)
        {
            _visibleRules.Insert(0, (rule, TraceRowRuleAction.Exclude));
            GenerationId++;
        }

        public TraceRowRuleAction GetVisibleAction(ITraceTableSnapshot traceTable, int unfilteredRowIndex)
        {
            EnsureVisibleRulePredicates(traceTable);

            TraceRowRuleAction defaultAction = TraceRowRuleAction.Include;
            foreach (var rule in _visibleRulePredicates)
            {
                if (rule.Predicate(traceTable, unfilteredRowIndex))
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
                _visibleRules = _visibleRules.ToList(),
                GenerationId = GenerationId
            };
        }

        private void EnsureVisibleRulePredicates(ITraceTableSnapshot traceTable)
        {
            if (_visibleRulePredicatesRuleGenerationId != GenerationId ||
                _visibleRulePredicatesTableGenerationId != traceTable.GenerationId)
            {
                Trace.WriteLine("Recompiling rule predicates...");
                var parser = new TraceTableRowPredicateLanguage(traceTable.Schema);
                List<(TraceTableRowPredicate Predicate, TraceRowRuleAction Action)> newPredicates = new();
                foreach (var rule in _visibleRules)
                {
                    var parseResult = parser.TryParse(rule.Rule);
                    if (!parseResult.WasSuccessful)
                    {
                        Trace.WriteLine($"Failed to parse rule '{rule.Rule}': {parseResult.Message}");
                        continue;
                    }

                    newPredicates.Add((parseResult.Value.Compile(), rule.Action));
                }
                _visibleRulePredicates = newPredicates;
            }

            _visibleRulePredicatesRuleGenerationId = GenerationId;
            _visibleRulePredicatesTableGenerationId = traceTable.GenerationId;
        }
    }
}
