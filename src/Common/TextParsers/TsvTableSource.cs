using System.Text;

namespace InstantTraceViewer
{
    /// <summary>
    /// TSV file parser as a trace source
    /// 
    /// https://en.wikipedia.org/wiki/Tab-separated_values
    /// </summary>
    public class TsvTableSource : TextReaderTableSourceBase
    {
        public TsvTableSource(string tsvFile, bool firstRowIsHeader, bool readInBackground)
            : this(Path.GetFileName(tsvFile), new StreamReader(tsvFile), firstRowIsHeader, readInBackground)
        {
        }

        public TsvTableSource(string displayName, TextReader tsvTextReader, bool firstRowIsHeader, bool readInBackground)
            : base(displayName, tsvTextReader, firstRowIsHeader, readInBackground)
        {
        }

        protected override bool ReadLine(TextReader textReader, StringBuilder valueBuilder, List<string> columnValues)
        {
            while (true)
            {
                int ch = textReader.Read();

                if (ch == '\t')
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
