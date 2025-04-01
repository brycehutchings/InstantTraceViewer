using InstantTraceViewer;

namespace InstantTraceViewerTests
{
    [TestClass]
    public class TraceTableRowSelectorSyntaxTests
    {
        class MockTraceTableSnapshot : ITraceTableSnapshot
        {
            public static readonly TraceSourceSchemaColumn Column1 = new TraceSourceSchemaColumn { Name = "Column1" };
            public static readonly TraceSourceSchemaColumn Column2 = new TraceSourceSchemaColumn { Name = "Column2" };
            public static readonly TraceSourceSchemaColumn Column3 = new TraceSourceSchemaColumn { Name = "Column3" };

            public TraceTableSchema Schema { get; set; } = new TraceTableSchema { Columns = [Column1, Column2, Column3] };

            public bool GetColumnBooleanTest(int rowIndex, TraceSourceSchemaColumn column) => true;
            public string GetColumnValueString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false) => $"{column.Name}_{rowIndex}";
            public DateTime GetColumnValueDateTime(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
            public UnifiedLevel GetColumnValueUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
            public int GetColumnValueInt(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
            public string GetColumnValueNameForId(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();
            public UnifiedOpcode GetColumnValueUnifiedOpcode(int rowIndex, TraceSourceSchemaColumn column) => throw new NotImplementedException();

            public int RowCount => 16;
            public int GenerationId => 1;
        }

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
                Assert.AreEqual(testString.Literal, TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(testString.Text));
            }
        }

        [TestMethod]
        public void TestTokenizer()
        {
            var testCases = new List<(string Text, string[] Tokens)>
            {
                ("", [ SyntaxTokenizer.EofText ]),
                (" ", [ SyntaxTokenizer.EofText ]),
                ("\n", [ SyntaxTokenizer.EofText ]),
                ("\t", [ SyntaxTokenizer.EofText ]),

                ("a", [ "a", SyntaxTokenizer.EofText ]),
                ("a b", [ "a", "b", SyntaxTokenizer.EofText ]),
                ("a\tb", [ "a", "b", SyntaxTokenizer.EofText ]),

                ("((a))", [ "(", "(", "a", ")", ")", SyntaxTokenizer.EofText ]),
                ("[[a]]", [ "[", "[", "a", "]", "]", SyntaxTokenizer.EofText ]),
                ("([a])", [ "(", "[", "a", "]", ")", SyntaxTokenizer.EofText ]),
                ("[(a)]", [ "[", "(", "a", ")", "]", SyntaxTokenizer.EofText ]),


                ("a<1", [ "a", "<", "1", SyntaxTokenizer.EofText ]),
                ("a<=1", [ "a", "<=", "1", SyntaxTokenizer.EofText ]),
                ("a==1", [ "a", "==", "1", SyntaxTokenizer.EofText ]),
                ("a<1", [ "a", "<", "1", SyntaxTokenizer.EofText ]),

                ("a <1", [ "a", "<", "1", SyntaxTokenizer.EofText ]),
                ("a<= 1", [ "a", "<=", "1", SyntaxTokenizer.EofText ]),
                ("a == 1", [ "a", "==", "1", SyntaxTokenizer.EofText ]),
                ("a  <  1", [ "a", "<", "1", SyntaxTokenizer.EofText ]),

                ("a in [\"a\",b,]", [ "a", "in", "[", "\"a\"", ",", "b", ",", "]", SyntaxTokenizer.EofText ]),
            };

            foreach (var testCase in testCases)
            {
                var actualTokens = SyntaxTokenizer.Tokenize(testCase.Text).ToArray();
                var actualTokenStrs = actualTokens.Select(t => t.Text).ToArray();

                CollectionAssert.AreEqual(testCase.Tokens, actualTokenStrs);
            }
        }

