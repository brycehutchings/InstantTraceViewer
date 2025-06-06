﻿using System.Diagnostics;
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

            public int TokenIndex = 0;

            public IReadOnlyList<Token> Tokens;

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

                    return Tokens[TokenIndex].Text;
                }
            }

            public bool Eof { get; private set; }

            public void MoveNextToken()
            {
                TokenIndex++;
                if (TokenIndex >= Tokens.Count)
                {
                    Debug.Fail("Moved past end of token list"); // We should never move past the last token because the EOF token should be hit instead.
                }

                // If we moved to the next token, then the previous token was valid and any tracked expected tokens can be cleared
                // for building a new set of expected tokens for this new token.
                ExpectedTokens.Clear();
                Eof = CurrentToken == SyntaxTokenizer.EofText;
                ExpectedTokenStartIndex = Tokens[TokenIndex].StartIndex;
            }

            public ParserState Clone()
            {
                return new ParserState
                {
                    ExpectedTokens = new List<string>(ExpectedTokens),
                    ExpectedTokenStartIndex = ExpectedTokenStartIndex,
                    Tokens = Tokens,
                    TokenIndex = TokenIndex,
                    Eof = Eof
                };
            }

            public void ReplaceWith(ParserState state)
            {
                ExpectedTokens = state.ExpectedTokens;
                ExpectedTokenStartIndex = state.ExpectedTokenStartIndex;
                Tokens = state.Tokens;
                TokenIndex = state.TokenIndex;
                Eof = state.Eof;
            }

            public void MergeExpectedTokens(ParserState otherState)
            {
                // Both int and string parsing failed. Now we need to figure out what to show the user.
                // To resolve, we will use whichever one made it further in the parsing process.
                if (ExpectedTokenStartIndex > otherState.ExpectedTokenStartIndex)
                {
                    // This state already has what it needs to show the user.
                }
                else if (ExpectedTokenStartIndex < otherState.ExpectedTokenStartIndex)
                {
                    // The other parser actually made it further, so use it instead.
                    ReplaceWith(otherState);
                }
                else
                {
                    // Both parsers made it to the same point, so union the expected tokens.
                    ExpectedTokens.AddRange(otherState.ExpectedTokens);
                }
            }
        }

        public const string AndOperatorName = "and";
        public const string OrOperatorName = "or";

        public const string InOperatorName = "in";
        public const string StringInCSOperatorName = "in_cs";
        public const string StringContainsOperatorName = "contains";
        public const string StringContainsCSOperatorName = "contains_cs";
        public const string StringMatchesOperatorName = "matches";
        public const string StringMatchesCSOperatorName = "matches_cs";
        public const string StringMatchesRegexModifierName = "regex";

        public const string LessThanOperatorName = "<";
        public const string LessThanOrEqualOperatorName = "<=";
        public const string EqualsOperatorName = "==";
        public const string EqualsCIOperatorName = "=~";
        public const string NotEqualsOperatorName = "!=";
        public const string NotEqualsCIOperatorName = "!~";
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
            ParserState state = new() { Tokens = tokens.ToList() };

            Expression expressionBody = null;
            try
            {
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
                ActualToken = state.Tokens[state.TokenIndex]
            };
        }

        public static string CreateEscapedStringLiteral(string text)
            => '"' + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\"", "\\\"") + '"';

        public static string CreateEscapedStringLiteral(DateTime dateTime)
            => $"\"{FriendlyStringify.ToString(dateTime)}\"";

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
                else if (state.CurrentTokenMatches(AndOperatorName))
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
                else if (state.CurrentTokenMatches(OrOperatorName))
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
            else if (matchedColumn == _schema.ProcessIdColumn || matchedColumn == _schema.ThreadIdColumn)
            {
                return TryParseMany(state, matchedColumn, TryParseStringPredicate, TryParseIntIdentifierPredicate);
            }

            return TryParseStringPredicate(state, matchedColumn);
        }

        private Expression TryParseStringPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            if (state.CurrentTokenMatches(EqualsOperatorName) || state.CurrentTokenMatches(EqualsCIOperatorName) ||
                state.CurrentTokenMatches(NotEqualsOperatorName) || state.CurrentTokenMatches(NotEqualsCIOperatorName))
            {
                bool isNegated = state.CurrentToken.StartsWith("!");

                StringComparison? comparisonType = TryGetStringEqualityComparisonType(state.CurrentToken);
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
            else if (state.CurrentTokenMatches(InOperatorName) || state.CurrentTokenMatches(StringInCSOperatorName))
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
            Func<Expression, Expression, BinaryExpression> binaryExpression = TryGetBinaryExpression(state, true);
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
                    state.CurrentTokenMatches("\"<timestamp>\""); // Encourage some timestamp string literal
                    return null; // Invalid timestamp format.
                }

                state.MoveNextToken();
                return binaryExpression(GetTableTimestamp(matchedColumn), Expression.Constant(timestamp));
            }

            return null; // Unexpected token or end of input.
        }

        // Parses for a column that holds an integer value but does not provide < or > or the like.
        private Expression TryParseIntIdentifierPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            // Inequality operators don't make sense for integers that represent IDs.
            Func<Expression, Expression, BinaryExpression> binaryExpression = TryGetBinaryExpression(state, false);
            if (binaryExpression != null)
            {
                state.MoveNextToken();
                int? value = TryReadInt(state);
                if (value != null)
                {
                    state.MoveNextToken();
                    return binaryExpression(GetTableInt(matchedColumn), Expression.Constant(value.Value));
                }
            }
            else if (state.CurrentTokenMatches(InOperatorName))
            {
                state.MoveNextToken();

                IReadOnlyList<int> values = TryReadIntList(state);
                if (values != null)
                {
                    state.MoveNextToken();
                    return Expression.Call(
                        Expression.Constant(values.ToHashSet()),
                        typeof(HashSet<int>).GetMethod(nameof(HashSet<int>.Contains))!,
                        GetTableInt(matchedColumn));
                }
            }

            return null; // Unexpected token or end of input.
        }

        private Expression TryParseLevelPredicate(ParserState state, TraceSourceSchemaColumn matchedColumn)
        {
            Func<Expression, Expression, BinaryExpression> binaryExpression = TryGetBinaryExpression(state, true);
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
            else if (state.CurrentTokenMatches(InOperatorName))
            {
                state.MoveNextToken();

                IReadOnlyList<UnifiedLevel> values = TryReadLevelList(state);
                if (values == null)
                {
                    return null; // Unexpected token or end of input.
                }
                state.MoveNextToken();

                return Expression.Call(
                    Expression.Constant(values.ToHashSet()),
                    typeof(HashSet<UnifiedLevel>).GetMethod(nameof(HashSet<UnifiedLevel>.Contains))!,
                    GetTableLevel(matchedColumn));
            }
            return null; // Unexpected token or end of input.
        }

        // == and != are case sensitive to match major language behaviors (Python, C, etc).
        // =~ and !~ are case insensitive which is borrowed from the Kusto query language.
        private static StringComparison TryGetStringEqualityComparisonType(string operatorName)
            => operatorName.EndsWith("~", StringComparison.InvariantCultureIgnoreCase)
                 ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture;

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
                state.CurrentTokenMatches("\"<text>\""); // We just have a starting quote. Encourage some content with closing quote.
                return null; // End of input or malformed string literal.
            }
            else if (state.CurrentToken[^1] != '"')
            {
                state.CurrentTokenMatches(state.CurrentToken + '"'); // Add closing quote as expected token.
                return null; // End of input or malformed string literal.
            }

            return UnescapeStringLiteral(state.CurrentToken);
        }

        // Runs through parsers using the first to parse successfully. If all fail, the one that got furthest is used.
        private Expression TryParseMany(ParserState state, TraceSourceSchemaColumn matchedColumn, params Func<ParserState, TraceSourceSchemaColumn, Expression>[] parsers)
        {
            ParserState mergedParserState = state.Clone();
            foreach (var parser in parsers)
            {
                ParserState parserAttempt = state.Clone();
                Expression expression = parser(parserAttempt, matchedColumn);
                if (expression != null)
                {
                    state.ReplaceWith(parserAttempt);
                    return expression;
                }

                mergedParserState.MergeExpectedTokens(parserAttempt);
            }

            state.ReplaceWith(mergedParserState);
            return null;
        }

        private static IReadOnlyList<string> TryReadStringLiteralList(ParserState state) => TryReadList<string>(state, TryReadStringLiteral);

        private static IReadOnlyList<int> TryReadIntList(ParserState state) => TryReadList<int>(state, state => TryReadInt(state));

        private static IReadOnlyList<UnifiedLevel> TryReadLevelList(ParserState state) => TryReadList<UnifiedLevel>(state, state => TryReadLevel(state));

        private static IReadOnlyList<T> TryReadList<T>(ParserState state, Func<ParserState, object> readItem)
        {
            if (!state.CurrentTokenMatches("["))
            {
                return null; // End of input or not start of list.
            }

            List<T> values = new();
            while (true)
            {
                state.MoveNextToken(); // Move past '[' or ','
                object value = readItem(state);
                if (value == null)
                {
                    return null; // Unexpected token or end of input.
                }
                values.Add((T)value);

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

        private static int? TryReadInt(ParserState state)
        {
            if (!state.Eof && int.TryParse(state.CurrentToken, out int value))
            {
                return value;
            }

            state.CurrentTokenMatches("<integer>"); // Encourage some integer content.
            return null;
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

        private static Func<Expression, Expression, BinaryExpression> TryGetBinaryExpression(ParserState state, bool includeInequalityOperators)
        {
            Func<Expression, Expression, BinaryExpression> matchingOperatorExpression = null;

            void CheckCurrentToken(string operatorName, Func<Expression, Expression, BinaryExpression> operatorExpression)
            {
                if (state.CurrentTokenMatches(operatorName))
                {
                    matchingOperatorExpression = operatorExpression;
                }
            }

            CheckCurrentToken(EqualsOperatorName, Expression.Equal);
            CheckCurrentToken(NotEqualsOperatorName, Expression.NotEqual);

            if (includeInequalityOperators)
            {
                CheckCurrentToken(LessThanOperatorName, Expression.LessThan);
                CheckCurrentToken(LessThanOrEqualOperatorName, Expression.LessThanOrEqual);
                CheckCurrentToken(GreaterThanOperatorName, Expression.GreaterThan);
                CheckCurrentToken(GreaterThanOrEqualOperatorName, Expression.GreaterThanOrEqual);
            }

            return matchingOperatorExpression;
        }

        private static Expression GetTableString(TraceSourceSchemaColumn column)
            => Expression.Call(
                Param1TraceTableSnapshot,
                typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnValueString))!,
                Param2RowIndex,
                Expression.Constant(column),
                Expression.Constant(false) /* allowMultiline */);

        private static Expression GetTableInt(TraceSourceSchemaColumn column)
            => Expression.Call(
                Param1TraceTableSnapshot,
                typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnValueInt))!,
                Param2RowIndex,
                Expression.Constant(column));

        private static Expression GetTableLevel(TraceSourceSchemaColumn column)
            => Expression.Call(
                Param1TraceTableSnapshot,
                typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnValueUnifiedLevel))!,
                Param2RowIndex,
                Expression.Constant(column));

        private static Expression GetTableTimestamp(TraceSourceSchemaColumn column)
            => Expression.Call(
                Param1TraceTableSnapshot,
                typeof(ITraceTableSnapshot).GetMethod(nameof(ITraceTableSnapshot.GetColumnValueDateTime))!,
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