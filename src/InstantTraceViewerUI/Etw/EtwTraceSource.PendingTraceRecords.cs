using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource
    {
        private delegate void RecordUpdater(ref EtwRecord record);

        private static readonly TimeSpan PendingRecordWallclockMinAge = TimeSpan.FromMilliseconds(2000);
        private static readonly TimeSpan PendingRecordEventTimeMinAge = TimeSpan.FromMilliseconds(100);

        private readonly ReaderWriterLockSlim _pendingRecordsLock = new ReaderWriterLockSlim();
        private DateTime _pendingRecordsStartTime = DateTime.MinValue;
        private List<EtwRecord> _pendingRecords = new();

        private void AddPendingRecord(EtwRecord record)
        {
            if (IsPaused)
            {
                return;
            }

            _pendingRecordsLock.EnterWriteLock();
            try
            {
                if (_pendingRecords.Count == 0)
                {
                    _pendingRecordsStartTime = DateTime.Now;
                }

                _pendingRecords.Add(record);
            }
            finally
            {
                _pendingRecordsLock.ExitWriteLock();
            }
        }

        private bool UpdatePendingRecord(int threadId, double timestampRelativeMSec, RecordUpdater recordUpdater)
        {
            _pendingRecordsLock.EnterWriteLock();
            try
            {
                Span<EtwRecord> pendingTraceRecords = CollectionsMarshal.AsSpan(_pendingRecords);

                for (int i = pendingTraceRecords.Length - 1; i >= 0; i--)
                {
                    ref var pendingRecord = ref pendingTraceRecords[i];

                    if (pendingRecord.TimestampRelativeMSec < timestampRelativeMSec)
                    {
                        break; // Not found and no need to go earlier.
                    }
                    else if (pendingRecord.ThreadId == threadId && pendingRecord.TimestampRelativeMSec == timestampRelativeMSec)
                    {
                        recordUpdater(ref pendingRecord);
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                _pendingRecordsLock.ExitWriteLock();
            }
        }

        // We hold records back briefly so that stackwalk event data can get injected into the record they are associated with.
        // Stackwalk events usually come just a few microseconds after their associated event.
        private List<EtwRecord>? TakeReadyPendingRecords()
        {
            List<EtwRecord>? pendingTraceRecordsLocal = null;
            _pendingRecordsLock.EnterWriteLock();
            try
            {
                if (_pendingRecords.Count > 0)
                {
                    // A record is only flushed once either:
                    // 1. An event at least PendingRecordEventTimeMinAge newer than the record has arrived.
                    // 2. PendingRecordWallclockMinAge of wall-clock time has passed for events up to that record's event timestamp.
                    //    This ensures events eventually flush even if no new events come in.
                    DateTime firstPendingRecordTimestamp = _pendingRecords[0].Timestamp;
                    DateTime maxReadyTimestamp1 = firstPendingRecordTimestamp + (DateTime.Now - _pendingRecordsStartTime - PendingRecordWallclockMinAge);
                    DateTime maxReadyTimestamp2 = _pendingRecords[^1].Timestamp - PendingRecordEventTimeMinAge;
                    DateTime maxReadyTimestamp = maxReadyTimestamp2 > maxReadyTimestamp1 ? maxReadyTimestamp2 : maxReadyTimestamp1;

                    int readyRecordCount = 0;
                    while (readyRecordCount < _pendingRecords.Count && _pendingRecords[readyRecordCount].Timestamp <= maxReadyTimestamp)
                    {
                        readyRecordCount++;
                    }

                    if (readyRecordCount > 0)
                    {
                        pendingTraceRecordsLocal = _pendingRecords.GetRange(0, readyRecordCount);
                        _pendingRecords.RemoveRange(0, readyRecordCount);

                        // Re-anchor the wall-clock reference to the new first record's timestamp so the remaining
                        // records keep the same effective wait window. (When the list is now empty, the next
                        // AddPendingRecord resets the start time, so there is nothing to do.)
                        if (_pendingRecords.Count > 0)
                        {
                            _pendingRecordsStartTime += _pendingRecords[0].Timestamp - firstPendingRecordTimestamp;
                        }
                    }
                }
            }
            finally
            {
                _pendingRecordsLock.ExitWriteLock();
            }

            return pendingTraceRecordsLocal;
        }
    }
}
