using InstantTraceViewer;
using System;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource : ITraceSource
    {
        struct LastImagePdbInfo
        {
            public int ProcessId;
            public int ThreadId;
            public ulong ImageBase;
            public string PdbFileName;
            public int PdbAge;
            public Guid PdbSig;
        }

        private LastImagePdbInfo _lastImagePdbInfo;

        private void SubscribeToSymbolEvents()
        {
            // These events are injected in at the same timestamp and the kernel Image events. So this symbol data can be correlated
            // with the kernel Image events by Timestamp + ImageBase. Also they come right before the kernel Image events.

            // SymbolTraceEventParser's documentation explains why we need this:
            // Kernel traces have information about images that are loaded, however they don't have enough information
            // in the events themselves to unambigously look up PDBs without looking at the data inside the images.
            // This means that symbols can't be resolved unless you are on the same machine on which you gathered the data.
            // 
            // XPERF solves this problem by adding new 'synthetic' events that it creates by looking at the trace and then
            // opening each DLL mentioned and extracting the information needed to look PDBS up on a symbol server (this 
            // includes the PE file's TimeDateStamp as well as a PDB Guid, and 'pdbAge' that can be found in the DLLs header.
            _symbolEventParser.ImageIDDbgID_RSDS += _symbolEventParser_ImageIDDbgID_RSDS;
            _symbolEventParser.ImageIDDbgID_ILRSDS += _symbolEventParser_ImageIDDbgID_ILRSDS;

#if false
            _symbolEventParser.ImageIDFileVersion += _symbolEventParser_ImageIDFileVersion;

            // _symbolEventParser.All += SymbolEventParser_All;

            // We don't need this one. Even though it includes the TimeDateStamp which is often missing from the Kernel load events, we actually only need the GuidSig and Age to locate the PDB.
            // "ImageID" - ImageBase, ImageSize, ProcessID, TimeDateStamp, BuildTime (often bogus), OriginalFileName
            // _symbolEventParser.ImageID += <ignored>

            // I don't see a use for this one. MajorVersion and MinorVersion are usually 0 or 77?
            // "ImageID/DbgPPDB" - TimeDateStamp, MajorVersion, MinorVersion
            // _symbolEventParser.ImageIDDbgPPDB += <ignored>
#endif
        }

        private void _symbolEventParser_ImageIDDbgID_RSDS(Microsoft.Diagnostics.Tracing.Parsers.Symbol.DbgIDRSDSTraceData obj)
        {
            _lastImagePdbInfo.ProcessId = obj.ProcessID;
            _lastImagePdbInfo.ThreadId = obj.ThreadID;
            _lastImagePdbInfo.ImageBase = obj.ImageBase;
            _lastImagePdbInfo.PdbFileName = obj.PdbFileName;
            _lastImagePdbInfo.PdbAge = obj.Age;
            _lastImagePdbInfo.PdbSig = obj.GuidSig;
        }

        private void _symbolEventParser_ImageIDDbgID_ILRSDS(Microsoft.Diagnostics.Tracing.Parsers.Symbol.DbgIDILRSDSTraceData obj)
        {
            _lastImagePdbInfo.ProcessId = obj.ProcessID;
            _lastImagePdbInfo.ThreadId = obj.ThreadID;
            _lastImagePdbInfo.PdbFileName = obj.PdbFileName;
            _lastImagePdbInfo.PdbAge = obj.Age;
            _lastImagePdbInfo.ImageBase = obj.ImageBase;
            _lastImagePdbInfo.PdbSig = obj.GuidSig;
        }

#if false
        private void _symbolEventParser_ImageIDFileVersion(Microsoft.Diagnostics.Tracing.Parsers.Symbol.FileVersionTraceData obj)
        {
            // "ImageID/FileVersion" - ImageSize, TimeDateStamp, BuildTime (often bogus), OrigFileName, FileDescription, FileVersion, BinFileVersion, VerLanguage, ProductName, CompanyName, ProductVersion, FileId, ProgramId
            // TODO: Not currently exposed.
        }

        private void SymbolEventParser_All(TraceEvent obj)
        {
            var newRecord = CreateBaseTraceRecord(obj);
            newRecord.Name = obj.EventName;

            var namedValues = new List<NamedValue>();
            foreach (var payloadName in obj.PayloadNames)
            {
                namedValues.Add(new NamedValue(payloadName, obj.PayloadByName(payloadName)));
            }

            newRecord.NamedValues = namedValues.ToArray();
            AddEvent(newRecord);
        }
#endif
    }
}