using InstantTraceViewer;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace InstantTraceViewerUI
{
    public enum TraceRowRuleAction
    {
        Include,
        Exclude
    }

    public interface IRule
    {
        public string Query { get; }
        public TraceRowRuleAction Action { get; }

        public bool Enabled { get; }

        // The result of parsing the query.
        public TraceTableRowSelectorParseResults ParseResult { get; }

        // Predicate is compiled from the query if successful.
        public TraceTableRowSelector? Predicate { get; }
    }

    internal class ViewerRules
    {
        class Rule : IRule
        {
            public required string Query { get; init; }
            public required TraceRowRuleAction Action { get; init; }

            public bool Enabled { get; set; } = true;

            // The result of parsing the query.
            public TraceTableRowSelectorParseResults ParseResult { get; set; }

            // Predicate is compiled from the query if successful.
            public TraceTableRowSelector? Predicate { get; set; }
        }

        private List<Rule> _visibleRules = new();

        private int _visibleRulePredicatesRuleGenerationId = -1;
        private int _visibleRulePredicatesTableGenerationId = -1;

        // Bumping this id will trigger a complete rebuild of the filtered trace table.
        public int GenerationId { get; private set; } = 1;

        public void ClearRules()
        {
            _visibleRules.Clear();
            GenerationId++;
        }

        public void AddIncludeRule(string query)
        {
            // Include rules go last to ensure anything already excluded stays excluded.
            _visibleRules.Add(new Rule { Query = query, Action = TraceRowRuleAction.Include });
            GenerationId++;
        }

        public void AddExcludeRule(string query)
        {
            // Exclude rules go first to ensure they exclude things that might be matched by a preexisting include rule.
            _visibleRules.Insert(0, new Rule { Query = query, Action = TraceRowRuleAction.Exclude });
            GenerationId++;
        }

        public void UpdateRule(int index, string query)
        {
            Rule oldRule = _visibleRules[index];
            _visibleRules[index] = new Rule { Query = query, Action = oldRule.Action, Enabled = oldRule.Enabled };
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

        public IReadOnlyList<IRule> Rules => _visibleRules;

        public TraceRowRuleAction GetVisibleAction(ITraceTableSnapshot traceTable, int unfilteredRowIndex)
        {
            if (_visibleRules.Count == 0)
            {
                return TraceRowRuleAction.Include;
            }

            EnsureVisibleRulePredicates(traceTable);

            TraceRowRuleAction defaultAction = TraceRowRuleAction.Include;
            foreach (var rule in _visibleRules)
            {
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
                }
            }

            _visibleRulePredicatesRuleGenerationId = GenerationId;
            _visibleRulePredicatesTableGenerationId = traceTable.GenerationId;
        }
    }
}
