using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;

namespace InstantTraceViewer
{
    public delegate bool TraceTableRowSelector(ITraceTableSnapshot traceTableSnapshot, int rowIndex);

    public class TraceTableRowSelectorParseResults
    {
        public IReadOnlyList<string> Tokens;
        public int Index;
        public Expression<TraceTableRowSelector> Expression;

        public string CurrentToken
        {
            get
            {
                if (Eof)
                {
                    throw new ArgumentException("Unexpected end of input");
                }
                return Tokens[Index];
            }
        }

        public bool Eof => Index >= Tokens.Count;

        public void MoveNextToken()
        {
            Index++;
        }
    }

    public class TraceTableRowSelectorSyntax
    {
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

        public TraceTableRowSelectorParseResults Parse(string text)
        {
            // Split the text up by whitespace or punctuation
            string[] tokens = Tokenize(text).ToArray();

            TraceTableRowSelectorParseResults result = new() { Index = 0, Tokens = tokens };
            Expression expressionBody = ParseExpression(result, true);
            result.Expression = Expression.Lambda<TraceTableRowSelector>(expressionBody, Param1TraceTableSnapshot, Param2RowIndex);
            return result;
        }

        public static string CreateEscapedStringLiteral(string text)
        {
            return '"' + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\"", "\\\"") + '"';
        }

        private Expression ParseExpression(TraceTableRowSelectorParseResults result, bool allowChaining, bool stopAtCloseParenthesis = false)
        {
            Expression expression = null;

            do
            {
                if (string.Equals(result.CurrentToken, "(", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    expression = ParseExpression(result, true, true);
                }
                else if (stopAtCloseParenthesis && expression != null && string.Equals(result.CurrentToken, ")", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    return expression;
                }
                else if (string.Equals(result.CurrentToken, "not", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    expression = Expression.Not(ParseExpression(result, false, false));
                }
                else if (allowChaining && expression != null && string.Equals(result.CurrentToken, "and", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    // Don't allow chaining after 'and' because 'or' has higher precedence.
                    expression = Expression.AndAlso(expression, ParseExpression(result, false, stopAtCloseParenthesis));
                }
                else if (allowChaining && expression != null && string.Equals(result.CurrentToken, "or", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    // Allow chaining after 'or' because it has higher precedence than 'and'.
                    expression = Expression.OrElse(expression, ParseExpression(result, true, stopAtCloseParenthesis));
                }
                else if (expression == null)
                {
                    expression = ParseComparison(result);
                }
                else
                {
                    throw new ArgumentException($"Unexpected token: {result.CurrentToken}");
                }
            }
            while (!result.Eof && allowChaining);

            if (expression == null)
            {
                throw new ArgumentException($"Expected expression");
            }

            return expression;
        }

        private Expression ParseComparison(TraceTableRowSelectorParseResults result)
        {
            if (!_columns.TryGetValue(result.CurrentToken, out TraceSourceSchemaColumn column))
            {
                throw new ArgumentException($"Unknown column: {result.CurrentToken}");
            }

            result.MoveNextToken();

            string operatorName = result.CurrentToken;
            if (operatorName.Equals(StringEqualsOperatorName, StringComparison.InvariantCultureIgnoreCase) ||
                operatorName.Equals(StringEqualsCSOperatorName, StringComparison.InvariantCultureIgnoreCase))
            {
                result.MoveNextToken();
                string value = ReadStringLiteral(result.CurrentToken);
                result.MoveNextToken();
                return ComparisonExpressions.StringEquals(GetTableString(column), Expression.Constant(value, typeof(string)), GetStringComparisonType(operatorName));
            }
            else if (operatorName.Equals(StringContainsOperatorName, StringComparison.InvariantCultureIgnoreCase) ||
                     operatorName.Equals(StringContainsCSOperatorName, StringComparison.InvariantCultureIgnoreCase))
            {
                result.MoveNextToken();
                string value = ReadStringLiteral(result.CurrentToken);
                result.MoveNextToken();
                return ComparisonExpressions.StringContains(GetTableString(column), Expression.Constant(value, typeof(string)), GetStringComparisonType(operatorName));
            }
            else if (operatorName.Equals(StringMatchesOperatorName, StringComparison.InvariantCultureIgnoreCase) ||
                     operatorName.Equals(StringMatchesCSOperatorName, StringComparison.InvariantCultureIgnoreCase))
            {
                result.MoveNextToken();

                // Token following matches/matches_cs may be an optional "regex" modifier.
                string token = result.CurrentToken;
                if (result.CurrentToken.Equals(StringMatchesRegexModifierName, StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    string value = ReadStringLiteral(result.CurrentToken);
                    result.MoveNextToken();
                    return ComparisonExpressions.MatchesRegex(GetTableString(column), Expression.Constant(value, typeof(string)), GetRegexOptions(operatorName));
                }
                else
                {
                    string value = ReadStringLiteral(result.CurrentToken);
                    result.MoveNextToken();
                    return ComparisonExpressions.Matches(GetTableString(column), Expression.Constant(value, typeof(string)), GetRegexOptions(operatorName));
                }
            }
            else
            {
                throw new ArgumentException($"Unknown operator: {result.CurrentToken}");
            }
        }

        private static StringComparison GetStringComparisonType(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? StringComparison.InvariantCulture : StringComparison.InvariantCultureIgnoreCase;

        private static RegexOptions GetRegexOptions(string operatorName)
            => operatorName.EndsWith("_cs", StringComparison.InvariantCultureIgnoreCase)
                 ? RegexOptions.None : RegexOptions.IgnoreCase;

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

        private static IEnumerable<string> Tokenize(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                // Skip whitespace
                for (; i < text.Length && char.IsWhiteSpace(text[i]); i++) ;

                if (i >= text.Length)
                {
                    break;
                }
                else if (text[i] == '"')
                {
                    StringBuilder sb = new();
                    sb.Append(text[i]); // Start quote
                    i++;
                    while (true)
                    {
                        if (i == text.Length)
                        {
                            throw new ArgumentException("Unterminated string literal");
                        }
                        if (text[i] == '\\')
                        {
                            i++;
                            if (i == text.Length)
                            {
                                throw new ArgumentException("Unterminated string literal");
                            }

                            sb.Append(text[i] switch
                            {
                                '"' => '"',
                                '\\' => '\\',
                                'n' => '\n',
                                't' => '\t',
                                'r' => '\r',
                                _ => throw new ArgumentException($"Invalid escape sequence: \\{text[i]}")
                            });
                        }
                        else if (text[i] == '"')
                        {
                            sb.Append(text[i]);
                            i++;
                            yield return sb.ToString();
                            break;
                        }
                        else
                        {
                            sb.Append(text[i]);
                            i++;
                        }
                    }
                }
                else if (text[i] == '(' || text[i] == ')')
                {
                    yield return text[i++].ToString();
                }
                else
                {
                    // Read a token until parenthesis or whitespace
                    int startIndex = i;
                    for (; i < text.Length && text[i] != '(' && text[i] != ')' && !char.IsWhiteSpace(text[i]); i++) ;
                    yield return text.Substring(startIndex, i - startIndex).ToString();
                }
            }
        }

        // Column names could contain spaces and other characters that would make parsing ambiguous/troublesome so strip out everything except for letters and numbers
        // and have every column variable start with '@'.
        public static string CreateColumnVariableName(TraceSourceSchemaColumn column) => '@' + GetColumnNameForParsing(column);
        private static string GetColumnNameForParsing(TraceSourceSchemaColumn column) => Regex.Replace(column.Name, "[^\\w]", "");
    };
}
