using System.IO;
using System.Runtime.Serialization.Json;
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
    /// 
    public class CsvTableSource : ITraceSource
    {
        private const float DefaultColumnSize = 5.0f;
        private readonly TraceTableSchema _schema;
        private readonly string _displayName;
        private TextReader _csvTextReader;
        private Thread _parseThread;

        private readonly ReaderWriterLockSlim _traceRecordsLock = new();
        private ListBuilder<string[]> _traceRecords = new();

        private bool _disposed = false;

        public CsvTableSource(string tsvFile, bool firstRowIsHeader, bool readInBackground)
            : this(Path.GetFileName(tsvFile), new StreamReader(tsvFile), firstRowIsHeader, readInBackground)
        {
        }

        public CsvTableSource(string displayName, TextReader tsvTextReader, bool firstRowIsHeader, bool readInBackground)
        {
            _displayName = displayName;
            _csvTextReader = tsvTextReader;

            // Read the first line to get the column names/count.
            List<string> columnValues = new();
            StringBuilder valueBuilder = new();
            bool reachedEof = ReadLine(valueBuilder, columnValues);

            // The viewer UI does not support 0 columns.
            if (columnValues.Count == 0)
            {
                _schema = new TraceTableSchema() { Columns = [new TraceSourceSchemaColumn() { Name = "Empty", DefaultColumnSize = DefaultColumnSize }] };
            }
            else
            {
                if (firstRowIsHeader)
                {
                    _schema = new TraceTableSchema() { Columns = columnValues.Select(name => new TraceSourceSchemaColumn() { Name = name, DefaultColumnSize = DefaultColumnSize }).ToList() };
                }
                else
                {
                    _schema = new TraceTableSchema() { Columns = Enumerable.Range(0, columnValues.Count).Select(i => new TraceSourceSchemaColumn() { Name = $"Column{i}", DefaultColumnSize = DefaultColumnSize }).ToList() };
                    AddTraceRecord(columnValues.ToArray());
                }
            }

            if (!reachedEof)
            {
                if (readInBackground)
                {
                    _parseThread = new Thread(ReadRows);
                    _parseThread.Start();
                }
                else
                {
                    ReadRows();
                }
            }
        }

        public string DisplayName => _displayName;

        public bool CanClear => false;

        public void Clear()
        {
            throw new NotImplementedException();
        }

        public ITraceTableSnapshot CreateSnapshot()
        {
            _traceRecordsLock.EnterReadLock();
            try
            {
                return new StringArrayTableSnapshot(_traceRecords.CreateSnapshot(), _schema);
            }
            finally
            {
                _traceRecordsLock.ExitReadLock();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _csvTextReader?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void ReadRows()
        {
            List<string> columnValues = new();
            StringBuilder valueBuilder = new();
            bool reachedEof;
            do
            {
                reachedEof = ReadLine(valueBuilder, columnValues);
                AddTraceRecord(columnValues.ToArray());
                valueBuilder.Clear();
                columnValues.Clear();
            }
            while (!_disposed && !reachedEof);
        }

        private bool ReadLine(StringBuilder valueBuilder, List<string> columnValues)
        {
            while (true)
            {
                int ch = _csvTextReader.Read();

                // NOTE: A well-formed CSV will only have a starting quote right after a comma or start of line, but this is more flexible.
                // If a quote is hit, we will read until the end quote is hit or end of line.
                if (ch == '\"')
                {
                    while (true)
                    {
                        ch = _csvTextReader.Read();
                        if (ch == '\"' || ch == -1)
                        {
                            if (_csvTextReader.Peek() == '\"')
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
                        if (_csvTextReader.Peek() == '\n')
                        {
                            _csvTextReader.Read();
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

        void AddTraceRecord(string[] record)
        {
            _traceRecordsLock.EnterWriteLock();
            try
            {
                _traceRecords.Add(record);
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }
        }
    }
}
