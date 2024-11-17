using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace InstantTraceViewer
{
    public class ParserIdea
    {
        public class Result
        {
            public IReadOnlyList<string> Tokens;
            public int Index;
            public Expression<TraceTableRowPredicate> Expression;

            public string CurrentToken
            {
                get
                {
                    if (Index >= Tokens.Count)
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

        private static readonly ParameterExpression Param1TraceTableSnapshot = Expression.Parameter(typeof(ITraceTableSnapshot), "TraceTableSnapshot");
        private static readonly ParameterExpression Param2RowIndex = Expression.Parameter(typeof(int), "RowIndex");
        private readonly IReadOnlyDictionary<string, TraceSourceSchemaColumn> _columns;

        public ParserIdea(TraceTableSchema schema)
        {
            // Column variables are in the form '@<column name>' and are case-insensitive.
            _columns = schema.Columns.ToDictionary(c => CreateColumnVariableName(c), c => c, StringComparer.CurrentCultureIgnoreCase);
        }

        public Result Parse(string text)
        {
            // Split the text up by whitespace or punctuation
            string[] tokens = Tokenize(text).ToArray();

            Result result = new() { Index = 0, Tokens = tokens };
            Expression expressionBody = ParseExpression(result, true);
            result.Expression = Expression.Lambda<TraceTableRowPredicate>(expressionBody, Param1TraceTableSnapshot, Param2RowIndex);
            return result;
        }

        private Expression ParseExpression(Result result, bool allowChaining, bool stopAtCloseParenthesis = false)
        {
            Expression leftExpression = null;

            do
            {
                if (result.Eof)
                {
                    Trace.Assert(leftExpression != null); // TODO: Need a check here?
                    break;
                }
                else if (string.Equals(result.CurrentToken, "(", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    leftExpression = ParseExpression(result, true, true);
                }
                else if (stopAtCloseParenthesis && leftExpression != null && string.Equals(result.CurrentToken, ")", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    return leftExpression;
                }
                else if (string.Equals(result.CurrentToken, "not", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    leftExpression = Expression.Not(ParseExpression(result, false, false));
                }
                else if (allowChaining && leftExpression != null && string.Equals(result.CurrentToken, "and", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    leftExpression = Expression.AndAlso(leftExpression, ParseExpression(result, false, stopAtCloseParenthesis));
                }
                else if (allowChaining && leftExpression != null && string.Equals(result.CurrentToken, "or", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    leftExpression = Expression.OrElse(leftExpression, ParseExpression(result, true, stopAtCloseParenthesis));
                }
                else if (leftExpression == null)
                {
                    leftExpression = ParseComparison(result);
                }
                else
                {
                    throw new ArgumentException($"Unexpected token: {result.CurrentToken}");
                }
            }
            while (allowChaining);

            return leftExpression;
        }

        private Expression ParseComparison(Result result)
        {
            if (!_columns.TryGetValue(result.CurrentToken, out TraceSourceSchemaColumn column))
            {
                throw new ArgumentException($"Unknown column: {result.CurrentToken}");
            }

            result.MoveNextToken();

            string operatorName = result.CurrentToken;
            if (operatorName.Equals("equals", StringComparison.InvariantCultureIgnoreCase) ||
                operatorName.Equals("equals_cs", StringComparison.InvariantCultureIgnoreCase))
            {
                result.MoveNextToken();
                string value = ReadStringLiteral(result.CurrentToken);
                result.MoveNextToken();
                return StringEqualsExpression(GetTableString(column), Expression.Constant(value, typeof(string)), GetStringComparisonType(operatorName));
            }
            else if (operatorName.Equals("contains", StringComparison.InvariantCultureIgnoreCase) ||
                     operatorName.Equals("contains_cs", StringComparison.InvariantCultureIgnoreCase))
            {
                result.MoveNextToken();
                string value = ReadStringLiteral(result.CurrentToken);
                result.MoveNextToken();
                return StringContainsExpression(GetTableString(column), Expression.Constant(value, typeof(string)), GetStringComparisonType(operatorName));
            }
            else if (operatorName.Equals("matches", StringComparison.InvariantCultureIgnoreCase) ||
                     operatorName.Equals("matches_cs", StringComparison.InvariantCultureIgnoreCase))
            {
                result.MoveNextToken();

                // Token following matches/matches_cs may be an optional "regex" modifier.
                string token = result.CurrentToken;
                if (result.CurrentToken.Equals("regex", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.MoveNextToken();
                    string value = ReadStringLiteral(result.CurrentToken);
                    result.MoveNextToken();
                    return MatchesRegexExpression(GetTableString(column), Expression.Constant(value, typeof(string)), GetRegexOptions(operatorName));
                }
                else
                {
                    string value = ReadStringLiteral(result.CurrentToken);
                    result.MoveNextToken();
                    return MatchesExpression(GetTableString(column), Expression.Constant(value, typeof(string)), GetRegexOptions(operatorName));
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

        private static Expression StringEqualsExpression(Expression left, Expression right, StringComparison comparison)
        {
            var method = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string), typeof(StringComparison)])!;
            return Expression.Call(method, left, right, Expression.Constant(comparison));
        }

        private static Expression StringContainsExpression(Expression left, Expression right, StringComparison comparison)
        {
            var method = typeof(TraceTableRowPredicateLanguage).GetMethod(
                nameof(StringContainsImpl),
                BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(string), typeof(string), typeof(StringComparison)])!;
            return Expression.Call(method, left, right, Expression.Constant(comparison));
        }

        // Null is not expected but just in case the Expression uses our own wrapper to protect against it.
        private static bool StringContainsImpl(string left, string right, StringComparison comparison) => left?.Contains(right, comparison) ?? (right == null);

        private static Expression MatchesExpression(Expression left, Expression right, RegexOptions options)
        {
            // Convert * and ? to regex pattern.
            string matchPattern = (string)((ConstantExpression)right).Value!;
            matchPattern = "^" + Regex.Escape(matchPattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            var method = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string)])!;
            return Expression.Call(
                Expression.Constant(new Regex(matchPattern, options)),
                method,
                left);
        }

        private static Expression MatchesRegexExpression(Expression left, Expression right, RegexOptions options)
        {
            // Convert the string literal to a regex at parse time so it can efficiently be reused for each row.
            string matchPattern = (string)((ConstantExpression)right).Value!;
            var method = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string)])!;
            return Expression.Call(
                Expression.Constant(new Regex(matchPattern, options)),
                method,
                left);
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
