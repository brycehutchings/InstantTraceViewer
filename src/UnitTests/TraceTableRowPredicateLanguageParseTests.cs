using InstantTraceViewer;
using Sprache;
using System.Linq.Expressions;

namespace InstantTraceViewerTests
{
    class MockTraceTableSnapshot : ITraceTableSnapshot
    {
        public static readonly TraceSourceSchemaColumn Column1 = new TraceSourceSchemaColumn { Name = "Column1" };
        public static readonly TraceSourceSchemaColumn Column2 = new TraceSourceSchemaColumn { Name = "Column2" };
        public static readonly TraceSourceSchemaColumn Column3 = new TraceSourceSchemaColumn { Name = "Column3" };

        public TraceTableSchema Schema { get; set; } = new TraceTableSchema { Columns = [Column1, Column2, Column3] };

        public bool GetColumnBooleanTest(int rowIndex, TraceSourceSchemaColumn column) => true;
        public string GetColumnString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false) => $"{column.Name}_{rowIndex}";
        public DateTime GetColumnDateTime(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
        public UnifiedLevel GetColumnUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
        public int GetColumnInt(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
        public int RowCount => 16;
        public int GenerationId => 1;
    }

    [TestClass]
    public class TraceTableRowPredicateLanguageParseTests
    {
        [TestMethod]
        public void TestEscape()
        {
            List<(string Text, string Literal)> testStrings = [
                ("", "\"\""),
                ("foo", "\"foo\""),
                ("foo bar", "\"foo bar\""),
                ("\"foo\"", "\"\\\"foo\\\"\""),
                ("\\", "\"\\\\\""),
                ("\n", "\"\\n\""),
                ("\t", "\"\\t\""),
                ("\r", "\"\\r\""),
            ];

            foreach (var testString in testStrings)
            {
                Assert.AreEqual(testString.Literal, TraceTableRowPredicateLanguage.CreateEscapedStringLiteral(testString.Text));
            }
        }

        [TestMethod]
        public void TestColumnNames()
        {
            // Test column names with non-word characters. And duplicated column names (after fixup).
            var schema = new TraceTableSchema
            {
                Columns = [
                    new TraceSourceSchemaColumn { Name = "Column1" },
                    new TraceSourceSchemaColumn { Name = "Column 1" },
                    new TraceSourceSchemaColumn { Name = "@[ Column 2 *] " }
                ]
            };

            MockTraceTableSnapshot mockTraceTableSnapshot = new() { Schema = schema };

            TraceTableRowPredicateLanguage conditionParser = new(mockTraceTableSnapshot.Schema);

            {
                // When two columns end up with the same variable name, the first one wins (for no particular reason really, so this behavior can change if needed).
                IResult<Expression<Func<ITraceTableSnapshot, int, bool>>> result = conditionParser.TryParse("@Column1 equals \"Column1_0\"");
                Assert.IsTrue(result.WasSuccessful);
                Func<ITraceTableSnapshot, int, bool> compiledFunc = result.Value.Compile();
                Assert.IsTrue(compiledFunc(mockTraceTableSnapshot, 0 /* rowIndex */));
            }

            {
                IResult<Expression<Func<ITraceTableSnapshot, int, bool>>> result = conditionParser.TryParse("@Column2 equals \"@[ Column 2 *] _0\"");
                Assert.IsTrue(result.WasSuccessful);
                Func<ITraceTableSnapshot, int, bool> compiledFunc = result.Value.Compile();
                Assert.IsTrue(compiledFunc(mockTraceTableSnapshot, 0 /* rowIndex */));
            }
        }

        [TestMethod]
        public void TestParse()
        {
            MockTraceTableSnapshot mockTraceTableSnapshot = new();

            TraceTableRowPredicateLanguage conditionParser = new(mockTraceTableSnapshot.Schema);

            List<(string, bool)> validConditionTests = new()
            {
                // equals/equals_cs
                ("@Column1 equals \"Column1_0\"", true),
                ("@Column1 equals \"column1_0\"", true),
                ("@Column1 equals_cs \"Column1_0\"", true),
                ("@Column1 equals_cs \"column1_0\"", false),

                // contains/contains_cs
                ("@Column1 contains \"OLUM\"", true),
                ("@Column1 contains \"foo\"", false),
                ("@Column1 contains_cs \"OLUM\"", false),
                ("@Column1 contains_cs \"olum\"", true),

                // matches/matches_cs
                ("@Column1 matches \"Col*1_?\"", true),
                ("@Column1 matches \"col*1_?\"", true),
                ("@Column1 matches \"col*1_1\"", false),
                ("@Column1 matches_cs \"Col*1_?\"", true),
                ("@Column1 matches_cs \"col*1_?\"", false),

                // matches regex/matches_cs regex
                ("@Column1 matches regex \"Col.+1_0\"", true),
                ("@Column1 matches regex \"col.+1_0\"", true),
                ("@Column1 matches regex \"col.+1_1\"", false),
                ("@Column1 matches_cs regex \"Col.+1_0\"", true),
                ("@Column1 matches_cs regex \"col.+1_0\"", false),

                // Single subexpression either order
                ("@Column1 equals \"Column1_0\"", true),
                ("(@Column1 equals \"Column1_0\")", true),
                ("@Column1 equals \"foo\"", false),

                // 'not' operator
                ("not @Column1 equals \"Column1_0\"", false),
                ("not @Column1 equals \"foo\"", true),
                ("not (@Column1 equals \"Column1_0\")", false),
                ("not (@Column1 equals \"foo\")", true),
                ("not  @Column1 equals \"Column1_0\" or @Column2 equals \"Column2_0\"", true),
                ("not (@Column1 equals \"Column1_0\" or @Column2 equals \"Column2_0\")", false),

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

                // And/or applied left to right
                ("@Column1 equals \"Column1_0\" and @Column2 equals \"Column2_0\" or @Column2 equals \"foo\"", true), // true && true || false == true
                ("@Column1 equals \"Column1_0\" and @Column2 equals \"foo\" or @Column2 equals \"Column2_0\"", true), // true && false || true == true
                ("@Column1 equals \"foo\" and @Column2 equals \"Column2_0\" or @Column2 equals \"foo\"", false), // false && true || false == false
                ("@Column1 equals \"foo\" and @Column2 equals \"bar\" or @Column2 equals \"Column2_0\"", true), // false && false || true == true

                // TODO: escaping in string literals
            };

            foreach (var (text, expected) in validConditionTests)
            {
                IResult<Expression<Func<ITraceTableSnapshot, int, bool>>> expressionResult = conditionParser.TryParse(text);
                Assert.IsTrue(expressionResult.WasSuccessful, $"Condition {text} did not parse");
                Func<ITraceTableSnapshot, int, bool> compiledFunc = expressionResult.Value.Compile();
                Assert.AreEqual(expected, compiledFunc(mockTraceTableSnapshot, 0 /* rowIndex */), $"\nCondition: {text}\nExpression: {expressionResult.Value}");
            }

            List<string> invalidSyntaxTests = [
                "@",
                "notstringorcolumn",
                "@Column1 equals noquote",
                "@Column1 not equals \"foo\"",
                "noquote equals @Column1",
                "@NoSuchColumn equals \"Column1_0\"",
                "equals",
                "not",
                "()",
                "(@Column1 equals \"Column1_0\"",
                "@Column1 equals \"Column1_0\")",
                "\"Column1_0\" equals @Column1",
                "(\"Column1_0\" equals @Column1)",
                "\"foo\" equals @Column1",
                "\"a\" equals \"A\"",
                "not \"foo\" equals @Column1",
                "not (\"foo\" equals @Column1)",
                "@Column1 equals @Column2",
            ];

            foreach (var text in invalidSyntaxTests)
            {
                var condition = conditionParser.TryParse(text);
                Assert.IsFalse(condition.WasSuccessful, $"Expected parsing failure for {text}");
            }
        }
    }
}
