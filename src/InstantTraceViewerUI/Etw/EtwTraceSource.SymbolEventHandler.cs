using InstantTraceViewer;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource : ITraceSource
    {
        private void SubscribeToSymbolEvents()
        {
            // SymbolTraceEventParser's documentation explains why we need this:
            // Kernel traces have information about images that are loaded, however they don't have enough information
            // in the events themselves to unambigously look up PDBs without looking at the data inside the images.
            // This means that symbols can't be resolved unless you are on the same machine on which you gathered the data.
            // 
            // XPERF solves this problem by adding new 'synthetic' events that it creates by looking at the trace and then
            // opening each DLL mentioned and extracting the information needed to look PDBS up on a symbol server (this 
            // includes the PE file's TimeDateStamp as well as a PDB Guid, and 'pdbAge' that can be found in the DLLs header.
            _symbolEventParser.All += SymbolEventParser_All;

            // These events are injected in at the same timestamp and the kernel Image events. So this symbol data can be correlated
            // with the kernel Image events by Timestamp + ImageBase. Also they come right before the kernel Image events.

            _symbolEventParser.ImageIDDbgID_RSDS += _symbolEventParser_ImageIDDbgID_RSDS;
            _symbolEventParser.ImageIDDbgID_ILRSDS += _symbolEventParser_ImageIDDbgID_ILRSDS;

            _symbolEventParser.ImageIDFileVersion += _symbolEventParser_ImageIDFileVersion;

            // We don't need this one. Even though it includes the TimeDateStamp which is often missing from the Kernel load events, we actually only need the GuidSig and Age to locate the PDB.
            // "ImageID" - ImageBase, ImageSize, ProcessID, TimeDateStamp, BuildTime (often bogus), OriginalFileName
            // _symbolEventParser.ImageID += <ignored>

            // I don't see a use for this one. MajorVersion and MinorVersion are usually 0 or 77?
            // "ImageID/DbgPPDB" - TimeDateStamp, MajorVersion, MinorVersion
            // _symbolEventParser.ImageIDDbgPPDB += <ignored>


            /*
            48204 (MrShell)	39108	KernelTraceControl	64		ImageID/FileVersion	Always	2026-05-13 08:39:09.599900	ImageSize:78716928 TimeDateStamp:1777934217 BuildTime:2026-05-04 15:36:57.000000 OrigFileName:MrShell.exe FileDescription: FileVersion:1.0.0.0 BinFileVersion:1.0.0.0 VerLanguage:1033 ProductName: CompanyName: ProductVersion:1.0.0.0 FileId:00006d56ed143fad2eb687316e8bfeefe9da757081c1 ProgramId:0006322c2ec31bae675159dee83eca493eae00000904
            48204 (MrShell)	39108	KernelTraceControl	Info		ImageID	Always	2026-05-13 08:39:09.599900	ImageBase:0x7FF765130000 ImageSize:78716928 ProcessID:0 TimeDateStamp:1777934217 BuildTime:2026-05-04 15:36:57.000000 OriginalFileName:MrShell.exe
            48204 (MrShell)	39108	KernelTraceControl	36		ImageID/DbgID_RSDS	Always	2026-05-13 08:39:09.599900	ImageBase:0x7FF765130000 GuidSig:eae2b3f6-8392-4804-9de8-5621378da3e2 Age:1 PDBFileName:F:\1\s\bin\godot.windows.template_release.x86_64.mono.pdb
            48204 (MrShell)	39108	Windows Kernel	Load		Image	Always	2026-05-13 08:39:09.599900	File:D:\repos\cloud1\binlocal\Immersive\Desktop\WinX64\MrShell\MrShell.exe ImageBase:0x7FF765130000 SizeOfImage:0x04B12000(78716928) CheckSum:0x00000000 TimeDateStamp:0x69F91F89 (1777934217)
            */
        }

        private void _symbolEventParser_ImageIDDbgID_RSDS(Microsoft.Diagnostics.Tracing.Parsers.Symbol.DbgIDRSDSTraceData obj)
        {
            // "ImageID/DbgID_RSDS" - ImageBase, GuidSig, Age, PDBFileName
        }

        private void _symbolEventParser_ImageIDDbgID_ILRSDS(Microsoft.Diagnostics.Tracing.Parsers.Symbol.DbgIDILRSDSTraceData obj)
        {
            // "ImageID/DbgID_ILRSDS" - ImageBase, GuidSig, Age, PDBFileName
        }

        private void _symbolEventParser_ImageIDFileVersion(Microsoft.Diagnostics.Tracing.Parsers.Symbol.FileVersionTraceData obj)
        {
            // "ImageID/FileVersion" - ImageSize, TimeDateStamp, BuildTime (often bogus), OrigFileName, FileDescription, FileVersion, BinFileVersion, VerLanguage, ProductName, CompanyName, ProductVersion, FileId, ProgramId
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
    }
}