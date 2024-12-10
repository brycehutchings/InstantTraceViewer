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

        public Token ActualToken { get; init; }
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
                    // Caller should be catching Eof before accessing CurrentToken to avoid exceptions for control flow because
                    // exception handling is too slow for real-time parsing.
                    Debug.Assert(!Eof);
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

        public const string EqualsOperatorName = "equals";
        public const string StringEqualsCSOperatorName = "equals_cs";
        public const string StringContainsOperatorName = "contains";
        public const string StringContainsCSOperatorName = "contains_cs";
        public const string StringMatchesOperatorName = "matches";
        public const string StringMatchesCSOperatorName = "matches_cs";
        public const string StringMatchesRegexModifierName = "regex";

        public const string AtLeastLevelOperatorName = "atleast";
        public const string AtMostLevelOperatorName = "atmost";

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
                expressionBody = TryParseExpression(state);
            }
            catch (Exception)
            {
                Debug.Fail("Parser should not throw exceptions. This makes real-time parsing too slow.");
            }

            return new TraceTableRowSelectorParseResults
            {
                Expression = expressionBody != null ? Expression.Lambda<TraceTableRowSelector>(expressionBody, Param1TraceTableSnapshot, Param2RowIndex) : null,
                ExpectedTokens = state.ExpectedTokens.Distinct().ToArray(),
                ExpectedTokenStartIndex = state.ExpectedTokenStartIndex,
                ActualToken = state.TokenEnumerator.Current
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

        private Expression TryParseExpression(ParserState state, bool closeParenthesisExpected = false)
        {
            Expression leftExpression = TryParseTerm(state);

            while (leftExpression != null)
            {
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
                    Expression rightExpression = TryParseTerm(state);
                    if (rightExpression != null)
                    {
                        leftExpression = Expression.AndAlso(leftExpression, rightExpression);
                    }
                }
                else if (state.CurrentTokenMatches("or"))
                {
                    state.MoveNextToken();
                    // "OR" has lower precedence than "AND" and so we parse everything to the right as if it was a grouped leftExpression.
                    Expression rightExpression = TryParseExpression(state, closeParenthesisExpected);
                    if (rightExpression != null)
                    {
                        leftExpression = Expression.OrElse(leftExpression, rightExpression);
                    }
                }
                else
                {
                    if (state.Eof)
                    {
                        // Break out of the loop after all of the token match checks so that the Expected field is populated.
                        break;
                    }

                    return null; // Unexpected token.
                }
            }

            return leftExpression;
        }

        private Expression TryParseTerm(ParserState state)
        {
            if (state.CurrentTokenMatches("("))
            {
                state.MoveNextToken();
                Expression expression = TryParseExpression(state, closeParenthesisExpected: true);

                if (!state.CurrentTokenMatches(")"))
                {
                    return null; // Unexpected token or end of input.
                }
                state.MoveNextToken();

                return expression;
            }
            else if (state.CurrentTokenMatches("not"))
            {
                state.MoveNextToken();
                Expression expression = TryParseTerm(state);
                return expression != null ? Expression.Not(expression) : null;
            }
            else
            {
                return TryParsePredicate(state);
            }
        }

        private Expression TryParsePredicate(ParserState state)
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
                return null; // Unexpected token or end of input.
            }

            state.MoveNextToken();

            if (matchedColumn == _schema.UnifiedLevelColumn)
            {
                return TryParseLevelPredicate(state, matchedColumn);
            }

            return TryParseStringPredicate(state, matchedColumn);
        }

        private Expression TryParseStringPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            if (state.CurrentTokenMatches(EqualsOperatorName) || state.CurrentTokenMatches(StringEqualsCSOperatorName))
            {
                StringComparison? comparisonType = TryGetStringComparisonType(state.CurrentToken);
                if (comparisonType == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();

                string value = TryReadStringLiteral(state);
                if (value == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();

                return ComparisonExpressions.StringEquals(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), comparisonType.Value);
            }
            else if (state.CurrentTokenMatches(StringContainsOperatorName) || state.CurrentTokenMatches(StringContainsCSOperatorName))
            {
                StringComparison? comparisonType = TryGetStringComparisonType(state.CurrentToken);
                if (comparisonType == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();

                string value = TryReadStringLiteral(state);
                if (value == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();

                return ComparisonExpressions.StringContains(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), comparisonType.Value);
            }
            else if (state.CurrentTokenMatches(StringMatchesOperatorName) || state.CurrentTokenMatches(StringMatchesCSOperatorName))
            {
                RegexOptions? regexOptions = TryGetRegexOptions(state.CurrentToken);
                if (regexOptions == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();

                // Token following matches/matches_cs may be an optional "regex" modifier.
                if (state.CurrentTokenMatches(StringMatchesRegexModifierName))
                {
                    state.MoveNextToken();

                    string value = TryReadStringLiteral(state);
                    if (value == null)
                    {
                        return null; // Unexpected token or end of input.
                    }

                    state.MoveNextToken();
                    return ComparisonExpressions.MatchesRegex(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), regexOptions.Value);
                }
                else
                {
                    string value = TryReadStringLiteral(state);
                    if (value == null)
                    {
                        return null; // Unexpected token or end of input.
                    }

                    state.MoveNextToken();
                    return ComparisonExpressions.Matches(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), regexOptions.Value);
                }
            }

            return null; // Unexpected token or end of input.
        }

        private Expression TryParseLevelPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            if (state.CurrentTokenMatches(AtLeastLevelOperatorName))
            {
                state.MoveNextToken();
                UnifiedLevel? level = TryReadLevel(state);
                if (level == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();
                return Expression.LessThanOrEqual(Expression.Convert(GetTableLevel(matchedColumn), typeof(int)), Expression.Constant((int)level.Value));
            }
            else if (state.CurrentTokenMatches(AtMostLevelOperatorName))
            {
                state.MoveNextToken();
                UnifiedLevel? level = TryReadLevel(state);
                if (level == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();
                return Expression.GreaterThanOrEqual(Expression.Convert(GetTableLevel(matchedColumn), typeof(int)), Expression.Constant((int)level.Value));
            }
            else if (state.CurrentTokenMatches(EqualsOperatorName))
            {
                state.MoveNextToken();
                UnifiedLevel? level = TryReadLevel(state);
                if (level == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();
                return Expression.Equal(GetTableLevel(matchedColumn), Expression.Constant(level.Value));
            }

            return null; // Unexpected token or end of input.
        }

        private static StringComparison TryGetStringComparisonType(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? StringComparison.InvariantCulture : StringComparison.InvariantCultureIgnoreCase;

        private static RegexOptions TryGetRegexOptions(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? RegexOptions.None : RegexOptions.IgnoreCase;

        // Validates the token is a quoted string literal, provides proper "expected" content
        private static string TryReadStringLiteral(ParserState state)
        {
            if (state.Eof || state.CurrentToken.Length == 0 || state.CurrentToken[0] != '"')
            {
                state.CurrentTokenMatches("\""); // Add quote as expected token.
                return null; // End of input or malformed string literal.
            }
            else if (state.CurrentToken.Length == 1)
            {
                state.CurrentTokenMatches("\"[text]\""); // We just have a starting quote. Encourage some content with closing quote.
                return null; // End of input or malformed string literal.
            }
            else if (state.CurrentToken[^1] != '"')
            {
                state.CurrentTokenMatches(state.CurrentToken + '"'); // Add closing quote as expected token.
                return null; // End of input or malformed string literal.
            }

            return UnescapeStringLiteral(state.CurrentToken);
        }

        private static UnifiedLevel? TryReadLevel(ParserState state)
        {
            UnifiedLevel? matchedLevel = null;

            // Read levels in reverse order so intellisense shows them in the order that semantically matches minimum/maximum.
            foreach (var level in Enum.GetValues<UnifiedLevel>().Reverse())
            {
                if (state.CurrentTokenMatches(level.ToString()))
                {
                    matchedLevel = level; // Continue looping so all 'expected' tokens are collected.
                }
            }

            return matchedLevel;
        }

        private static Expression GetTableString(TraceSourceSchemaColumn column)
            => Expression.Call(
                Param1TraceTableSnapshot,
                typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnString))!,
                Param2RowIndex,
                Expression.Constant(column),
                Expression.Constant(false) /* allowMultiline */);

        private static Expression GetTableLevel(TraceSourceSchemaColumn column)
            => Expression.Call(
                Param1TraceTableSnapshot,
                typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnUnifiedLevel))!,
                Param2RowIndex,
                Expression.Constant(column));

        // Column names could contain spaces and other characters that would make parsing ambiguous/troublesome so strip out everything except for letters and numbers
        // and have every matchedColumn variable start with '@'.
        public static string CreateColumnVariableName(TraceSourceSchemaColumn column)
            => '@' + GetColumnNameForParsing(column);
        private static string GetColumnNameForParsing(TraceSourceSchemaColumn column)
            => Regex.Replace(column.Name, "[^\\w]", "");
    };
}