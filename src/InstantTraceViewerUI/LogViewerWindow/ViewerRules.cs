using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace InstantTraceViewerUI
{
    public enum TraceRowRuleAction
    {
        Include,
        Exclude,
        Highlight
    }

    // Properties of interface cannot be changed directly because the generationId is managed outside of the Rules.
    public interface IRule
    {
        public string Query { get; }
        public TraceRowRuleAction Action { get; }

        public bool Enabled { get; }

        // The result of parsing the query.
        public TraceTableRowSelectorParseResults ParseResult { get; }

        // Predicate is compiled from the query if successful.
        public TraceTableRowSelector? Predicate { get; }

        // Highlight color can be changed directly because it does not affect filtering, unlike the other properties.
        public HighlightRowBgColor? HighlightColor { get; set;  }
    }

    internal class ViewerRules
    {
        class Rule : IRule
        {
            public required string Query { get; set; }
            public required TraceRowRuleAction Action { get; set; }
            public bool Enabled { get; set; } = true;
            // The result of parsing the query.
            public TraceTableRowSelectorParseResults ParseResult { get; set; }
            // Predicate is compiled from the query if successful.
            public TraceTableRowSelector? Predicate { get; set; }
            public HighlightRowBgColor? HighlightColor { get; set; }
        }

        private List<Rule> _visibleRules = new();

        private int _visibleRulePredicatesRuleGenerationId = -1;
        private int _visibleRulePredicatesTableGenerationId = -1;

        // Bumping this id will trigger a complete rebuild of the filtered trace table.
        public int GenerationId { get; private set; } = 1;

        public bool _applyFiltering = true;
        public bool ApplyFiltering
        {
            get => _applyFiltering;
            set
            {
                _applyFiltering = value;
                GenerationId++;
            }
        }

        public void ClearRules()
        {
            _visibleRules.Clear();
            GenerationId++;
        }

        public void AddRule(string query, TraceRowRuleAction ruleAction, HighlightRowBgColor? highlightColor = null)
        {
            // Include rules go last to ensure anything already excluded stays excluded.
            if (ruleAction == TraceRowRuleAction.Exclude)
            {
                if (highlightColor.HasValue)
                {
                    throw new ArgumentException("Highlight color cannot be set for exclude rules.");
                }

                // Exclude rules go first to ensure they exclude things that might be matched by a preexisting include rule.
                _visibleRules.Insert(0, new Rule { Query = query, Action = ruleAction, HighlightColor = highlightColor });
            }
            else
            {
                _visibleRules.Add(new Rule { Query = query, Action = ruleAction, HighlightColor = highlightColor });
            }
            GenerationId++;
        }

        public void AppendRule(bool enabled, TraceRowRuleAction action, string query, HighlightRowBgColor? highlightColor = null)
        {
            _visibleRules.Add(new Rule { Query = query, Enabled = enabled, Action = action, HighlightColor = highlightColor });
            GenerationId++;
        }

        public void UpdateRule(int index, string query)
        {
            _visibleRules[index].Query = query;
            GenerationId++;
        }

        public void RemoveRule(int index)
        {
            _visibleRules.RemoveAt(index);
            GenerationId++;
        }

        public void MoveRule(int index, int newIndex)
        {
            var rule = _visibleRules[index];
            _visibleRules.RemoveAt(index);
            _visibleRules.Insert(newIndex, rule);
            GenerationId++;
        }

        public void SetRuleEnabled(int index, bool enabled)
        {
            _visibleRules[index].Enabled = enabled;
            GenerationId++;
        }

        public void SetRuleAction(int index, TraceRowRuleAction action)
        {
            _visibleRules[index].Action = action;
            GenerationId++;
        }

        public IReadOnlyList<IRule> Rules => _visibleRules;

        public HighlightRowBgColor? TryGetHighlightColor(ITraceTableSnapshot traceTable, int rowIndex)
        {
            EnsureVisibleRulePredicates(traceTable);

            foreach (var rule in _visibleRules)
            {
                if (!rule.HighlightColor.HasValue)
                {
                    continue;
                }

                if (rule.Predicate == null)
                {
                    continue; // This rule could not be parsed.
                }

                if (!rule.Enabled)
                {
                    continue;
                }

                if (rule.Predicate(traceTable, rowIndex))
                {
                    return rule.HighlightColor;
                }
            }
            return null;
        }

        public TraceRowRuleAction GetVisibleAction(ITraceTableSnapshot traceTable, int unfilteredRowIndex)
        {
            if (_visibleRules.Count == 0 || !ApplyFiltering)
            {
                return TraceRowRuleAction.Include;
            }

            EnsureVisibleRulePredicates(traceTable);

            TraceRowRuleAction defaultAction = TraceRowRuleAction.Include;
            foreach (var rule in _visibleRules)
            {
                // Highlighting is handled separately.
                if (rule.Action == TraceRowRuleAction.Highlight)
                {
                    continue;
                }

                if (rule.Predicate == null)
                {
                    continue; // This rule could not be parsed.
                }

                if (!rule.Enabled)
                {
                    continue;
                }

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
                _applyFiltering = _applyFiltering,
                GenerationId = GenerationId
            };
        }

        private void EnsureVisibleRulePredicates(ITraceTableSnapshot traceTable)
        {
            if (_visibleRulePredicatesRuleGenerationId != GenerationId ||
                _visibleRulePredicatesTableGenerationId != traceTable.GenerationId)
            {
                Trace.WriteLine("Recompiling query predicates...");
                var parser = new TraceTableRowSelectorSyntax(traceTable.Schema);
                foreach (var rule in _visibleRules)
                {
                    rule.ParseResult = parser.Parse(rule.Query);
                    rule.Predicate = rule.ParseResult.Expression?.Compile();
                    rule.Enabled &= (rule.Predicate != null); // Disable if the rule could not be parsed.
                }
            }

            _visibleRulePredicatesRuleGenerationId = GenerationId;
            _visibleRulePredicatesTableGenerationId = traceTable.GenerationId;
        }
    }
}
