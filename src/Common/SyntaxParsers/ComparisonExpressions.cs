using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace InstantTraceViewer
{
    internal static class ComparisonExpressions
    {
        public static Expression StringEquals(Expression left, Expression right, StringComparison comparison)
        {
            var method = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string), typeof(StringComparison)])!;
            return Expression.Call(method, left, right, Expression.Constant(comparison));
        }

        public static Expression StringContains(Expression left, Expression right, StringComparison comparison)
        {
            var method = typeof(ComparisonExpressions).GetMethod(
                nameof(StringContainsImpl),
                BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(string), typeof(string), typeof(StringComparison)])!;
            return Expression.Call(method, left, right, Expression.Constant(comparison));
        }

        public static Expression Matches(Expression left, Expression right, RegexOptions options)
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

        public static Expression MatchesRegex(Expression left, Expression right, RegexOptions options)
        {
            // Convert the string literal to a regex at parse time so it can efficiently be reused for each row.
            string matchPattern = (string)((ConstantExpression)right).Value!;
            var method = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string)])!;
            return Expression.Call(
                Expression.Constant(new Regex(matchPattern, options)),
                method,
                left);
        }

        // Null is not expected but just in case the expression uses our own wrapper to protect against it.
        private static bool StringContainsImpl(string left, string right, StringComparison comparison) => left?.Contains(right, comparison) ?? (right == null);
    }
}
