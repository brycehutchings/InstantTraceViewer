using InstantTraceViewer;
using System.ComponentModel.DataAnnotations;

namespace InstantTraceViewerTests
{
    [TestClass]
    public class CsvParserTests
    {
        // Must not read in background so that all data is available for testing immediately.
        private CsvTableSource ReadTestString(string csvContent, bool firstRowIsHeader)
            => new CsvTableSource("test", new StringReader(csvContent), firstRowIsHeader, readInBackground: false);

        [TestMethod]
        public void Empty()
        {
            ITraceTableSnapshot emptyHeader = ReadTestString("", true).CreateSnapshot();
            Assert.AreEqual(1, emptyHeader.Schema.Columns.Count); // 0 columns is not valid for the viewer's sake.
            Assert.AreEqual(0, emptyHeader.RowCount);

            ITraceTableSnapshot emptyNoHeader = ReadTestString("", false).CreateSnapshot();
            Assert.AreEqual(1, emptyNoHeader.Schema.Columns.Count); // 0 columns is not valid for the viewer's sake.
            Assert.AreEqual(1, emptyNoHeader.RowCount);
        }

        [TestMethod]
        public void Space()
        {
            ITraceTableSnapshot emptyHeader = ReadTestString(" ", true).CreateSnapshot();
            Assert.AreEqual(1, emptyHeader.Schema.Columns.Count);
            Assert.AreEqual(" ", emptyHeader.Schema.Columns[0].Name);
            Assert.AreEqual(0, emptyHeader.RowCount);

            ITraceTableSnapshot emptyNoHeader = ReadTestString(" ", false).CreateSnapshot();
            Assert.AreEqual(1, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(1, emptyNoHeader.RowCount);
        }

        [TestMethod]
        public void SingleLineNoQuotes()
        {
            ITraceTableSnapshot emptyHeader = ReadTestString("a,b,c", true).CreateSnapshot();
            Assert.AreEqual(3, emptyHeader.Schema.Columns.Count);
            CollectionAssert.AreEqual
                (new[] { "a", "b", "c" },
                emptyHeader.Schema.Columns.Select(c => c.Name).ToArray());
            Assert.AreEqual(0, emptyHeader.RowCount);

            ITraceTableSnapshot emptyNoHeader = ReadTestString("a,b,c", false).CreateSnapshot();
            Assert.AreEqual(3, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(1, emptyNoHeader.RowCount);
            Assert.AreEqual("a", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("b", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("c", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[2]));
        }

        [TestMethod]
        public void SingleWhitespacePadding()
        {
            ITraceTableSnapshot emptyNoHeader = ReadTestString(" a ,\tb\t, c ", false).CreateSnapshot();
            Assert.AreEqual(3, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(1, emptyNoHeader.RowCount);
            Assert.AreEqual(" a ", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("\tb\t", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual(" c ", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[2]));
        }

        [TestMethod]
        public void TestNewLines()
        {
            ITraceTableSnapshot emptyNoHeader = ReadTestString("a,b\rc,d\ne,f\r\ng,h", false).CreateSnapshot();
            Assert.AreEqual(2, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(4, emptyNoHeader.RowCount);
            Assert.AreEqual("a", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("b", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("c", emptyNoHeader.GetColumnString(1, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("d", emptyNoHeader.GetColumnString(1, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("e", emptyNoHeader.GetColumnString(2, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("f", emptyNoHeader.GetColumnString(2, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("g", emptyNoHeader.GetColumnString(3, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("h", emptyNoHeader.GetColumnString(3, emptyNoHeader.Schema.Columns[1]));
        }

        [TestMethod]
        public void TestQuotesSingleLine()
        {
            ITraceTableSnapshot emptyNoHeader = ReadTestString("a,\"b,c\",d", false).CreateSnapshot();
            Assert.AreEqual(3, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(1, emptyNoHeader.RowCount);
            Assert.AreEqual("a", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("b,c", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("d", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[2]));
        }

        [TestMethod]
        public void TestQuotesEscaped()
        {
            ITraceTableSnapshot emptyNoHeader = ReadTestString("a,\"b,\"\"c\"\"\",d", false).CreateSnapshot();
            Assert.AreEqual(3, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(1, emptyNoHeader.RowCount);
            Assert.AreEqual("a", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("b,\"c\"", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("d", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[2]));
        }

        [TestMethod]
        public void TestQuotesMultiline()
        {
            ITraceTableSnapshot emptyNoHeader = ReadTestString("a,\"b\r\n,c\",d", false).CreateSnapshot();
            Assert.AreEqual(3, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(1, emptyNoHeader.RowCount);
            Assert.AreEqual("a", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("b\r\n,c", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("d", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[2]));
        }

        [TestMethod]
        public void TestQuotesQuirk()
        {
            // Some CSV have been observed to have quotes with more data around them between commas. Try to handle this elegantly.
            // Alternatively, we could treat quotes that aren't immediately at the start of a column value as literal quotes?
            // Either way, this is not a "well-formed" csv file if this case is hit.
            ITraceTableSnapshot emptyNoHeader = ReadTestString("a,b\"c,d\"e,f", false).CreateSnapshot();
            Assert.AreEqual(3, emptyNoHeader.Schema.Columns.Count);
            Assert.AreEqual(1, emptyNoHeader.RowCount);
            Assert.AreEqual("a", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("bc,de", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("f", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[2]));
        }

        [TestMethod]
        public void TestIrregularColumnCounts()
        {
            // Some CSV have been observed to have quotes with more data around them between commas. Try to handle this elegantly.
            // Alternatively, we could treat quotes that aren't immediately at the start of a column value as literal quotes?
            // Either way, this is not a "well-formed" csv file if this case is hit.
            ITraceTableSnapshot emptyNoHeader = ReadTestString("a,b\nc\nd,e,f", false).CreateSnapshot();
            Assert.AreEqual(2, emptyNoHeader.Schema.Columns.Count); // First row wins.
            Assert.AreEqual(3, emptyNoHeader.RowCount);
            Assert.AreEqual("a", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("b", emptyNoHeader.GetColumnString(0, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("c", emptyNoHeader.GetColumnString(1, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("", emptyNoHeader.GetColumnString(1, emptyNoHeader.Schema.Columns[1]));
            Assert.AreEqual("d", emptyNoHeader.GetColumnString(2, emptyNoHeader.Schema.Columns[0]));
            Assert.AreEqual("e", emptyNoHeader.GetColumnString(2, emptyNoHeader.Schema.Columns[1]));
        }
    }
}