using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace InstantTraceViewer
{
    public delegate bool TraceTableRowSelector(ITraceTableSnapshot traceTableSnapshot, int rowIndex);

    public interface ITraceTableRowSelectorParseResults
    {
        Expression<TraceTableRowSelector> Expression { get; }
    }

    public class SyntaxParseException : Exception
    {
        public SyntaxParseException(string message)
            : base(message)
        {
        }
    }

    public class TraceTableRowSelectorSyntax
    {
        private class ParseResults : ITraceTableRowSelectorParseResults
        {
            public Expression<TraceTableRowSelector> Expression { get; set; }
        }

        private class ParserState
        {
            private bool _eof = false;

            public IEnumerator<string> TokenEnumerator;
            //public Expression<TraceTableRowSelector> Expression { get; set; }

            public string CurrentToken
            {
                get
                {
                    if (_eof)
                    {
                        throw new ArgumentException("Unexpected end of input");
                    }
                    return TokenEnumerator.Current;
                }
            }

            public bool Eof => _eof;

            public void MoveNextToken()
            {
                _eof = !TokenEnumerator.MoveNext();
            }
        }

        public const string StringEqualsOperatorName = "equals";
        public const string StringEqualsCSOperatorName = "equals_cs";
        public const string StringContainsOperatorName = "contains";
        public const string StringContainsCSOperatorName = "contains_cs";
        public const string StringMatchesOperatorName = "matches";
        public const string StringMatchesCSOperatorName = "matches_cs";
        public const string StringMatchesRegexModifierName = $"regex";

        private static readonly ParameterExpression Param1TraceTableSnapshot = Expression.Parameter(typeof(ITraceTableSnapshot), "TraceTableSnapshot");
        private static readonly ParameterExpression Param2RowIndex = Expression.Parameter(typeof(int), "RowIndex");
        private readonly IReadOnlyDictionary<string, TraceSourceSchemaColumn> _columns;

        public TraceTableRowSelectorSyntax(TraceTableSchema schema)
        {
            // Column variables are in the form '@<column name>' and are case-insensitive.
            _columns = schema.Columns.ToDictionary(c => CreateColumnVariableName(c), c => c, StringComparer.CurrentCultureIgnoreCase);
        }

        public ITraceTableRowSelectorParseResults Parse(string text)
        {
            // Split the text up by whitespace or punctuation
            IEnumerable<string> tokens = SyntaxTokenizer.Tokenize(text);

            ParserState state = new() { TokenEnumerator = tokens.GetEnumerator() };
            state.MoveNextToken(); // Move to first token.

            Expression expressionBody = ParseExpression(state);

            return new ParseResults
            {
                Expression = Expression.Lambda<TraceTableRowSelector>(expressionBody, Param1TraceTableSnapshot, Param2RowIndex)
            };
        }

        public static string CreateEscapedStringLiteral(string text)
        {
            return '"' + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\"", "\\\"") + '"';
        }

        private Expression ParseExpression(ParserState state)
        {
            Expression leftExpression = ParseTerm(state);

            while (!state.Eof)
            {
                if (string.Equals(state.CurrentToken, ")", StringComparison.InvariantCultureIgnoreCase))
                {
                    // Unlike other parsing which advances after observing a token, we do not advance the token when we hit a ')' because there are potentially
                    // multiple levels of recursive leftExpression parsing that need to observe the ')' token so they know to pop up the stack until we get back to
                    // the "term" handler that encountered the '('.
                    break;
                }
                else if (string.Equals(state.CurrentToken, "and", StringComparison.InvariantCultureIgnoreCase))
                {
                    state.MoveNextToken();
                    // "AND" has higher precedence than "OR" so we only parse a single term rather than a full leftExpression.
                    leftExpression = Expression.AndAlso(leftExpression, ParseTerm(state));
                }
                else if (string.Equals(state.CurrentToken, "or", StringComparison.InvariantCultureIgnoreCase))
                {
                    state.MoveNextToken();
                    // "OR" has lower precedence than "AND" and so we parse everything to the right as if it was a grouped leftExpression.
                    leftExpression = Expression.OrElse(leftExpression, ParseExpression(state));
                }
                else
                {
                    throw new ArgumentException($"Unexpected token: {state.CurrentToken}");
                }
            }

            return leftExpression;
        }

        private Expression ParseTerm(ParserState state)
        {
            if (string.Equals(state.CurrentToken, "(", StringComparison.InvariantCultureIgnoreCase))
            {
                state.MoveNextToken();
                Expression expression = ParseExpression(state);

                if (state.Eof || !string.Equals(state.CurrentToken, ")", StringComparison.InvariantCultureIgnoreCase))
                {
                    throw new ArgumentException("Expected ')'");
                }
                state.MoveNextToken();

                return expression;
            }
            else if (string.Equals(state.CurrentToken, "not", StringComparison.InvariantCultureIgnoreCase))
            {
                state.MoveNextToken();
                return Expression.Not(ParseTerm(state));
            }
            else
            {
                return ParsePredicate(state);
            }
        }

        private Expression ParsePredicate(ParserState state)
        {
            if (!_columns.TryGetValue(state.CurrentToken, out TraceSourceSchemaColumn column))
            {
                throw new ArgumentException($"Unknown column: {state.CurrentToken}");
            }

            state.MoveNextToken();

            string operatorName = state.CurrentToken;
            if (operatorName.Equals(StringEqualsOperatorName, StringComparison.InvariantCultureIgnoreCase) ||
                operatorName.Equals(StringEqualsCSOperatorName, StringComparison.InvariantCultureIgnoreCase))
            {
                state.MoveNextToken();
                string value = ReadStringLiteral(state.CurrentToken);
                state.MoveNextToken();
                return ComparisonExpressions.StringEquals(GetTableString(column), Expression.Constant(value, typeof(string)), GetStringComparisonType(operatorName));
            }
            else if (operatorName.Equals(StringContainsOperatorName, StringComparison.InvariantCultureIgnoreCase) ||
                     operatorName.Equals(StringContainsCSOperatorName, StringComparison.InvariantCultureIgnoreCase))
            {
                state.MoveNextToken();
                string value = ReadStringLiteral(state.CurrentToken);
                state.MoveNextToken();
                return ComparisonExpressions.StringContains(GetTableString(column), Expression.Constant(value, typeof(string)), GetStringComparisonType(operatorName));
            }
            else if (operatorName.Equals(StringMatchesOperatorName, StringComparison.InvariantCultureIgnoreCase) ||
                     operatorName.Equals(StringMatchesCSOperatorName, StringComparison.InvariantCultureIgnoreCase))
            {
                state.MoveNextToken();

                // Token following matches/matches_cs may be an optional "regex" modifier.
                string token = state.CurrentToken;
                if (state.CurrentToken.Equals(StringMatchesRegexModifierName, StringComparison.InvariantCultureIgnoreCase))
                {
                    state.MoveNextToken();
                    string value = ReadStringLiteral(state.CurrentToken);
                    state.MoveNextToken();
                    return ComparisonExpressions.MatchesRegex(GetTableString(column), Expression.Constant(value, typeof(string)), GetRegexOptions(operatorName));
                }
                else
                {
                    string value = ReadStringLiteral(state.CurrentToken);
                    state.MoveNextToken();
                    return ComparisonExpressions.Matches(GetTableString(column), Expression.Constant(value, typeof(string)), GetRegexOptions(operatorName));
                }
            }
            else
            {
                throw new ArgumentException($"Unknown operator: {state.CurrentToken}");
            }
        }

        private static StringComparison GetStringComparisonType(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? StringComparison.InvariantCulture : StringComparison.InvariantCultureIgnoreCase;

        private static RegexOptions GetRegexOptions(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? RegexOptions.None : RegexOptions.IgnoreCase;

        // The tokenizer will handle escaped characters so at this stage just the quotes need to be trimmed off.
        private static string ReadStringLiteral(string quotedStringLiteral)
        {
            if (quotedStringLiteral.Length < 2 || quotedStringLiteral[0] != '"' || quotedStringLiteral[^1] != '"')
            {
                throw new ArgumentException("Invalid string literal");
            }

            return quotedStringLiteral.Substring(1, quotedStringLiteral.Length - 2);
        }

        private static Expression GetTableString(TraceSourceSchemaColumn column)
        {
            var method = typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnString))!;
            return Expression.Call(
                Param1TraceTableSnapshot,
                method,
                Param2RowIndex,
                Expression.Constant(column),
                Expression.Constant(false) /* allowMultiline */);
        }

        // Column names could contain spaces and other characters that would make parsing ambiguous/troublesome so strip out everything except for letters and numbers
        // and have every column variable start with '@'.
        public static string CreateColumnVariableName(TraceSourceSchemaColumn column) => '@' + GetColumnNameForParsing(column);
        private static string GetColumnNameForParsing(TraceSourceSchemaColumn column) => Regex.Replace(column.Name, "[^\\w]", "");
    };
}