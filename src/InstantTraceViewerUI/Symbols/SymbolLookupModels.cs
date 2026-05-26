using System;

namespace InstantTraceViewerUI.Symbols
{
    internal readonly record struct ModuleLookupRequest(
        string FileName,
        ulong SizeOfImage,
        uint TimeDateStamp)
    {
        public bool HasTimeDateStamp => TimeDateStamp != 0;
    }

    internal readonly record struct PdbLookupRequest(
        string PdbFileName,
        Guid Guid,
        uint Age)
    {
        public bool HasGuid => Guid != Guid.Empty;
    }

    internal readonly record struct DbgHelpFileIndexInfo(
        string FileName,
        uint TimeDateStamp,
        uint Size,
        string PdbFileName,
        Guid PdbGuid,
        uint PdbAge,
        uint PdbSignature)
    {
        public bool HasPdbIdentity => !string.IsNullOrEmpty(PdbFileName) && PdbGuid != Guid.Empty;
    }

    internal readonly record struct ResolvedSymbol(
        string ModuleName,
        ulong RelativeVirtualAddress,
        string SymbolName,
        ulong Displacement,
        bool Verified)
    {
        public string Format()
        {
            return Displacement == 0 ? SymbolName : $"{SymbolName}+0x{Displacement:X}";
        }
    }
}