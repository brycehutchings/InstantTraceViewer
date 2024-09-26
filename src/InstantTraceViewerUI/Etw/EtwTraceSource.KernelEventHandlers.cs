using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource : ITraceSource
    {
        private void SubscribeToKernelEvents()
        {
            //
            // Thread
            //
            _etwSource.Kernel.ThreadStart += OnThreadEvent;
            _etwSource.Kernel.ThreadStop += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStart += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStop += OnThreadEvent;

            _etwSource.Kernel.ThreadSetName += OnThreadSetName;

            //
            // Process
            //
            _etwSource.Kernel.ProcessStart += OnProcessEvent;
            _etwSource.Kernel.ProcessStop += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStart += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStop += OnProcessEvent;
            _etwSource.Kernel.ProcessDefunct += OnProcessEvent;

            //
            // FileIO
            //
            _etwSource.Kernel.FileIOMapFile += FileIO_MapFile;
            _etwSource.Kernel.FileIOUnmapFile += FileIO_MapFile;
            _etwSource.Kernel.FileIOMapFileDCStart += FileIO_MapFile;
            _etwSource.Kernel.FileIOMapFileDCStop += FileIO_MapFile;

            _etwSource.Kernel.FileIOName += FileIO_Name;
            _etwSource.Kernel.FileIOFileCreate += FileIO_Name;
            _etwSource.Kernel.FileIOFileDelete += FileIO_Name;
            _etwSource.Kernel.FileIOFileRundown += FileIO_Name;

            _etwSource.Kernel.FileIOCreate += FileIO_Create;
            _etwSource.Kernel.FileIOCleanup += FileIO_SimpleOp;
            _etwSource.Kernel.FileIOClose += FileIO_SimpleOp;
            _etwSource.Kernel.FileIOFlush += FileIO_SimpleOp;

            _etwSource.Kernel.FileIORead += FileIO_ReadWrite;
            _etwSource.Kernel.FileIOWrite += FileIO_ReadWrite;

            _etwSource.Kernel.FileIOSetInfo += FileIO_Info;
            _etwSource.Kernel.FileIODelete += FileIO_Info;
            _etwSource.Kernel.FileIORename += FileIO_Info;
            _etwSource.Kernel.FileIOQueryInfo += FileIO_Info;
            _etwSource.Kernel.FileIOFSControl += FileIO_Info;

            _etwSource.Kernel.FileIODirEnum += FileIO_DirEnum;
            _etwSource.Kernel.FileIODirNotify += FileIO_DirEnum;

            _etwSource.Kernel.FileIOOperationEnd += Kernel_FileIOOperationEnd;
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-create
        private void FileIO_Create(FileIOCreateTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.Name = obj.EventName;
                newRecord.Message = $"File:{obj.FileName} CreateOptions:{obj.CreateOptions} CreateDisposition:{obj.CreateDisposition} FileAttributes:{obj.FileAttributes} ShareAccess:{obj.ShareAccess}"; // IrpPtr:{obj.IrpPtr} FileObject:{obj.FileObject}";
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend
        private void Kernel_FileIOOperationEnd(FileIOOpEndTraceData obj)
        {
#if true
            // TODO: This is too noisy to have as its own event. It gives the result of a prior operation.
            // I think we should search backwards and augment the first event with the same IrpPtr with this NtStatus.
            return;
#else
            var newRecord = CreateBaseTraceRecord(obj);
            newRecord.Name = obj.EventName;
            newRecord.Message = $"NtStatus:{obj.NtStatus:X}"; // IrpPtr:{obj.IrpPtr}";
            AddEvent(newRecord);
#endif
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-simpleop
        private void FileIO_SimpleOp(FileIOSimpleOpTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.Name = obj.EventName;
                newRecord.Message = $"File:{obj.FileName}"; // IrpPtr:{obj.IrpPtr} FileKey:{obj.FileKey} FileObject:{obj.FileObject}";
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-name
        private void FileIO_Name(FileIONameTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.Name = obj.EventName;
                newRecord.Message = $"File:{obj.FileName}"; // FileKey:{obj.FileKey}";
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite
        private void FileIO_ReadWrite(FileIOReadWriteTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.Name = obj.EventName;
                newRecord.Message = $"File:{obj.FileName}"; // IrpPtr:{obj.IrpPtr} FileKey:{obj.FileKey}";
                AddEvent(newRecord);
            }
        }

        private void FileIO_MapFile(MapFileTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.Name = obj.EventName;
                newRecord.Message = $"File:{obj.FileName}"; // FileKey:{obj.FileKey}";
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-info
        private void FileIO_Info(FileIOInfoTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.Name = obj.EventName;
                newRecord.Message = $"File:{obj.FileName}"; // IrpPtr:{obj.IrpPtr} FileKey:{obj.FileKey}";
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-direnum
        private void FileIO_DirEnum(FileIODirEnumTraceData obj)
        {
            var newRecord = CreateBaseTraceRecord(obj);
            newRecord.Name = obj.EventName;
            newRecord.Message = $"Directory:{obj.DirectoryName} File:{obj.FileName} IrpPtr:{obj.IrpPtr} FileKey:{obj.FileKey}";
            AddEvent(newRecord);
        }

        private void OnThreadSetName(ThreadSetNameTraceData data)
        {
            _threadNames.AddOrUpdate(data.ThreadID, data.ThreadName, (key, oldValue) => data.ThreadName);
        }

        private void OnThreadEvent(ThreadTraceData data)
        {
            if (data.Opcode == TraceEventOpcode.Start || data.Opcode == TraceEventOpcode.DataCollectionStart)
            {
                if (!string.IsNullOrEmpty(data.ThreadName))
                {
                    _threadNames.AddOrUpdate(data.ThreadID, data.ThreadName, (key, oldValue) => data.ThreadName);
                }
                else
                {
                    // In case a thread id is reused, we want to make sure we don't have stale data. We need to use a timestamp if we want to keep old and new names around.
                    _threadNames.TryRemove(data.ThreadID, out _);
                }
            }

            // Very noisy--Do we think anyone will want to see the thread events?
#if false
            if (data.ProcessID == 0 && data.ThreadID == 0)
            {
                return; // Skip the idle process and thread.
            }

            if ((data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStart) ||
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && (_etwSession?.IsRealTime ?? false))
            {
                return; // DCStart/DCStop events are not useful in real-time mode. Lots of spam at the start.
            }

            var newRecord = CreateBaseTraceRecord(data);
            newRecord.ProviderName = "Kernel";

            StringBuilder sb = new();
            if (data.ParentProcessID > 0)
            {
                AppendField(sb, "ParentPid", data.ParentProcessID.ToString());
            }
            if (data.ParentThreadID > 0)
            {
                AppendField(sb, "ParentTid", data.ParentThreadID.ToString());
            }
            if (!string.IsNullOrEmpty(data.ThreadName))
            {
                AppendField(sb, "ThreadName", data.ThreadName);
            }
            newRecord.Message = sb.ToString();
            AddEvent(newRecord);
#endif
        }

        private void OnProcessEvent(ProcessTraceData data)
        {
            if (data.Opcode == TraceEventOpcode.Start || data.Opcode == TraceEventOpcode.DataCollectionStart)
            {
                _processNames.AddOrUpdate(data.ProcessID, data.ProcessName, (key, oldValue) => data.ProcessName);
            }

            if ((data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStart) ||
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && (_etwSession?.IsRealTime ?? false))
            {
                return; // DCStart/DCStop events are not useful in real-time mode. Lots of spam at the start.
            }

            var newRecord = CreateBaseTraceRecord(data);
            newRecord.Message = $"ParentPid:{data.ParentID} CommandLine:{data.CommandLine}";
            AddEvent(newRecord);
        }
    }
}