using InstantTraceViewer;
using Sprache;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

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
    public class ConditionParser
    {
        private static readonly ParameterExpression Param1 = Expression.Parameter(typeof(ITraceTableSnapshot), "_");
        private readonly TraceTableSchema _schema;
        private Parser<TraceSourceSchemaColumn> _anyColumnVariable;

        public ConditionParser(TraceTableSchema schema)
        {
            _schema = schema;
            foreach (TraceSourceSchemaColumn col in schema.Columns)
            {
                if (_anyColumnVariable == null)
                {
                    _anyColumnVariable = Parse.Char('@').Then(_ => Parse.IgnoreCase(col.Name).Return(col));
                }
                else
                {
                    _anyColumnVariable = _anyColumnVariable.Or(Parse.Char('@').Then(_ => Parse.IgnoreCase(col.Name).Return(col)));
                }
            }
        }

        public IResult<Expression<Func<ITraceTableSnapshot, bool>>> TryParseCondition(string text) => Lambda.TryParse(text);

        Parser<Expression<Func<ITraceTableSnapshot, bool>>> Lambda => ExpressionTerm.End().Select(body => Expression.Lambda<Func<ITraceTableSnapshot, bool>>(body, Param1));

        // lowest priority first
        Parser<Expression> ExpressionTerm => OrTerm;
        Parser<Expression> OrTerm => Parse.ChainOperator(OpOr, AndTerm, Expression.MakeBinary);
        Parser<Expression> AndTerm => Parse.ChainOperator(OpAnd, NegateTerm, Expression.MakeBinary);
        Parser<ExpressionType> OpOr = Parse.IgnoreCase("or").Token().Return(ExpressionType.OrElse);
        Parser<ExpressionType> OpAnd = Parse.IgnoreCase("and").Token().Return(ExpressionType.AndAlso);

        Parser<Expression> NegateTerm => NegatedFactor.Or(Factor);

        Parser<Expression> NegatedFactor =>
            from negate in Parse.IgnoreCase("not").Token()
            from expr in Factor
            select Expression.Not(expr);

        Parser<Expression> Factor => SubExpression.Or(StringEquals);

        Parser<Expression> SubExpression =>
            from lparen in Parse.Char('(').Token()
            from expr in ExpressionTerm
            from rparen in Parse.Char(')').Token()
            select expr;

        Parser<Expression> ColumnVariable => _anyColumnVariable.Select(GetTableString);

        Parser < Expression > StringLiteral =>
            (from open in Parse.Char('"')
             from content in Parse.CharExcept('"').Many().Text()
             from close in Parse.Char('"')
             select Expression.Constant(content)).Token();

        Parser<Expression> StringEquals =>
            from left in ColumnVariable.Or(StringLiteral)
            from op in Parse.IgnoreCase("equals").Token()
            from right in ColumnVariable.Or(StringLiteral)
            select Expression.Equal(left, right);

        Expression GetTableString(TraceSourceSchemaColumn column)
        {
            //var column = _schema.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
            //if (column == null)
            //{
            //    throw new ParseException($"Column '{name}' not found in schema.");
            //}

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

            //var condition1 = ConditionParser.Parse("true and false or true");
            //Console.WriteLine(condition1.ToString());
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

                // Case-insensitive column name
                ("@column1 equals \"Column1_0\"", true),

                // Two subexpressions with AND
                ("@Column1 equals \"Column1_0\" and @Column2 equals \"Column2_0\"", true),
                ("@Column1 equals \"Column1_0\" and @Column2 equals \"foo\"", false),
                ("@Column1 equals \"foo\"       and @Column2 equals \"Column2_0\"", false),
                ("@Column1 equals \"foo bar\"   and @Column2 equals \"Column2_0\"", false),

                // Two subexpressions with OR
                ("@Column1 equals \"Column1_0\" or @Column2 equals \"foo\"", true),
                ("@Column1 equals \"foo\" or @Column2 equals \"Column2_0\"", true),
                ("@Column1 equals \"foo\" or @Column2 equals \"foo\"", false),
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
