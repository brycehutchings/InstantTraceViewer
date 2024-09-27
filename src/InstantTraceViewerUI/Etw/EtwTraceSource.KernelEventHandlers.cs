using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource : ITraceSource
    {
        // Copied from Microsoft.Diagnostics.Tracing.Parsers.Kernel in order to improve naming.
        [Flags]
        public enum KernelFileCreateOptions
        {
            None = 0,
            Archive = 0x20,
            Compressed = 0x800,
            Device = 0x40,
            Directory = 0x10,
            Encrypted = 0x4000,
            Hidden = 2,
            IntegrityStream = 0x8000,
            Normal = 0x80,
            NotContentIndexed = 0x2000,
            NoScrubData = 0x20000,
            Offline = 0x1000,
            ReadOnly = 1,
            ReparsePoint = 0x400,
            SparseFile = 0x200,
            System = 4,
            Temporary = 0x100,
            Virtual = 0x10000
        }

        // Copied from Microsoft.Diagnostics.Tracing.Parsers.Kernel in order to improve naming.
        public enum KernelFileCreateDisposition
        {
            Supersede = 0,
            CreateNew = 2,
            CreateAlways = 5,
            OpenExisting = 1,
            OpenAlways = 3,
            TruncateExisting = 4
        }

        // Copied from https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ne-wdm-_file_information_class
        private enum FileInformationClass
        {
            FileDirectoryInformation = 1,
            FileFullDirectoryInformation = 2,
            FileBothDirectoryInformation = 3,
            FileBasicInformation = 4,
            FileStandardInformation = 5,
            FileInternalInformation = 6,
            FileEaInformation = 7,
            FileAccessInformation = 8,
            FileNameInformation = 9,
            FileRenameInformation = 10,
            FileLinkInformation = 11,
            FileNamesInformation = 12,
            FileDispositionInformation = 13,
            FilePositionInformation = 14,
            FileFullEaInformation = 15,
            FileModeInformation = 16,
            FileAlignmentInformation = 17,
            FileAllInformation = 18,
            FileAllocationInformation = 19,
            FileEndOfFileInformation = 20,
            FileAlternateNameInformation = 21,
            FileStreamInformation = 22,
            FilePipeInformation = 23,
            FilePipeLocalInformation = 24,
            FilePipeRemoteInformation = 25,
            FileMailslotQueryInformation = 26,
            FileMailslotSetInformation = 27,
            FileCompressionInformation = 28,
            FileObjectIdInformation = 29,
            FileCompletionInformation = 30,
            FileMoveClusterInformation = 31,
            FileQuotaInformation = 32,
            FileReparsePointInformation = 33,
            FileNetworkOpenInformation = 34,
            FileAttributeTagInformation = 35,
            FileTrackingInformation = 36,
            FileIdBothDirectoryInformation = 37,
            FileIdFullDirectoryInformation = 38,
            FileValidDataLengthInformation = 39,
            FileShortNameInformation = 40,
            FileIoCompletionNotificationInformation = 41,
            FileIoStatusBlockRangeInformation = 42,
            FileIoPriorityHintInformation = 43,
            FileSfioReserveInformation = 44,
            FileSfioVolumeInformation = 45,
            FileHardLinkInformation = 46,
            FileProcessIdsUsingFileInformation = 47,
            FileNormalizedNameInformation = 48,
            FileNetworkPhysicalNameInformation = 49,
            FileIdGlobalTxDirectoryInformation = 50,
            FileIsRemoteDeviceInformation = 51,
            FileUnusedInformation = 52,
            FileNumaNodeInformation = 53,
            FileStandardLinkInformation = 54,
            FileRemoteProtocolInformation = 55,
            FileRenameInformationBypassAccessCheck = 56,
            FileLinkInformationBypassAccessCheck = 57,
            FileVolumeNameInformation = 58,
            FileIdInformation = 59,
            FileIdExtdDirectoryInformation = 60,
            FileReplaceCompletionInformation = 61,
            FileHardLinkFullIdInformation = 62,
            FileIdExtdBothDirectoryInformation = 63,
            FileDispositionInformationEx = 64,
            FileRenameInformationEx = 65,
            FileRenameInformationExBypassAccessCheck = 66,
            FileDesiredStorageClassInformation = 67,
            FileStatInformation = 68,
            FileMemoryPartitionInformation = 69,
            FileStatLxInformation = 70,
            FileCaseSensitiveInformation = 71,
            FileLinkInformationEx = 72,
            FileLinkInformationExBypassAccessCheck = 73,
            FileStorageReserveIdInformation = 74,
            FileCaseSensitiveInformationForceAccessCheck = 75,
            FileKnownFolderInformation = 76,
            FileStatBasicInformation = 77,
            FileId64ExtdDirectoryInformation = 78,
            FileId64ExtdBothDirectoryInformation = 79,
            FileIdAllExtdDirectoryInformation = 80,
            FileIdAllExtdBothDirectoryInformation = 81,
            FileStreamReservationInformation,
            FileMupProviderInfo,
            FileMaximumInformation
        }

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
                newRecord.Message = $"File:{obj.FileName} CreateOptions:{(KernelFileCreateOptions)obj.CreateOptions} CreateDisposition:{(KernelFileCreateDisposition)obj.CreateDisposition} FileAttributes:{obj.FileAttributes} ShareAccess:{obj.ShareAccess}"; // IrpPtr:{obj.IrpPtr} FileObject:{obj.FileObject}";
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend
        private void Kernel_FileIOOperationEnd(FileIOOpEndTraceData obj)
        {
#if false
            // TODO: This is too noisy to have as its own event. It gives the result of a prior operation.
            // I think we should search backwards and augment the first event with the same IrpPtr with this NtStatus.
            return;
#else
            var newRecord = CreateBaseTraceRecord(obj);
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
                newRecord.Message = $"File:{obj.FileName}"; // FileKey:{obj.FileKey}";
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite
        private void FileIO_ReadWrite(FileIOReadWriteTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                // TODO: How do we parse the IO request packet flags (obj.IoFlags)?
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.Message = $"File:{obj.FileName} Offset:{obj.Offset} Size:{obj.IoSize}"; // IrpPtr:{obj.IrpPtr} FileKey:{obj.FileKey}";

                if (obj.IoFlags != 0)
                {
                    newRecord.Message += $" IoFlags:{obj.IoFlags}";
                }

                AddEvent(newRecord);
            }
        }

        private void FileIO_MapFile(MapFileTraceData obj)
        {
            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
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
                newRecord.Message = $"File:{obj.FileName} InfoClass={(FileInformationClass)obj.InfoClass}"; // IrpPtr:{obj.IrpPtr} FileKey:{obj.FileKey}";

                if (obj.InfoClass == (int)FileInformationClass.FileEndOfFileInformation)
                {
                    newRecord.Message += $" EndOfFilePosition:{obj.ExtraInfo}";
                }
                else if (obj.ExtraInfo != 0)
                {
                    newRecord.Message += $" ExtraInfo:{obj.ExtraInfo}";
                }

                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-direnum
        private void FileIO_DirEnum(FileIODirEnumTraceData obj)
        {
            var newRecord = CreateBaseTraceRecord(obj);
            newRecord.Message = $"Directory:{obj.DirectoryName} File:{obj.FileName}"; // IrpPtr:{obj.IrpPtr} FileKey:{obj.FileKey}";
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