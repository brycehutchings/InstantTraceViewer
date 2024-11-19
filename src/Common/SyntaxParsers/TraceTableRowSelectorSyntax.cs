using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace InstantTraceViewer
{
    public delegate bool TraceTableRowSelector(ITraceTableSnapshot traceTableSnapshot, int rowIndex);

    public class TraceTableRowSelectorParseResults
    {
        public Expression<TraceTableRowSelector> Expression { get; init; }

        public IReadOnlyList<string> ExpectedTokens { get; init; }

        public int ExpectedTokenStartIndex { get; init; }
    }

    public class TraceTableRowSelectorSyntax
    {
        private class ParserState
        {
            public List<string> ExpectedTokens = new();

            public int ExpectedTokenStartIndex = 0;

            public IEnumerator<Token> TokenEnumerator;

            public bool CurrentTokenMatches(string expectedToken)
            {
                ExpectedTokens.Add(expectedToken);
                if (Eof)
                {
                    return false;
                }

                return CurrentToken.Equals(expectedToken, StringComparison.InvariantCultureIgnoreCase);
            }

            public string CurrentToken
            {
                get
                {
                    if (Eof)
                    {
                        throw new ArgumentException("Unexpected end of input");
                    }

                    return TokenEnumerator.Current.Text;
                }
            }

            public bool Eof { get; private set; }

            public void MoveNextToken()
            {
                bool movedNext = !TokenEnumerator.MoveNext();
                Debug.Assert(!movedNext); // We should never move past the last token because the EOF token should be hit instead.

                // If we moved to the next token, then the previous token was valid and any tracked expected tokens can be cleared
                // for building a new set of expected tokens for this new token.
                ExpectedTokens.Clear();
                Eof = TokenEnumerator.Current.Text == SyntaxTokenizer.EofText;
                ExpectedTokenStartIndex = TokenEnumerator.Current.StartIndex;
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
        private readonly TraceTableSchema _schema;

        public TraceTableRowSelectorSyntax(TraceTableSchema schema)
        {
            _schema = schema;
        }

        public TraceTableRowSelectorParseResults Parse(string text)
        {
            // Split the text up by whitespace or punctuation
            IEnumerable<Token> tokens = SyntaxTokenizer.Tokenize(text);
            ParserState state = new() { TokenEnumerator = tokens.GetEnumerator() };

            Expression expressionBody = null;
            try
            {
                state.MoveNextToken(); // Move to first token.
                expressionBody = ParseExpression(state);
            }
            catch (Exception)
            {
                // FIXME: Don't use exceptions for control flow. The caller will get the details from ExpectedTokens.
            }

            return new TraceTableRowSelectorParseResults
            {
                Expression = expressionBody != null ? Expression.Lambda<TraceTableRowSelector>(expressionBody, Param1TraceTableSnapshot, Param2RowIndex) : null,
                ExpectedTokens = state.ExpectedTokens,
                ExpectedTokenStartIndex = state.ExpectedTokenStartIndex
            };
        }

        public static string CreateEscapedStringLiteral(string text)
        {
            return '"' + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\"", "\\\"") + '"';
        }

        private static string UnescapeStringLiteral(string text)
        {
            return text.Substring(1, text.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\r", "\r").Replace("\\\\", "\\");
        }

        private Expression ParseExpression(ParserState state, bool closeParenthesisExpected = false)
        {
            Expression leftExpression = ParseTerm(state);

            while (closeParenthesisExpected || !state.Eof)
            {
                // The ")" check must come first so that it is captured as an allowed token before failing.
                if (closeParenthesisExpected && state.CurrentTokenMatches(")"))
                {
                    // Unlike other parsing which advances after observing a token, we do not advance the token when we hit a ')' because there are potentially
                    // multiple levels of recursive leftExpression parsing that need to observe the ')' token so they know to pop up the stack until we get back to
                    // the "term" handler that encountered the '('.
                    break;
                }
                else if (state.CurrentTokenMatches("and"))
                {
                    state.MoveNextToken();
                    // "AND" has higher precedence than "OR" so we only parse a single term rather than a full leftExpression.
                    leftExpression = Expression.AndAlso(leftExpression, ParseTerm(state));
                }
                else if (state.CurrentTokenMatches("or"))
                {
                    state.MoveNextToken();
                    // "OR" has lower precedence than "AND" and so we parse everything to the right as if it was a grouped leftExpression.
                    leftExpression = Expression.OrElse(leftExpression, ParseExpression(state, closeParenthesisExpected));
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
            if (state.CurrentTokenMatches("("))
            {
                state.MoveNextToken();
                Expression expression = ParseExpression(state, closeParenthesisExpected: true);

                if (!state.CurrentTokenMatches(")"))
                {
                    throw new ArgumentException("Expected ')'");
                }
                state.MoveNextToken();

                return expression;
            }
            else if (state.CurrentTokenMatches("not"))
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
            // Loop through ALL columns rather than do a dictionary lookup so the parser state can log each expected token.
            TraceSourceSchemaColumn? matchedColumn = null;
            foreach (var column in _schema.Columns)
            {
                string columnVariableName = CreateColumnVariableName(column);
                if (matchedColumn == null && state.CurrentTokenMatches(columnVariableName))
                {
                    matchedColumn = column;
                }
            }

            if (matchedColumn == null)
            {
                throw new ArgumentException($"Unknown column: {state.CurrentToken}");
            }

            state.MoveNextToken();

            if (state.CurrentTokenMatches(StringEqualsOperatorName) || state.CurrentTokenMatches(StringEqualsCSOperatorName))
            {
                StringComparison comparisonType = GetStringComparisonType(state.CurrentToken);
                state.MoveNextToken();

                string value = ReadStringLiteral(state);
                state.MoveNextToken();

                return ComparisonExpressions.StringEquals(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), comparisonType);
            }
            else if (state.CurrentTokenMatches(StringContainsOperatorName) || state.CurrentTokenMatches(StringContainsCSOperatorName))
            {
                StringComparison comparisonType = GetStringComparisonType(state.CurrentToken);
                state.MoveNextToken();

                string value = ReadStringLiteral(state);
                state.MoveNextToken();

                return ComparisonExpressions.StringContains(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), comparisonType);
            }
            else if (state.CurrentTokenMatches(StringMatchesOperatorName) || state.CurrentTokenMatches(StringMatchesCSOperatorName))
            {
                RegexOptions regexOptions = GetRegexOptions(state.CurrentToken);
                state.MoveNextToken();

                // Token following matches/matches_cs may be an optional "regex" modifier.
                if (state.CurrentTokenMatches(StringMatchesRegexModifierName))
                {
                    state.MoveNextToken();

                    string value = ReadStringLiteral(state);
                    state.MoveNextToken();
                    return ComparisonExpressions.MatchesRegex(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), regexOptions);
                }
                else
                {
                    string value = ReadStringLiteral(state);
                    state.MoveNextToken();
                    return ComparisonExpressions.Matches(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), regexOptions);
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

        // Validates the token is a quoted string literal, provides proper "expected" content
        private static string ReadStringLiteral(ParserState state)
        {
            if (state.Eof || state.CurrentToken.Length == 0 || state.CurrentToken[0] != '"')
            {
                state.CurrentTokenMatches("\""); // Add quote as expected token.
                throw new ArgumentException("Invalid string literal");
            }
            else if (state.CurrentToken.Length == 1)
            {
                state.CurrentTokenMatches("\"[text]\""); // We just have a starting quote. Encourage some content with closing quote.
                throw new ArgumentException("Invalid string literal");
            }
            else if (state.CurrentToken[^1] != '"')
            {
                state.CurrentTokenMatches(state.CurrentToken + '"'); // Add closing quote as expected token.
                throw new ArgumentException("Invalid string literal");
            }

            return UnescapeStringLiteral(state.CurrentToken);
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
        // and have every matchedColumn variable start with '@'.
        public static string CreateColumnVariableName(TraceSourceSchemaColumn column) => '@' + GetColumnNameForParsing(column);
        private static string GetColumnNameForParsing(TraceSourceSchemaColumn column) => Regex.Replace(column.Name, "[^\\w]", "");
    };
}