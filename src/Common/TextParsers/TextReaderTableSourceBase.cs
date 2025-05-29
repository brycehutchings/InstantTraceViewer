using System.Text;

namespace InstantTraceViewer
{
    /// <summary>
    /// TSV/CSV base file parser as a trace source
    /// </summary>
    public abstract class TextReaderTableSourceBase : ITraceSource
    {
        private const float DefaultColumnSize = 5.0f;
        private readonly TraceTableSchema _schema;
        private readonly string _displayName;
        private TextReader _textReader;
        private Thread _parseThread;

        private readonly ReaderWriterLockSlim _traceRecordsLock = new();
        private ListBuilder<string[]> _traceRecords = new();

        private bool _disposed = false;

        protected TextReaderTableSourceBase(string displayName, TextReader tsvTextReader, bool firstRowIsHeader, bool readInBackground)
        {
            _displayName = displayName;
            _textReader = tsvTextReader;

            // Read the first line to get the column names/count.
            List<string> columnValues = new();
            StringBuilder valueBuilder = new();
            ReadLine(_textReader, valueBuilder, columnValues);
            if (columnValues.Count == 0)
            {
                // The viewer UI does not support 0 columns.
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
                    AddTraceRecord(columnValues);
                }
            }

            if (_textReader.Peek() != -1) // If there is more content to read...
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

        public bool CanPause => false;
        public bool IsPaused => false;
        public void TogglePause()
        {
            throw new NotImplementedException();
        }

        public int LostEvents => 0;

        public bool IsPreprocessingData => false;

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
                    _textReader?.Dispose();
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
            do
            {
                if (ReadLine(_textReader, valueBuilder, columnValues))
                {
                    AddTraceRecord(columnValues);
                }
                valueBuilder.Clear();
                columnValues.Clear();
            }
            while (!_disposed && _textReader.Peek() != -1);
        }

        protected abstract bool ReadLine(TextReader textReader, StringBuilder valueBuilder, List<string> columnValues);

        void AddTraceRecord(List<string> record)
        {
            _traceRecordsLock.EnterWriteLock();
            try
            {
                // Ensure we have an element for every column.
                while (record.Count < _schema.Columns.Count)
                {
                    record.Add("");
                }

                _traceRecords.Add(record.ToArray());
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }
        }
    }
}
