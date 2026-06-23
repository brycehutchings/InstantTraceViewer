using Microsoft.Diagnostics.Tracing;
using System;
using System.IO;
using InstantTraceViewer;
using InstantTraceViewerUI.Symbols;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource
    {
        // Sometimes the tracing library will show this provider name is shown as "MSNT_SystemTrace" and other times as "Kernel Provider" (and maybe other names?).
        // https://learn.microsoft.com/en-us/windows/win32/etw/msnt-systemtrace
        public readonly static Guid SystemProvider = Guid.Parse("{9e814aad-3204-11d2-9a82-006008a86939}");

        private EtwRecord CreateBaseTraceRecord(TraceEvent data)
        {
            var newRecord = new EtwRecord();
            newRecord.ProcessId = data.ProcessID;
            newRecord.ThreadId = data.ThreadID;
            newRecord.Timestamp = data.TimeStamp;
            newRecord.TimestampRelativeMSec = data.TimeStampRelativeMSec;
            newRecord.Level = data.Level;
            newRecord.Keywords = (ulong)data.Keywords;

            // Extract process and thread IDs from events without them (e.g. some Kernel events).
            if (newRecord.ProcessId == -1 || newRecord.ThreadId == -1)
            {
                for (int i = 0; i < data.PayloadNames.Length; i++)
                {
                    if (newRecord.ProcessId == -1 && string.Equals(data.PayloadNames[i], "ProcessID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int pid)
                    {
                        newRecord.ProcessId = pid;
                    }
                    else if (newRecord.ThreadId == -1 && string.Equals(data.PayloadNames[i], "ThreadID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int tid)
                    {
                        newRecord.ThreadId = tid;
                    }
                }
            }

            newRecord.ProcessName = _processNames.TryGetValue(newRecord.ProcessId, out string processName) ? processName : null;
            newRecord.ThreadName = _threadNames.TryGetValue(newRecord.ThreadId, out string threadName) ? threadName : null;
            newRecord.ProviderName = data.ProviderName;

            if (data.ProviderGuid == SystemProvider && !Enum.IsDefined(data.Opcode))
            {
                // Use EventName which is "TaskName/OpCodeName" because many of the Kernel/System OpCodes aren't defined in the TraceEventOpcode enum
                // and will display as opaque numbers. We don't set OpCode separately because it'll be distracting when the OpCode is part of the name.
                newRecord.Name = data.EventName;
            }
            else
            {
                newRecord.Name = data.TaskName;
                newRecord.OpCode = (byte)data.Opcode;
            }

            return newRecord;
        }

        private StackFrame ResolveInstructionPointer(int processId, DateTime timestamp, ulong instructionPointer)
        {
            LoadedImage? loadedImage = _moduleTracker.GetLoadedImage(processId, instructionPointer, timestamp);
            if (!loadedImage.HasValue)
            {
                return new StackFrame(instructionPointer, null);
            }

            ulong relativeVirtualAddress = instructionPointer - loadedImage.Value.ImageBase;

            string? symbolName = SymbolResolver.Instance.ResolveSymbol(loadedImage.Value.RegisteredModule, relativeVirtualAddress);
            if (!string.IsNullOrEmpty(symbolName))
            {
                return new StackFrame(instructionPointer, symbolName);
            }

            string moduleName = Path.GetFileName(loadedImage.Value.FileName);
            if (string.IsNullOrEmpty(moduleName))
            {
                moduleName = loadedImage.Value.FileName;
            }

            return new StackFrame(instructionPointer, $"{moduleName}+0x{relativeVirtualAddress:X}");
        }

        // Invoked when new symbols have been loaded. Walks already-collected records (both committed and pending) and
        // re-resolves every stack frame so previously unresolved instruction pointers can pick up the new symbols.
        private void ReResolveAllStackFrames()
        {
            _pendingRecordsLock.EnterWriteLock();
            try
            {
                foreach (var record in _pendingRecords)
                {
                    ReResolveStackFrames(record);
                }
            }
            finally
            {
                _pendingRecordsLock.ExitWriteLock();
            }

            _traceRecordsLock.EnterWriteLock();
            try
            {
                foreach (var record in _traceRecords.CreateSnapshot())
                {
                    ReResolveStackFrames(record);
                }

                // Bump the generation so consumers re-read the now-updated stack frames.
                _generationId++;
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }
        }

        private void ReResolveStackFrames(in EtwRecord record)
        {
            if (record.NamedValues == null)
            {
                return;
            }

            // Current stack frames are never nested so this query is sufficient.
            foreach (var namedValue in record.NamedValues)
            {
                if (namedValue.Value is StackFrame[] stackFrames)
                {
                    for (int i = 0; i < stackFrames.Length; i++)
                    {
                        stackFrames[i] = ResolveInstructionPointer(record.ProcessId, record.Timestamp, stackFrames[i].InstructionPointer);
                    }
                }
            }
        }
    }
}
