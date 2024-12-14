using System.Text;

namespace InstantTraceViewer
{
    /// <summary>
    /// CSV file parser as a trace source
    /// 
    /// Some notes on CSV format:
    /// https://tools.ietf.org/html/rfc4180
    /// https://en.wikipedia.org/wiki/Comma-separated_values
    /// 
    /// Test cases:
    /// https://github.com/maxogden/csv-spectrum/tree/master/csvs
    /// https://github.com/pandas-dev/pandas/tree/e0a127a82868e432e5a1ee067b39ef7142d73d66/pandas/tests/io/parser/data
    /// https://github.com/mapnik/test-data/tree/dac50a321bdcc92c6183ded08200eb8fa117532c/csv
    /// </summary>
    public class CsvTableSource : TextReaderTableSourceBase
    {
        public CsvTableSource(string tsvFile, bool firstRowIsHeader, bool readInBackground)
            : this(Path.GetFileName(tsvFile), new StreamReader(tsvFile), firstRowIsHeader, readInBackground)
        {
        }

        public CsvTableSource(string displayName, TextReader tsvTextReader, bool firstRowIsHeader, bool readInBackground)
            : base(displayName, tsvTextReader, firstRowIsHeader, readInBackground)
        {
        }

        protected override bool ReadLine(TextReader textReader, StringBuilder valueBuilder, List<string> columnValues)
        {
            while (true)
            {
                int ch = textReader.Read();

                // NOTE: A well-formed CSV will only have a starting quote right after a comma or start of line, but this is more flexible.
                // If a quote is hit, we will read until the end quote is hit or end of line.
                if (ch == '\"')
                {
                    while (true)
                    {
                        ch = textReader.Read();
                        if (ch == '\"' || ch == -1)
                        {
                            if (textReader.Peek() == '\"')
                            {
                                // This is an escaped quote.
                                valueBuilder.Append('\"');
                                continue;
                            }

                            break;
                        }

                        valueBuilder.Append((char)ch);
                    }
                }
                else if (ch == ',')
                {
                    // We have reached the end of the value by hitting a comma
                    columnValues.Add(valueBuilder.ToString());
                    valueBuilder.Clear();
                }
                else if (ch == '\n' || ch == '\r' || ch == -1 /* eof */)
                {
                    // We have reached the end of the value by hitting the end of line marker or end of file.
                    columnValues.Add(valueBuilder.ToString());
                    valueBuilder.Clear();

                    // Move past \r, \n or \r\n so that the next line starts reading on the new line.
                    if (ch == '\r')
                    {
                        if (textReader.Peek() == '\n')
                        {
                            textReader.Read();
                        }
                    }

                    return ch == -1;
                }
                else
                {
                    valueBuilder.Append((char)ch);
                }
            }
        }
    }
}
