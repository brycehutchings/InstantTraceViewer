using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System;
using System.Collections.Generic;
using InstantTraceViewer;

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
            _etwSource.Kernel.StackWalkStack += OnStackWalkStack;

            //
            // Keywords.ContextSwitch
            //
            _etwSource.Kernel.ThreadCSwitch += OnThreadCSwitch;

            //
            // Keywords.Dispatcher
            //
            _etwSource.Kernel.DispatcherReadyThread += OnDispatcherReadyThread;

            //
            // Keywords.ImageLoad
            //
            _etwSource.Kernel.ImageLoad += OnImageLoadUnload;
            _etwSource.Kernel.ImageUnload += OnImageLoadUnload;
            _etwSource.Kernel.ImageDCStart += OnImageLoadUnload;
            _etwSource.Kernel.ImageDCStop += OnImageLoadUnload;

            //
            // Keywords.Thread
            //
            _etwSource.Kernel.ThreadStart += OnThreadEvent;
            _etwSource.Kernel.ThreadStop += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStart += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStop += OnThreadEvent;

            _etwSource.Kernel.ThreadSetName += OnThreadSetName;

            //
            // Keywords.Process
            //
            _etwSource.Kernel.ProcessStart += OnProcessEvent;
            _etwSource.Kernel.ProcessStop += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStart += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStop += OnProcessEvent;
            _etwSource.Kernel.ProcessDefunct += OnProcessEvent;
            // Process Terminate (Opcode=11) events are not subscribable here and come in as "Dynamic" events.
            // I think they are not technically Kernel events and they have associated threads.

            //
            // Keywords.FileIO (and Keywords.DiskFileIO for Filename?)
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

        private void OnImageLoadUnload(ImageLoadTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            var newRecord = CreateBaseTraceRecord(obj);

            // TimeDateStamp is from the PE header and is seconds since January 1, 1970 UTC.
            DateTimeOffset timeDateStamp = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(obj.TimeDateStamp).ToLocalTime();

            newRecord.NamedValues = [
                new NamedValue("File", obj.FileName),
                new NamedValue("ImageBase", obj.ImageBase),
                new NamedValue("ImageSize", obj.ImageSize),
                new NamedValue("TimeDateStamp", timeDateStamp.ToString("yyyy-MM-dd HH:mm:ss")),
                new NamedValue("CheckSum", obj.ImageChecksum)];
            AddEvent(newRecord);
        }

        private void OnStackWalkStack(StackWalkStackTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            // Better for analysis or graphical visualization. Too noisy for logs.
        }

        private void OnThreadCSwitch(CSwitchTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            // Better for analysis or graphical visualization. Too noisy for logs.
        }

        private void OnDispatcherReadyThread(DispatcherReadyThreadTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            // Better for analysis or graphical visualization. Too noisy for logs.
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-create
        private void FileIO_Create(FileIOCreateTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.NamedValues = [
                    new NamedValue("File", obj.FileName),
                    new NamedValue("CreateOptions", (KernelFileCreateOptions)obj.CreateOptions),
                    new NamedValue("CreateDisposition", (KernelFileCreateDisposition)obj.CreateDisposition),
                    new NamedValue("FileAttributes", obj.FileAttributes),
                    new NamedValue("ShareAccess", obj.ShareAccess)];
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend
        private void Kernel_FileIOOperationEnd(FileIOOpEndTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

#if false
            // TODO: This is too noisy to have as its own event. It gives the result of a prior operation.
            // I think we should search backwards and augment the first event with the same IrpPtr with this NtStatus.
            return;
#else
            var newRecord = CreateBaseTraceRecord(obj);
            newRecord.NamedValues = [new NamedValue("NtStatus", obj.NtStatus.ToString("X"))];
            AddEvent(newRecord);
#endif
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-simpleop
        private void FileIO_SimpleOp(FileIOSimpleOpTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.NamedValues = [new NamedValue("File", obj.FileName)];
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-name
        private void FileIO_Name(FileIONameTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.NamedValues = [new NamedValue("File", obj.FileName)];
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite
        private void FileIO_ReadWrite(FileIOReadWriteTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                var namedValues = new List<NamedValue> {
                    new NamedValue("File", obj.FileName),
                    new NamedValue("Offset", obj.Offset),
                    new NamedValue("Size", obj.IoSize)
                };

                if (obj.IoFlags != 0)
                {
                    namedValues.Add(new NamedValue("IoFlags", obj.IoFlags));
                }

                newRecord.NamedValues = namedValues.ToArray();
                AddEvent(newRecord);
            }
        }

        private void FileIO_MapFile(MapFileTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                newRecord.NamedValues = [new NamedValue("File", obj.FileName)];
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-info
        private void FileIO_Info(FileIOInfoTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            if (!string.IsNullOrEmpty(obj.FileName))
            {
                var newRecord = CreateBaseTraceRecord(obj);
                var namedValues = new List<NamedValue> {
                    new NamedValue("File", obj.FileName),
                    new NamedValue("InfoClass", (FileInformationClass)obj.InfoClass)
                };

                if (obj.InfoClass == (int)FileInformationClass.FileEndOfFileInformation)
                {
                    namedValues.Add(new NamedValue("EndOfFilePosition", obj.ExtraInfo));
                }
                else if (obj.ExtraInfo != 0)
                {
                    namedValues.Add(new NamedValue("ExtraInfo", obj.ExtraInfo));
                }

                newRecord.NamedValues = namedValues.ToArray();
                AddEvent(newRecord);
            }
        }

        // https://learn.microsoft.com/en-us/windows/win32/etw/fileio-direnum
        private void FileIO_DirEnum(FileIODirEnumTraceData obj)
        {
            if (IsPaused)
            {
                return;
            }

            var newRecord = CreateBaseTraceRecord(obj);
            newRecord.NamedValues = [
                new NamedValue("Directory", obj.DirectoryName),
                new NamedValue("File", obj.FileName)];
            AddEvent(newRecord);
        }

        private void OnThreadSetName(ThreadSetNameTraceData data)
        {
            _threadNames.AddOrUpdate(data.ThreadID, data.ThreadName, (key, oldValue) => data.ThreadName);

            if (IsPaused)
            {
                return; // We still want to update the names but not add events.
            }
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

            if (IsPaused)
            {
                return; // We still want to update the names but not add events.
            }

            // IGNORED: Very noisy--Do we think anyone will want to see the thread events?
        }

        private void OnProcessEvent(ProcessTraceData data)
        {
            if (data.Opcode == TraceEventOpcode.Start || data.Opcode == TraceEventOpcode.DataCollectionStart)
            {
                _processNames.AddOrUpdate(data.ProcessID, data.ProcessName, (key, oldValue) => data.ProcessName);
            }

            if (IsPaused)
            {
                return; // We still want to update the names but not add events.
            }

            if ((data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStart) ||
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && (_etwSession?.IsRealTime ?? false))
            {
                return; // DCStart/DCStop events are not useful in real-time mode. Lots of spam at the start.
            }

            var newRecord = CreateBaseTraceRecord(data);

            var namedValues = new List<NamedValue>();

            // Exit status seems most interesting so it goes first.
            if (data.Opcode == TraceEventOpcode.Stop)
            {
                namedValues.Add(new NamedValue("ExitStatus", data.ExitStatus));
            }

            namedValues.Add(new NamedValue("ParentPid", data.ParentID));

            if (!string.IsNullOrWhiteSpace(data.CommandLine))
            {
                namedValues.Add(new NamedValue("CommandLine", data.CommandLine));
            }

            if (!string.IsNullOrEmpty(data.PackageFullName))
            {
                namedValues.Add(new NamedValue("PackageFullName", data.PackageFullName));
            }

            namedValues.Add(new NamedValue("SessionID", data.SessionID));

            newRecord.NamedValues = namedValues.ToArray();

            AddEvent(newRecord);
        }
    }
}