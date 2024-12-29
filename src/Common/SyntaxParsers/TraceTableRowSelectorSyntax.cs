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

        public const string StringEqualsCSOperatorName = "equals_cs";
        public const string StringInOperatorName = "in";
        public const string StringInCSOperatorName = "in_cs";
        public const string StringContainsOperatorName = "contains";
        public const string StringContainsCSOperatorName = "contains_cs";
        public const string StringMatchesOperatorName = "matches";
        public const string StringMatchesCSOperatorName = "matches_cs";
        public const string StringMatchesRegexModifierName = "regex";

        public const string LessThanOperatorName = "<";
        public const string LessThanOrEqualOperatorName = "<=";
        public const string EqualsOperatorName = "==";
        public const string NotEqualsOperatorName = "!=";
        public const string GreaterThanOperatorName = ">";
        public const string GreaterThanOrEqualOperatorName = ">=";

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
                    if (rightExpression == null)
                    {
                        return null; // Unexpected token or end of input.
                    }

                    leftExpression = Expression.AndAlso(leftExpression, rightExpression);
                }
                else if (state.CurrentTokenMatches("or"))
                {
                    state.MoveNextToken();
                    // "OR" has lower precedence than "AND" and so we parse everything to the right as if it was a grouped leftExpression.
                    Expression rightExpression = TryParseExpression(state, closeParenthesisExpected);
                    if (rightExpression == null)
                    {
                        return null; // Unexpected token or end of input.
                    }

                    leftExpression = Expression.OrElse(leftExpression, rightExpression);
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
            else if (matchedColumn == _schema.TimestampColumn)
            {
                return TryParseTimestampPredicate(state, matchedColumn);
            }


             return TryParseStringPredicate(state, matchedColumn);
        }

        private Expression TryParseStringPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            // "equals_cs" has no symbol-based operator equivalent to avoid adding less commonly seen operators like =~, etc.
            if (state.CurrentTokenMatches(NotEqualsOperatorName) || state.CurrentTokenMatches(EqualsOperatorName) ||
                state.CurrentTokenMatches(StringEqualsCSOperatorName))
            {
                bool isNegated = state.CurrentTokenMatches(NotEqualsOperatorName);

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

                Expression result = ComparisonExpressions.StringEquals(GetTableString(matchedColumn), Expression.Constant(value, typeof(string)), comparisonType.Value);
                return isNegated ? Expression.Not(result) : result;
            }
            else if (state.CurrentTokenMatches(StringInOperatorName) || state.CurrentTokenMatches(StringInCSOperatorName))
            {
                IEqualityComparer<string> equalityComparer = TryGetStringEqualityComparer(state.CurrentToken);
                if (equalityComparer == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();

                IReadOnlyList<string> values = TryReadStringLiteralList(state);
                if (values == null)
                {
                    return null; // Unexpected token or end of input.
                }

                state.MoveNextToken();

                var valueSet = values.ToHashSet(equalityComparer);

                return Expression.Call(
                    Expression.Constant(valueSet),
                    typeof(HashSet<string>).GetMethod(nameof(HashSet<string>.Contains))!,
                    GetTableString(matchedColumn));
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

        private Expression TryParseTimestampPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            Func<Expression, Expression, BinaryExpression> binaryExpression = TryGetBinaryExpression(state);
            if (binaryExpression != null)
            {
                state.MoveNextToken();
                string timestampStr = TryReadStringLiteral(state);
                if (timestampStr == null)
                {
                    return null; // Unexpected token or end of input.
                }

                if (!DateTime.TryParse(timestampStr, out DateTime timestamp))
                {
                    state.CurrentTokenMatches("\"[timestamp]\""); // Encourage some timestamp string literal
                    return null; // Invalid timestamp format.
                }

                state.MoveNextToken();
                return binaryExpression(GetTableTimestamp(matchedColumn), Expression.Constant(timestamp));
            }

            return null; // Unexpected token or end of input.
        }

        private Expression TryParseLevelPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            Func<Expression, Expression, BinaryExpression> binaryExpression = TryGetBinaryExpression(state);
            if (binaryExpression != null)
            {
                state.MoveNextToken();
                UnifiedLevel? level = TryReadLevel(state);
                if (level == null)
                {
                    return null; // Unexpected token or end of input.
                }
                state.MoveNextToken();
                return binaryExpression(Expression.Convert(GetTableLevel(matchedColumn), typeof(int)), Expression.Constant((int)level.Value));
            }

            return null; // Unexpected token or end of input.
        }

        private static StringComparison TryGetStringComparisonType(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? StringComparison.InvariantCulture : StringComparison.InvariantCultureIgnoreCase;

        private static IEqualityComparer<string> TryGetStringEqualityComparer(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? StringComparer.InvariantCulture : StringComparer.InvariantCultureIgnoreCase;

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

        private static IReadOnlyList<string> TryReadStringLiteralList(ParserState state)
        {
            if (state.Eof || state.CurrentToken.Length == 0 || state.CurrentToken[0] != '[')
            {
                state.CurrentTokenMatches("["); // Add [ as expected token.
                return null; // End of input or malformed string literal.
            }

            List<string> values = new();
            while (true)
            {
                state.MoveNextToken(); // Move past '[' or ','
                string value = TryReadStringLiteral(state);
                if (value == null)
                {
                    return null; // Unexpected token or end of input.
                }
                values.Add(value);

                state.MoveNextToken();
                if (state.CurrentTokenMatches("]"))
                {
                    break;
                }
                else if (!state.CurrentTokenMatches(","))
                {
                    return null; // Unexpected token or end of input.
                }
            }
            return values;
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

        private static Func<Expression, Expression, BinaryExpression> TryGetBinaryExpression(ParserState state)
        {
            Func<Expression, Expression, BinaryExpression> matchingOperatorExpression = null;

            void CheckCurrentToken(string operatorName, Func<Expression, Expression, BinaryExpression> operatorExpression)
            {
                if (state.CurrentTokenMatches(operatorName))
                {
                    matchingOperatorExpression = operatorExpression;
                }
            }

            CheckCurrentToken(LessThanOperatorName, Expression.LessThan);
            CheckCurrentToken(LessThanOrEqualOperatorName, Expression.LessThanOrEqual);
            CheckCurrentToken(EqualsOperatorName, Expression.Equal);
            CheckCurrentToken(NotEqualsOperatorName, Expression.NotEqual);
            CheckCurrentToken(GreaterThanOperatorName, Expression.GreaterThan);
            CheckCurrentToken(GreaterThanOrEqualOperatorName, Expression.GreaterThanOrEqual);

            return matchingOperatorExpression;
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

        private static Expression GetTableTimestamp(TraceSourceSchemaColumn column)
            => Expression.Call(
                Param1TraceTableSnapshot,
                typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnDateTime))!,
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