        [TestMethod]
        public void TestParseValidSyntax()
        {
            MockTraceTableSnapshot mockTraceTableSnapshot = new();

            TraceTableRowSelectorSyntax conditionParser = new(mockTraceTableSnapshot.Schema);

            List<(string, bool)> validConditionTests = new()
            {
                // ==/=~
                //("   @Column1  ==  \"Column1_0\"  ", true),
                //("@Column1 == \"column1_0\"", false),
                ("@Column1 =~ \"column1_0\"", true),

                // !=/!~
                ("@Column1 != \"column1_0\"", true),
                ("@Column1 !~ \"column1_0\"", false),

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
                ("@Column1 == \"Column1_0\"", true),

                ("(@Column1 == \"Column1_0\")", true),
                ("@Column1 == \"foo\"", false),

                // 'not' operator
                ("not @Column1 == \"Column1_0\"", false),
                ("not not @Column1 == \"Column1_0\"", true),
                ("not not not @Column1 == \"Column1_0\"", false),
                ("not @Column1 == \"foo\"", true),
                ("not (@Column1 == \"Column1_0\")", false),
                ("not (@Column1 == \"foo\")", true),

                ("not  @Column1 == \"Column1_0\" or @Column2 == \"Column2_0\"", true),
                ("not (@Column1 == \"Column1_0\" or @Column2 == \"Column2_0\")", false),
                ("not(@Column1 == \"Column1_0\" or @Column2 == \"Column2_0\")", false),

                // Case-insensitive column name
                ("@column1 == \"Column1_0\"", true),

                // Two subexpressions connected with 'and'
                ("@Column1 == \"Column1_0\" and @Column2 == \"Column2_0\"", true),
                ("@Column1 == \"Column1_0\" AND @Column2 == \"foo\"", false),
                ("@Column1 == \"foo\"       and @Column2 == \"Column2_0\"", false),
                ("@Column1 == \"foo bar\"   and @Column2 == \"Column2_0\"", false),

                // Two subexpressions connected with 'or'
                ("@Column1 == \"Column1_0\" or @Column2 == \"foo\"", true),
                ("@Column1 == \"foo\"       OR @Column2 == \"Column2_0\"", true),
                ("@Column1 == \"foo\"       or @Column2 == \"foo\"", false),

                // And/or applied left to right
                ("@Column1 == \"Column1_0\" and @Column2 == \"Column2_0\" or @Column2 == \"foo\"", true), // true && true || false == true
                ("@Column1 == \"Column1_0\" and @Column2 == \"foo\" or @Column2 == \"Column2_0\"", true), // true && false || true == true
                ("@Column1 == \"foo\" and @Column2 == \"Column2_0\" or @Column2 == \"foo\"", false), // false && true || false == false
                ("@Column1 == \"foo\" and @Column2 == \"bar\" or @Column2 == \"Column2_0\"", true), // false && false || true == true

                // Or takes precedence over And
                // Effectively this test is doing "true or true and false" which should be parsed as "(true) or (true and false)" and not "(true or true) and (false)"
                ("@Column1 == \"Column1_0\" or @Column2 == \"Column2_0\" and @Column2 == \"foo\"", true),

                // TODO: escaping in string literals
            };

            foreach (var (text, expectMatch) in validConditionTests)
            {
                Console.WriteLine($"Condition: {text}");

                TraceTableRowSelectorParseResults parseResult = conditionParser.Parse(text);

                Console.WriteLine($"Expression: {parseResult.Expression}\n");

                TraceTableRowSelector compiledFunc = parseResult.Expression.Compile();
                Assert.AreEqual(expectMatch, compiledFunc(mockTraceTableSnapshot, 0 /* rowIndex */), $"\nCondition: {text}\nExpression: {parseResult.Expression}");
            }
        }

        [TestMethod]
        public void TestParseInvalidSyntax()
        {
            MockTraceTableSnapshot mockTraceTableSnapshot = new();

            TraceTableRowSelectorSyntax conditionParser = new(mockTraceTableSnapshot.Schema);

            List<string> invalidSyntaxTests = [
                "(@Column1 == \"Column1_0\"",
                "@Column1 == \"Column1_0\" and@Column2 == \"Column2_t\"",
                "@",
                "notstringorcolumn",
                "@Column1equals\"nospaces\"",
                "@Column1 == noquote",
                "@Column1 not == \"foo\"",
                "noquote == @Column1",
                "@NoSuchColumn == \"Column1_0\"",
                "==",
                "not",
                "()",
                "() and (@Column1 == \"Column1_0\")",
                "not()",
                "(@Column1 == \"Column1_0\"",
                "@Column1 == \"Column1_0\")",
                "\"Column1_0\" == @Column1",
                "(\"Column1_0\" == @Column1)",
                "\"foo\" == @Column1",
                "\"a\" == \"A\"",
                "not \"foo\" == @Column1",
                "not (\"foo\" == @Column1)",
                "@Column1 == @Column2",
                "not@Column1 == \"Column1_0\"", // must be space or ( after not.
            ];

            foreach (var text in invalidSyntaxTests)
            {
                Console.WriteLine($"Condition: {text}");
                var condition = conditionParser.Parse(text);
                if (condition.Expression != null)
                {
                    Assert.Fail($"Condition {text} should not have parsed\n{condition}");
                }
                else
                {
                    Console.WriteLine($"Expected: {string.Join(", ", condition.ExpectedTokens)}\n");
                }
            }
        }
    }
}
