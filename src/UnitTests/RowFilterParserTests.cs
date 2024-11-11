using InstantTraceViewer;
using Sprache;
using System.Linq.Expressions;

namespace UnitTests
{
    class MockTraceTableSnapshot : ITraceTableSnapshot
    {
        public static readonly TraceSourceSchemaColumn Column1 = new TraceSourceSchemaColumn { Name = "Column1" };
        public static readonly TraceSourceSchemaColumn Column2 = new TraceSourceSchemaColumn { Name = "Column2" };
        public static readonly TraceSourceSchemaColumn Column3 = new TraceSourceSchemaColumn { Name = "Column3" };

        public TraceTableSchema Schema => new TraceTableSchema { Columns = [Column1, Column2, Column3] };

        public bool GetColumnBooleanTest(int rowIndex, TraceSourceSchemaColumn column) => true;
        public string GetColumnString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false) => $"{column.Name}_{rowIndex}";
        public DateTime GetColumnDateTime(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
        public UnifiedLevel GetColumnUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
        public int GetColumnInt(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
        public int RowCount => 16;
        public int GenerationId => 1;
    }

    // https://stackoverflow.com/a/51738050
    // TODOs:
    // * Operators: contains, contains_cs, startswith, startswith_cs, endswith, endswith_cs, matches, matches_cs, matches_regex, matches_regex_cs
    // * Timestamp support. With less/greater than
    // * Int support. With less/greater than
    public class ConditionParser
    {
        private static readonly ParameterExpression Param1 = Expression.Parameter(typeof(ITraceTableSnapshot), "_");
        private readonly TraceTableSchema _schema;
        private Parser<TraceSourceSchemaColumn> _anyColumnVariable;

        public ConditionParser(TraceTableSchema schema)
        {
            _schema = schema;

            // Column variables are in the form '@<column name>' and are case-insensitive. This expression will match any of them.
            _anyColumnVariable =
                schema.Columns.Select(col => Parse.Char('@').Then(_ => Parse.IgnoreCase(col.Name).Return(col)))
                    .Aggregate((a, b) => a.Or(b));

        }

        public IResult<Expression<Func<ITraceTableSnapshot, bool>>> TryParseCondition(string text) => Lambda.TryParse(text);

        private Parser<Expression<Func<ITraceTableSnapshot, bool>>> Lambda => ExpressionTerm.End().Select(body => Expression.Lambda<Func<ITraceTableSnapshot, bool>>(body, Param1));

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

        private Parser<Expression> Factor => SubExpression.Or(StringEquals).Or(StringEqualsCaseInsensitive);

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
            select Expression.Constant(string.Concat(v.Select(v2 => UnescapeString(v2))));

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

        private Parser<Expression> StringEquals =>
            from left in ColumnVariable.Or(StringLiteral)
            from op in Parse.IgnoreCase("equals").Token()
            from right in ColumnVariable.Or(StringLiteral)
            select StringEqualsExpression(left, right, StringComparison.CurrentCultureIgnoreCase);

        private Parser<Expression> StringEqualsCaseInsensitive =>
            from left in ColumnVariable.Or(StringLiteral)
            from op in Parse.IgnoreCase("equals_cs").Token()
            from right in ColumnVariable.Or(StringLiteral)
            select StringEqualsExpression(left, right, StringComparison.CurrentCulture);

        private static Expression StringEqualsExpression(Expression left, Expression right, StringComparison comparison)
        {
            var method = typeof(string).GetMethod("Equals", new[] { typeof(string), typeof(string), typeof(StringComparison) });
            return Expression.Call(method, left, right, Expression.Constant(comparison));
        }

        private static Expression GetTableString(TraceSourceSchemaColumn column)
        {
            var method = typeof(ITraceTableSnapshot).GetMethod("GetColumnString"); // TODO nameof?
            return Expression.Call(
                Param1, // The instance that we call the method on is a parameter to this expression.
                method,
                Expression.Constant(0) /* rowIndex */,
                Expression.Constant(column),
                Expression.Constant(false) /* allowMultiline */);
        }
    }

    [TestClass]
    public class RowFilterParserTests
    {
        [TestMethod]
        public void Test()
        {
            MockTraceTableSnapshot mockTraceTableSnapshot = new();

            ConditionParser conditionParser = new(mockTraceTableSnapshot.Schema);

            List<(string, bool)> validConditionTests = new()
            {
                // Single subexpression
                ("@Column1 equals \"Column1_0\"", true),
                ("\"Column1_0\" equals @Column1", true),
                ("(@Column1 equals \"Column1_0\")", true),
                ("(\"Column1_0\" equals @Column1)", true),
                ("@Column1 equals \"foo\"", false),
                ("\"foo\" equals @Column1", false),

                // equals and equals_cs
                ("@Column1 equals \"Column1_0\"", true),
                ("@Column1 equals \"column1_0\"", true),
                ("@Column1 equals_cs \"Column1_0\"", true),
                ("@Column1 equals_cs \"column1_0\"", false),

                // TODO: escaping in string literals

                // Case-insensitive column name
                ("@column1 equals \"Column1_0\"", true),

                // Two subexpressions connected with 'and'
                ("@Column1 equals \"Column1_0\" and @Column2 equals \"Column2_0\"", true),
                ("@Column1 equals \"Column1_0\" AND @Column2 equals \"foo\"", false),
                ("@Column1 equals \"foo\"       and @Column2 equals \"Column2_0\"", false),
                ("@Column1 equals \"foo bar\"   and @Column2 equals \"Column2_0\"", false),

                // Two subexpressions connected with 'or'
                ("@Column1 equals \"Column1_0\" or @Column2 equals \"foo\"", true),
                ("@Column1 equals \"foo\"       OR @Column2 equals \"Column2_0\"", true),
                ("@Column1 equals \"foo\"       or @Column2 equals \"foo\"", false),
            };

            foreach (var (text, expected) in validConditionTests)
            {
                IResult<Expression<Func<ITraceTableSnapshot, bool>>> expressionResult = conditionParser.TryParseCondition(text);
                Assert.IsTrue(expressionResult.WasSuccessful, $"Condition {text} did not parse");
                Func<ITraceTableSnapshot, bool> compiledFunc = expressionResult.Value.Compile();
                Assert.AreEqual(expected, compiledFunc(mockTraceTableSnapshot), $"\nCondition: {text}\nExpression: {expressionResult.Value}");
            }

            List<string> invalidSyntaxTests = new()
            {
                "@",
                "notstringorcolumn",
                "@Column1 equals noquote",
                "noquote equals @Column1",
                "@NoSuchColumn equals \"Column1_0\"",
                "equals",
                "()",
                "(@Column1 equals \"Column1_0\"",
                "@Column1 equals \"Column1_0\")",
            };

            foreach (var text in invalidSyntaxTests)
            {
                var condition = conditionParser.TryParseCondition(text);
                Assert.IsFalse(condition.WasSuccessful, $"Expected parsing failure for {text}");
            }
        }
    }
}
