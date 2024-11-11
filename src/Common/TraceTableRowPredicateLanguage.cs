using Sprache;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace InstantTraceViewer
{
    public class TraceTableRowPredicateLanguage
    {
        public const string StringEqualsBinaryOperatorName = "equals";
        public const string StringEqualsCSBinaryOperatorName = "equals_cs";
        public const string StringContainsBinaryOperatorName = "contains";
        public const string StringContainsCSBinaryOperatorName = "contains_cs";
        public const string StringMatchesBinaryOperatorName = "matches";
        public const string StringMatchesCSBinaryOperatorName = "matches_cs";
        public const string StringMatchesRegexBinaryOperatorName = $"{StringMatchesBinaryOperatorName} regex";
        public const string StringMatchesRegexCSBinaryOperatorName = $"{StringMatchesCSBinaryOperatorName} regex";

        private static readonly ParameterExpression Param1TraceTableSnapshot = Expression.Parameter(typeof(ITraceTableSnapshot), "TraceTableSnapshot");
        private static readonly ParameterExpression Param2RowIndex = Expression.Parameter(typeof(int), "RowIndex");

        private readonly TraceTableSchema _schema;
        private Parser<TraceSourceSchemaColumn> _anyColumnVariable;

        public TraceTableRowPredicateLanguage(TraceTableSchema schema)
        {
            _schema = schema;

            // Column variables are in the form '@<column name>' and are case-insensitive. This expression will match any of them.
            _anyColumnVariable =
                schema.Columns.Select(col => Parse.Char('@').Then(_ => Parse.IgnoreCase(GetColumnNameForParsing(col)).Return(col)))
                    .Aggregate((a, b) => a.Or(b));

        }

        public static string CreateColumnVariableName(TraceSourceSchemaColumn column) => '@' + GetColumnNameForParsing(column);

        public static string CreateEscapedStringLiteral(string text)
        {
            return '"' + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\"", "\\\"") + '"';
        }

        public IResult<Expression<Func<ITraceTableSnapshot, int, bool>>> TryParse(string text) => Lambda.TryParse(text);

        private Parser<Expression<Func<ITraceTableSnapshot, int, bool>>> Lambda =>
            ExpressionTerm.End().Select(body => Expression.Lambda<Func<ITraceTableSnapshot, int, bool>>(body, Param1TraceTableSnapshot, Param2RowIndex));

        // lowest priority first
        private Parser<Expression> ExpressionTerm => OrTerm;
        private Parser<Expression> OrTerm => Parse.ChainOperator(OpOr, AndTerm, Expression.MakeBinary);
        private Parser<Expression> AndTerm => Parse.ChainOperator(OpAnd, NegateTerm, Expression.MakeBinary);
        private Parser<ExpressionType> OpOr = Parse.IgnoreCase("or").Token().Return(ExpressionType.OrElse);
        private Parser<ExpressionType> OpAnd = Parse.IgnoreCase("and").Token().Return(ExpressionType.AndAlso);

        private Parser<Expression> NegateTerm => NegatedFactor.Or(Factor);

        private Parser<Expression> NegatedFactor =>
            from negate in Parse.IgnoreCase("not").Token()
            from expr in Factor
            select Expression.Not(expr);

        private Parser<Expression> Factor => SubExpression
            .Or(StringEquals).Or(StringEqualsCS)
            .Or(StringContains).Or(StringContainsCS)
            .Or(StringMatches).Or(StringMatchesCS)
            .Or(StringMatchesRegex).Or(StringMatchesRegexCS);

        private Parser<Expression> SubExpression =>
            from lparen in Parse.Char('(').Token()
            from expr in ExpressionTerm
            from rparen in Parse.Char(')').Token()
            select expr;

        private Parser<Expression> ColumnVariable => _anyColumnVariable.Select(GetTableString);

        private Parser<Expression> StringLiteral =>
            from start in Parse.Char('"')
            from v in
                    Parse.String("\\\"")
                .Or(Parse.String("\\\\"))
                .Or(Parse.String("\\r"))
                .Or(Parse.String("\\n"))
                .Or(Parse.String("\\t"))
                .Or(Parse.AnyChar.Except(Parse.String("\\")).Except(Parse.Char('"')).Many()).Many()
            from end in Parse.Char('"')
            select Expression.Constant(string.Concat(v.Select(UnescapeString)));

        private static string UnescapeString(IEnumerable<char> charSequence)
        {
            string str = new string(charSequence.ToArray());
            return str == "\\\"" ? "\"" :
                   str == "\\\\" ? "\\" :
                   str == "\\r" ? "\r" :
                   str == "\\n" ? "\n" :
                   str == "\\t" ? "\t" :
                   str;
        }

        private Parser<Expression> StringContains =>
            from left in ColumnVariable
            from op in Parse.IgnoreCase(StringContainsBinaryOperatorName).Token()
            from right in StringLiteral
            select StringContainsExpression(left, right, StringComparison.CurrentCultureIgnoreCase);
        private Parser<Expression> StringContainsCS =>
            from left in ColumnVariable
            from op in Parse.IgnoreCase(StringContainsCSBinaryOperatorName).Token()
            from right in StringLiteral
            select StringContainsExpression(left, right, StringComparison.CurrentCulture);

        private Parser<Expression> StringEquals =>
            from left in ColumnVariable
            from op in Parse.IgnoreCase(StringEqualsBinaryOperatorName).Token()
            from right in StringLiteral
            select StringEqualsExpression(left, right, StringComparison.CurrentCultureIgnoreCase);
        private Parser<Expression> StringEqualsCS =>
            from left in ColumnVariable
            from op in Parse.IgnoreCase(StringEqualsCSBinaryOperatorName).Token()
            from right in StringLiteral
            select StringEqualsExpression(left, right, StringComparison.CurrentCulture);

        private Parser<Expression> StringMatchesRegex =>
            from left in ColumnVariable
            from op1 in Parse.IgnoreCase(StringMatchesBinaryOperatorName).Token()
            from op2 in Parse.IgnoreCase("regex").Token()
            from right in StringLiteral
            select RegexMatchesExpression(left, right, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private Parser<Expression> StringMatchesRegexCS =>
            from left in ColumnVariable
            from op1 in Parse.IgnoreCase(StringMatchesCSBinaryOperatorName).Token()
            from op2 in Parse.IgnoreCase("regex").Token()
            from right in StringLiteral
            select RegexMatchesExpression(left, right, RegexOptions.Compiled);

        private Parser<Expression> StringMatches =>
            from left in ColumnVariable
            from op1 in Parse.IgnoreCase(StringMatchesBinaryOperatorName).Token()
            from right in StringLiteral
            select MatchesExpression(left, right, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private Parser<Expression> StringMatchesCS =>
            from left in ColumnVariable
            from op1 in Parse.IgnoreCase(StringMatchesCSBinaryOperatorName).Token()
            from right in StringLiteral
            select MatchesExpression(left, right, RegexOptions.Compiled);

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

        // Null is not expected but just in case the expression uses our own wrapper to protect against it.
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

        private static Expression RegexMatchesExpression(Expression left, Expression right, RegexOptions options)
        {
            // Convert the string literal to a regex at parse time so it can efficiently be reused for each row.
            string matchPattern = (string)((ConstantExpression)right).Value!;
            var method = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string)])!;
            return Expression.Call(
                Expression.Constant(new Regex(matchPattern, options)),
                method,
                left);
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

        // Column names could contain spaces and other troublesome characters so strip out everything except for letters and numbers.
        private static string GetColumnNameForParsing(TraceSourceSchemaColumn column) => Regex.Replace(column.Name, "[^\\w]", "");
    }
}
