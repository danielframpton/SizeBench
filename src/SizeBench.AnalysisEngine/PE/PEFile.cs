﻿using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SizeBench.AnalysisEngine.DIAInterop;
using SizeBench.AnalysisEngine.Helpers;
using SizeBench.AnalysisEngine.Symbols;
using SizeBench.Logging;

namespace SizeBench.AnalysisEngine.PE;

internal sealed class PEFile : IDisposable, IBinaryDataLoader
{
    private readonly IntPtr _library = IntPtr.Zero;
    private readonly unsafe byte* _libraryBaseAddress = null;
    private readonly ulong _libraryPreferredLoadAddress;
    private readonly IMAGE_NT_HEADERS32 _headers32;
    private readonly IMAGE_NT_HEADERS64 _headers64;

    // The TempFile will only exist if _hasForceIntegrityBitSet == true
    private readonly bool _hasForceIntegrityBitSet;
    internal GuaranteedLocalFile GuaranteedLocalCopyOfBinary { get; }


    // This property is controlled by the "/filealign:<x>" option to link.exe
    public uint FileAlignment { get; }

    // This property is controlled by the "/align:<x>" option to link.exe
    public uint SectionAlignment { get; }

    public byte BytesPerWord { get; }

    public MachineType MachineType => this.BytesPerWord == 8 ? this._headers64.FileHeader.Machine : this._headers32.FileHeader.Machine;

    private List<IMAGE_DEBUG_DIRECTORY> DebugDirectories { get; set; } = new List<IMAGE_DEBUG_DIRECTORY>();

    internal PEFileDebugSignature DebugSignature { get; private set; } = new PEFileDebugSignature(Guid.Empty, 0, String.Empty);

    public RVARange RsrcRange { get; }
    internal SortedList<uint, RsrcSymbolBase> RsrcSymbols { get; } = new SortedList<uint, RsrcSymbolBase>();

    internal SortedList<uint, ISymbol> OtherPESymbols { get; } = new SortedList<uint, ISymbol>();
    internal RVARangeSet OtherPESymbolsRVARanges { get; }

    internal class PEFileDebugSignature
    {
        public Guid Guid { get; }
        public uint Age { get; }
        public string PdbPath { get; }

        public PEFileDebugSignature(Guid guid, uint age, string pdbPath)
        {
            this.Guid = guid;
            this.Age = age;
            this.PdbPath = pdbPath;
        }
    }

    /// <summary>
    /// Loads the PE file into memory, strips integrity bit if it must, and so on.
    /// </summary>
    /// <param name="originalBinaryPathMayBeRemote">The path to the binary - this should be a local path for perf, but that is up to the caller.  We assume it's local and read/write-able.</param>
    public unsafe PEFile(string originalBinaryPathMayBeRemote, ILogger logger)
    {
        using var taskLog = logger.StartTaskLog("Parse PE File");
        var bytes = new byte[4096];
        using (var stream = File.OpenRead(originalBinaryPathMayBeRemote))
        {
            stream.Read(bytes, offset: 0, count: bytes.Length);
        }

        fixed (byte* pBytes = bytes)
        {
            var headerPtr = new IntPtr(pBytes);
            var dosHeader = Marshal.PtrToStructure<IMAGE_DOS_HEADER>(headerPtr);
            Debug.Assert(dosHeader.isValid);

            var ntHeaderPtr = new IntPtr(pBytes + dosHeader.e_lfanew);
            var headers32 = Marshal.PtrToStructure<IMAGE_NT_HEADERS32>(ntHeaderPtr);
            this._hasForceIntegrityBitSet = headers32.OptionalHeader.DllCharacteristics.HasFlag(DllCharacteristicsType.IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY);
        }

        this.GuaranteedLocalCopyOfBinary = new GuaranteedLocalFile(originalBinaryPathMayBeRemote, taskLog, forceLocalCopy: this._hasForceIntegrityBitSet, openDeleteOnCloseStreamImmediately: false);

        if (this._hasForceIntegrityBitSet)
        {
            taskLog.Log("IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY is set, unsetting this bit to allow us to load it.");
            StripForceIntegrityBit(taskLog);
        }

        this.GuaranteedLocalCopyOfBinary.OpenDeleteOnCloseStreamIfCopiedLocally();

        this._library = LoadBinaryIntoMemory(taskLog);

        // The lower 32 bits of the handle returned by LoadLibraryEx are for use by the kernel, we should just strip them off to get the real
        // base address.
        // In experimental testing, it seems that LOAD_LIBRARY_AS_IMAGE_RESOURCE ends up adding +2 to the _library we just received, but this
        // could change for lots of reasons, so let's just mask it off.
        //
        // See here: http://blogs.msdn.com/b/oldnewthing/archive/2005/10/05/477802.aspx
        //
        this._libraryBaseAddress = (byte*)((ulong)this._library.ToInt64() & 0xFFFFFFFFFFFF0000);

        taskLog.Log($"{Path.GetFileName(this.GuaranteedLocalCopyOfBinary.OriginalPath)} library loaded at Base Address = 0x{((long)this._libraryBaseAddress):X}");

        // We do need to load _headers32 and _headers64 here, not above where we look at the headers earlier - because the
        // OptionalHeader.ImageBase will be different based on where the loader really put us in memory.  This is important for
        // when we want to do things like load vtable addresses later (we need to subtract the right _libraryPreferredLoadAddress)
        var _pntHeaders = new IntPtr(PInvokes.ImageNtHeader(this._libraryBaseAddress));

        this._headers32 = Marshal.PtrToStructure<IMAGE_NT_HEADERS32>(_pntHeaders);

        if (this._headers32.FileHeader.Machine is
            not MachineType.x64 and
            not MachineType.I386 and
            not MachineType.ARM and
            not MachineType.ARM64)
        {
            throw new InvalidOperationException($"SizeBench does not know how to deal with MachineType={this._headers32.FileHeader.Machine} binaries at this time.");
        }

        if (this._headers32.OptionalHeader.Magic == MagicType.IMAGE_NT_OPTIONAL_HDR64_MAGIC)
        {
            this.BytesPerWord = 8;
            this._headers64 = Marshal.PtrToStructure<IMAGE_NT_HEADERS64>(_pntHeaders);
            this._libraryPreferredLoadAddress = this._headers64.OptionalHeader.ImageBase;
            this.FileAlignment = this._headers64.OptionalHeader.FileAlignment;
            this.SectionAlignment = this._headers64.OptionalHeader.SectionAlignment;
            this.RsrcRange = RVARange.FromRVAAndSize(this._headers64.OptionalHeader.ResourceTable.VirtualAddress, this._headers64.OptionalHeader.ResourceTable.Size);
            taskLog.Log($"{Path.GetFileName(this.GuaranteedLocalCopyOfBinary.OriginalPath)} is 64-bit, preferred load address=0x{this._libraryPreferredLoadAddress:X}");
        }
        else
        {
            this.BytesPerWord = 4;
            this._libraryPreferredLoadAddress = this._headers32.OptionalHeader.ImageBase;
            this.FileAlignment = this._headers32.OptionalHeader.FileAlignment;
            this.SectionAlignment = this._headers32.OptionalHeader.SectionAlignment;
            this.RsrcRange = RVARange.FromRVAAndSize(this._headers32.OptionalHeader.ResourceTable.VirtualAddress, this._headers32.OptionalHeader.ResourceTable.Size);
            taskLog.Log($"{Path.GetFileName(this.GuaranteedLocalCopyOfBinary.OriginalPath)} is 32-bit, preferred load address=0x{this._libraryPreferredLoadAddress:X}");
        }

        // Parse out the various data directories in the optional header, if they're present.
        // There are 16 of them, so here we go.

        // 0. ExportTable
        // TBD - does SizeBench need to parse anything here?

        // 1. ImportTable
        ParseImportTable(taskLog);

        // 2. ResourceTable
        ParseRsrcSymbols();

        // 3. ExceptionTable
        // Exception Handling symbols are processed later because there's a careful dance of finding all ICF'd symbols at given RVAs and stuff like that before we can do this the best.
        // It happens in ParseEHSymbols, which is still called very early in startup before a Session is returned to a caller of the analysis engine, it's just not here.

        // 4. CertificateTable (aka IMAGE_DIRECTORY_ENTRY_SECURITY)
        // TBD - does SizeBench need to parse anything here?

        // 5. BaseRelocationTable
        AddDirectorySymbolIfPresent(this.BytesPerWord == 8 ? this._headers64.OptionalHeader.BaseRelocationTable : this._headers32.OptionalHeader.BaseRelocationTable, "Base Relocation Table");

        // 6. Debug
        ParseDebugDirectory(taskLog);

        // 7. Architecture
        // TBD - is this always zero as the docs say?
        Debug.Assert(this.BytesPerWord == 8 ? this._headers64.OptionalHeader.Architecture.VirtualAddress == 0 : this._headers32.OptionalHeader.Architecture.VirtualAddress == 0);

        // 8. GlobalPtr
        // TBD - does SizeBench need to parse anything here?

        // 9. TLSTable
        // TBD - does SizeBench need to parse anything here?

        // 10. LoadConfigTable
        // TBD - this requires more work as this structure has revved many times, but it has a lot of goodness in it.
        //       Tracked by Product Backlog Item 3600

        // 11. BoundImport
        // TBD - does SizeBench need to parse anything here?

        // 12. IAT
        // TBD - does SizeBench need to parse anything here?

        // 13. DelayImportDescriptor
        // TBD - does SizeBench need to parse anything here?

        // 14. CLRRuntimeHeader (aka IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR)
        // No need for SizeBench to try parsing this now as we reject managed binaries.

        // 15. Reserved
        Debug.Assert(this.BytesPerWord == 8 ? this._headers64.OptionalHeader.Reserved.VirtualAddress == 0 : this._headers32.OptionalHeader.Reserved.VirtualAddress == 0);


        // Now assemble the final RVARangeSet since all those symbols are collected.
        var otherPESymbolRanges = new List<RVARange>();

        foreach (var symbol in this.OtherPESymbols.Values)
        {
            otherPESymbolRanges.Add(new RVARange(symbol.RVA, symbol.RVAEnd));
        }

        this.OtherPESymbolsRVARanges = RVARangeSet.FromListOfRVARanges(otherPESymbolRanges, maxPaddingToMerge: 16);
    }

    private void AddDirectorySymbolIfPresent(IMAGE_DATA_DIRECTORY dataDirectory, string name)
    {
        if (dataDirectory.VirtualAddress != 0)
        {
            this.OtherPESymbols.Add(dataDirectory.VirtualAddress, new PEDirectorySymbol(dataDirectory.VirtualAddress, dataDirectory.Size, $"[PE Directory] {name}"));
        }
    }

    private unsafe void ParseImportTable(ILogger log)
    {
        // Note that we don't want to add a PEDirectorySymbol for the import table, as we parse out each import descriptor in it instead which provides a more useful
        // symbol name by having the import DLL name.
        var importTable = this.BytesPerWord == 8 ? this._headers64.OptionalHeader.ImportTable : this._headers32.OptionalHeader.ImportTable;

        if (importTable.Size == 0)
        {
            return;
        }

        // The ImportTable is an array of IMAGE_IMPORT_DESCRIPTORs.
        Debug.Assert(importTable.Size % Marshal.SizeOf<IMAGE_IMPORT_DESCRIPTOR>() == 0);

        var descriptor = (IMAGE_IMPORT_DESCRIPTOR*)GetDataMemberPtrByRVA(importTable.VirtualAddress);
        var descriptorRva = importTable.VirtualAddress;
        while (descriptorRva < importTable.VirtualAddress + importTable.Size)
        {
            if (descriptor->Name == 0 || descriptor->OriginalFirstThunk == 0)
            {
                this.OtherPESymbols.Add(descriptorRva, new ImportDescriptorSymbol(descriptorRva, "null terminator"));
                break;
            }

            // The descriptor name will be the name of the module being imported, like "kernel32.dll" or "combase.dll"
            var descriptorName = Marshal.PtrToStringAnsi(new IntPtr(GetDataMemberPtrByRVA(descriptor->Name)))!;
            this.OtherPESymbols.Add(descriptorRva, new ImportDescriptorSymbol(descriptorRva, descriptorName));
            //TODO: this should be some kind of string symbol, not PEDirectorySymbol
            this.OtherPESymbols.Add(descriptor->Name, new PEDirectorySymbol(descriptor->Name, (uint)descriptorName.Length + 1 /* null terminator */, $"`string': \"{descriptorName}\""));

            Debug.Assert((descriptor->OriginalFirstThunk != 0) == (descriptor->FirstThunk != 0));

            // The "OriginalFirstThunk" points to imports that are unresolved in memory, the "FirstThunk" points to imports that have been resolved.
            // In some cases, it seems that some binaries *do* resolve their imports even when loading the image as LOAD_LIBRARY_AS_IMAGE_RESOURCE, so
            // we walk the OriginalFirstThunk list.
            // In the future we may want to also record the RVAs from the FirstThunk list, but be careful to test a broad range of binaries if so, and for
            // now it seems DIA can see those symbols so this isn't necessary to fill all the gaps.
            if (descriptor->OriginalFirstThunk != 0)
            {
                var thunkRva = descriptor->OriginalFirstThunk;
                while (true)
                {
                    Debug.Assert(this.BytesPerWord is 8 or 4);
                    var thunk64 = this.BytesPerWord is 8 ? (IMAGE_THUNK_DATA64*)GetDataMemberPtrByRVA(thunkRva) : null;
                    var thunk32 = this.BytesPerWord is not 8 ? (IMAGE_THUNK_DATA32*)GetDataMemberPtrByRVA(thunkRva) : null;
                    var thunkSize = this.BytesPerWord is 8 ? (uint)Marshal.SizeOf<IMAGE_THUNK_DATA64>() : (uint)Marshal.SizeOf<IMAGE_THUNK_DATA32>();

                    var ordinal = (ushort)((this.BytesPerWord is 8 ? thunk64->Ordinal : thunk32->Ordinal) & 0xFFFF);
                    var isOrdinalOnly = this.BytesPerWord is 8 ? (thunk64->Ordinal & (1ul << 63)) > 0 : (thunk32->Ordinal & (1 << 31)) > 0; // If the high bit is set, this is not a named entry, it is ordinal-only
                    var addressOfData = this.BytesPerWord is 8 ? (uint)thunk64->AddressOfData : thunk32->AddressOfData;

                    if (ordinal == 0)
                    {
                        this.OtherPESymbols.Add(thunkRva, new ImportThunkSymbol(thunkRva, thunkSize, 0, descriptorName, "null terminator"));
                        break;
                    }
                    else if (isOrdinalOnly)
                    {
                        this.OtherPESymbols.Add(thunkRva, new ImportThunkSymbol(thunkRva, thunkSize, ordinal, descriptorName, null));
                    }
                    else
                    {
                        var importByNamePtr = GetDataMemberPtrByRVA(addressOfData);
                        // First 2 bytes are the 'hint' (ordinal)
                        var hint = *(ushort*)(importByNamePtr);
                        importByNamePtr += 2;
                        var thunkName = Marshal.PtrToStringAnsi(new IntPtr(importByNamePtr))!;
                        this.OtherPESymbols.Add(thunkRva, new ImportThunkSymbol(thunkRva, thunkSize, hint, descriptorName, thunkName));
                        this.OtherPESymbols.Add(addressOfData, new ImportByNameSymbol(addressOfData, (uint)thunkName.Length + 1 /* null terminator */ + 2 /* hint ushort */, hint, descriptorName, thunkName));
                    }

                    thunkRva += thunkSize;
                }
            }


            descriptor++;
            descriptorRva += (uint)Marshal.SizeOf<IMAGE_IMPORT_DESCRIPTOR>();
        }
    }

    private unsafe void ParseDebugDirectory(ILogger log)
    {
        AddDirectorySymbolIfPresent(this.BytesPerWord == 8 ? this._headers64.OptionalHeader.Debug : this._headers32.OptionalHeader.Debug, "Debug");

        //TODO: see if this P/Invoke can be removed and we can just use the OptionalHeader.Debug RVA?
        PInvokes.ImageDirectoryEntryToDataEx(this._libraryBaseAddress, false, IMAGE_DIRECTORY_ENTRY.Debug, out _, out var headerPtr);

        if (headerPtr == IntPtr.Zero)
        {
            // No debug directories found, so we'll accept whatever PDB the user gives us - hopefully it matches!
            // This means PEFile.DebugSignature will be null.
            log.Log("Unable to find IMAGE_DIRECTORY_ENTRY.Debug - we'll do the best we can, but no signature match will be performed.");
            return;
        }

        unsafe
        {
            var numDirectories = this.BytesPerWord == 8 ? this._headers64.OptionalHeader.Debug.Size / Marshal.SizeOf<IMAGE_DEBUG_DIRECTORY>() : this._headers32.OptionalHeader.Debug.Size / Marshal.SizeOf<IMAGE_DEBUG_DIRECTORY>();
            this.DebugDirectories.Capacity = (int)numDirectories;
            log.Log($"Found {numDirectories} IMAGE_DEBUG_DIRECTORY entries");

            var pDebugDirectory = this._libraryBaseAddress + (this.BytesPerWord == 8 ? this._headers64.OptionalHeader.Debug.VirtualAddress : this._headers32.OptionalHeader.Debug.VirtualAddress);
            var debugDirectory = Marshal.PtrToStructure<IMAGE_DEBUG_DIRECTORY>((IntPtr)pDebugDirectory);
            var sizeOfDebugDirectory = Marshal.SizeOf<IMAGE_DEBUG_DIRECTORY>();

            this.DebugDirectories.Add(debugDirectory);
            this.OtherPESymbols.Add(debugDirectory.AddressOfRawData, new PEDirectorySymbol(debugDirectory.AddressOfRawData, debugDirectory.SizeOfData, $"[Debug Directory] {debugDirectory.Type}"));

            for (var i = 1; i < numDirectories; i++)
            {
                pDebugDirectory += sizeOfDebugDirectory;
                debugDirectory = Marshal.PtrToStructure<IMAGE_DEBUG_DIRECTORY>((IntPtr)pDebugDirectory);
                this.DebugDirectories.Add(debugDirectory);

                // Some debug directories have 0 size, such as ILTCG if ILTCG is turned on but no incremental linking actually happens.  Zero size isn't important for SizeBench anyway as it's all
                // about visibility of size.
                if (debugDirectory.SizeOfData != 0)
                {
                    this.OtherPESymbols.Add(debugDirectory.AddressOfRawData, new PEDirectorySymbol(debugDirectory.AddressOfRawData, debugDirectory.SizeOfData, $"[Debug Directory] {debugDirectory.Type}"));
                }
            }

            foreach (var directory in this.DebugDirectories)
            {
                if (directory.Type == IMAGE_DEBUG_TYPE.CodeView)
                {
                    log.Log("Found CodeView debug directory, looking for RSDS data");

                    var rsdsPtr = new IntPtr(this._libraryBaseAddress + directory.AddressOfRawData);

                    if (Marshal.ReadInt32(rsdsPtr) == (int)IMAGE_DEBUG_TYPE_MAGIC.RSDS_SIGNATURE)
                    {
                        log.Log("Found RSDS magic signature");
                        var rsdsInfo = Marshal.PtrToStructure<RSDS_DEBUG_FORMAT>(rsdsPtr);

                        var pathPtr = new IntPtr(rsdsPtr.ToInt64() + Marshal.SizeOf(typeof(RSDS_DEBUG_FORMAT)));
                        var path = Marshal.PtrToStringAnsi(pathPtr) ?? String.Empty;

                        this.DebugSignature = new PEFileDebugSignature(new Guid(rsdsInfo.Guid), rsdsInfo.Age, path);

                        log.Log($"DebugSignature Guid={this.DebugSignature.Guid}, Age={this.DebugSignature.Age}, Path={this.DebugSignature.PdbPath}");
                    }
                    break;
                }
            }
        }
    }

    private unsafe IntPtr LoadBinaryIntoMemory(ILogger taskLog)
    {
        var library = PInvokes.LoadLibraryExW(this.GuaranteedLocalCopyOfBinary.GuaranteedLocalPath, IntPtr.Zero, PInvokes.LoadLibraryFlags.LOAD_LIBRARY_AS_IMAGE_RESOURCE | PInvokes.LoadLibraryFlags.LOAD_IGNORE_CODE_AUTHZ_LEVEL);

        if (library == IntPtr.Zero)
        {
            taskLog.Log($"LoadLibraryEx failed for {this.GuaranteedLocalCopyOfBinary.GuaranteedLocalPath}{(this.GuaranteedLocalCopyOfBinary.CopiedLocally ? " (original path: " + this.GuaranteedLocalCopyOfBinary.OriginalPath + ")" : "")}", LogLevel.Error);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return library;
    }

    private void StripForceIntegrityBit(ILogger taskLog)
    {
        using (var memoryMappedFile = MemoryMappedFile.CreateFromFile(this.GuaranteedLocalCopyOfBinary.GuaranteedLocalPath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        {
            IMAGE_DOS_HEADER dosHeader;
            using (var dosHeaderStream = memoryMappedFile.CreateViewStream(offset: 0, size: Marshal.SizeOf<IMAGE_DOS_HEADER>()))
            using (var dosHeaderReader = new BinaryReader(dosHeaderStream))
            {
                var dosHeaderBytes = dosHeaderReader.ReadBytes(Marshal.SizeOf<IMAGE_DOS_HEADER>());
                var handle = GCHandle.Alloc(dosHeaderBytes, GCHandleType.Pinned);
                dosHeader = Marshal.PtrToStructure<IMAGE_DOS_HEADER>(handle.AddrOfPinnedObject());
                Debug.Assert(dosHeader.isValid);
            }

            // Conveniently, the DllCharacteristics bit that we're interested in unsetting is in a part of the optional header that's at the same
            // offset for 32-bit and 64-bit binaries so we'll just use the 32-bit header structure and it'll be ok.
            IMAGE_NT_HEADERS32 headers32;
            using (var ntHeadersStream = memoryMappedFile.CreateViewStream(offset: dosHeader.e_lfanew, size: Marshal.SizeOf<IMAGE_NT_HEADERS32>()))
            using (var ntHeadersReader = new BinaryReader(ntHeadersStream))
            using (var ntHeadersWriter = new BinaryWriter(ntHeadersStream))
            {
                var headers32Bytes = ntHeadersReader.ReadBytes(Marshal.SizeOf<IMAGE_NT_HEADERS32>());
                var handle = GCHandle.Alloc(headers32Bytes, GCHandleType.Pinned);
                headers32 = Marshal.PtrToStructure<IMAGE_NT_HEADERS32>(handle.AddrOfPinnedObject());

                // We should have only ever gotten into this function if this bit is set.
                Debug.Assert(headers32.OptionalHeader.DllCharacteristics.HasFlag(DllCharacteristicsType.IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY));

                // Seek to the beginning of the OptionalHeader first
                ntHeadersWriter.Seek(Marshal.OffsetOf<IMAGE_NT_HEADERS32>(nameof(IMAGE_NT_HEADERS32.OptionalHeader)).ToInt32(), SeekOrigin.Begin);
                // Then seek into the DllCharacteristics field
                ntHeadersWriter.Seek(Marshal.OffsetOf<IMAGE_OPTIONAL_HEADER32>(nameof(IMAGE_OPTIONAL_HEADER32.DllCharacteristics)).ToInt32(), SeekOrigin.Current);
                var dllCharacteristicsWithoutForceIntegrity = (ushort)((ushort)headers32.OptionalHeader.DllCharacteristics & (ushort)~DllCharacteristicsType.IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY);
                ntHeadersWriter.Write(dllCharacteristicsWithoutForceIntegrity);
            }
        }

        // Calculate the new checksum since we changed the header
        var result = PInvokes.MapFileAndCheckSumW(this.GuaranteedLocalCopyOfBinary.GuaranteedLocalPath, out var _ /* originalChecksum - unused */, out var newChecksum);

        if (result != PInvokes.MapFileAndCheckSumWResult.CHECKSUM_SUCCESS)
        {
            // Sometimes even if we fail to change the checksum we can still load things, so we'll log this as a warning and just continue trying to
            // load stuff into memory.  Maybe it'll work.
            taskLog.Log($"Unable to change the checksum after writing out the change to IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY.  Error was: {result}", LogLevel.Warning);
            return;
        }

        // Write the new checksum into the file
        using (var memoryMappedFile = MemoryMappedFile.CreateFromFile(this.GuaranteedLocalCopyOfBinary.GuaranteedLocalPath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        {
            IMAGE_DOS_HEADER dosHeader;
            using (var dosHeaderStream = memoryMappedFile.CreateViewStream(offset: 0, size: Marshal.SizeOf<IMAGE_DOS_HEADER>()))
            using (var dosHeaderReader = new BinaryReader(dosHeaderStream))
            {
                var dosHeaderBytes = dosHeaderReader.ReadBytes(Marshal.SizeOf<IMAGE_DOS_HEADER>());
                var handle = GCHandle.Alloc(dosHeaderBytes, GCHandleType.Pinned);
                dosHeader = Marshal.PtrToStructure<IMAGE_DOS_HEADER>(handle.AddrOfPinnedObject());
                Debug.Assert(dosHeader.isValid);
            }

            // Conveniently, the CheckSum field that we're interested in unsetting is in a part of the optional header that's at the same
            // offset for 32-bit and 64-bit binaries so we'll just use the 32-bit header structure and it'll be ok.
            IMAGE_NT_HEADERS32 headers32;
            using (var ntHeadersStream = memoryMappedFile.CreateViewStream(offset: dosHeader.e_lfanew, size: Marshal.SizeOf<IMAGE_NT_HEADERS32>()))
            using (var ntHeadersReader = new BinaryReader(ntHeadersStream))
            using (var ntHeadersWriter = new BinaryWriter(ntHeadersStream))
            {
                var headers32Bytes = ntHeadersReader.ReadBytes(Marshal.SizeOf<IMAGE_NT_HEADERS32>());
                var handle = GCHandle.Alloc(headers32Bytes, GCHandleType.Pinned);
                headers32 = Marshal.PtrToStructure<IMAGE_NT_HEADERS32>(handle.AddrOfPinnedObject());

                // By the time we get here, this bit should be unset!
                Debug.Assert(false == headers32.OptionalHeader.DllCharacteristics.HasFlag(DllCharacteristicsType.IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY));

                // Seek to the beginning of the OptionalHeader first
                ntHeadersWriter.Seek(Marshal.OffsetOf<IMAGE_NT_HEADERS32>(nameof(IMAGE_NT_HEADERS32.OptionalHeader)).ToInt32(), SeekOrigin.Begin);
                // Then seek into the CheckSum field
                ntHeadersWriter.Seek(Marshal.OffsetOf<IMAGE_OPTIONAL_HEADER32>(nameof(IMAGE_OPTIONAL_HEADER32.CheckSum)).ToInt32(), SeekOrigin.Current);
                ntHeadersWriter.Write(newChecksum);
            }
        }
    }

    /// <summary>
    /// Parses the table of Exception Handling symbols in the binary, referred to as PDATA (Procedure Data) and
    /// XDATA (eXception Data).
    /// </summary>
    /// <param name="XDataRVARange">If the caller can determine the range of the XDATA (probably by using DIA SymTagCoffGroups),
    /// then we'll use that, and in debug builds will even verify that every xdata symbol discovered fits into that range.
    /// But if the caller cannot determine this ahead of time, just pass null and we'll estimate the XDataRange on the way
    /// out in the parse result - it'll be imperfect since xdata alignment requirements are not recorded in the binary or
    /// PDB so we have to guess.</param>
    /// <returns>A structure holding the PDATA and XDATA symbols discovered, and the RVA ranges they fit in.</returns>
    public void ParseEHSymbols(Session session, IDIAAdapter diaAdapter, RVARange? XDataRVARange, ILogger logger)
    {
        unsafe
        {
            EHSymbolTable.Parse(this._libraryBaseAddress, this.SectionAlignment, session.DataCache, diaAdapter, this._headers32.FileHeader.Machine, XDataRVARange, logger);
        }

        if (session.DataCache.PDataSymbolsByRVA is null || session.DataCache.XDataSymbolsByRVA is null)
        {
            throw new InvalidOperationException("After finishing parsing EH symbols, we somehow haven't assigned the PDATA/XDATA symbols - this is incorrect, and a bug in SizeBench's implementation, not your usage of it.");
        }
    }

    #region RSRC symbols

    private void ParseRsrcSymbols()
    {
        unsafe
        {
            if (this.RsrcRange.RVAStart != 0)
            {
                var stringTables = new SortedList<uint, RsrcStringTableDataSymbol>();
                WalkResourceDirectory(this.RsrcRange.RVAStart, ref stringTables);

                // We want to group together all the string tables that are adjacent to each other to reduce noise in this, since
                // it's essentially an implementation detail that 16 strings are stored per STRINGTABLE - if a binary has a bunch
                // of strings they're probably right next to each other so this reduces the number of symbols for a user to see and
                // care about, and the number of symbols to diff.

                if (stringTables.Count > 0)
                {
                    var contiguousStringTables = new List<RsrcStringTableDataSymbol>();
                    foreach (var stringTableKVP in stringTables)
                    {
                        if (contiguousStringTables.Count == 0 ||
                            (RoundUpTo8ByteAlignment(contiguousStringTables[^1].RVAEnd) == stringTableKVP.Key &&
                             contiguousStringTables[^1].Language == stringTableKVP.Value.Language))
                        {
                            contiguousStringTables.Add(stringTableKVP.Value);
                        }
                        else
                        {
                            var group = new RsrcGroupStringTablesDataSymbol(contiguousStringTables);
                            this.RsrcSymbols.Add(group.RVA, group);
                            contiguousStringTables = new List<RsrcStringTableDataSymbol>();
                        }
                    }

                    if (contiguousStringTables.Count > 0)
                    {
                        var group = new RsrcGroupStringTablesDataSymbol(contiguousStringTables);
                        this.RsrcSymbols.Add(group.RVA, group);
                    }
                }
            }
        }
    }

    private unsafe void WalkResourceDirectory(uint directoryRVAStart, ref SortedList<uint, RsrcStringTableDataSymbol> stringTables, uint depth = 0, IMAGE_RESOURCE_DIRECTORY_ENTRY? depth0 = null, IMAGE_RESOURCE_DIRECTORY_ENTRY? depth1 = null)
    {
        if (depth > 2)
        {
            // SizeBench doesn't know how to navigate arbitrary rsrc sections defined in the PE spec, since there's very little defined there.
            // By convention, Windows and rc.exe only use 3 levels (depth 0, 1, and 2) so if we get deeper than that we'll give up.
            return;
        }

        var rsrcSectionStart = this._libraryBaseAddress + this.RsrcRange.RVAStart;

        var directory = Marshal.PtrToStructure<IMAGE_RESOURCE_DIRECTORY>(new IntPtr(GetDataMemberPtrByRVA(directoryRVAStart)));
        var directorySize = (uint)Marshal.SizeOf<IMAGE_RESOURCE_DIRECTORY>() +
                            (uint)(Marshal.SizeOf<IMAGE_RESOURCE_DIRECTORY_ENTRY>() * (directory.NumberOfIdEntries + directory.NumberOfNamedEntries));

        Win32ResourceType rsrcType;
        string rsrcTypeName;
        if (depth0 != null && Enum.IsDefined(typeof(Win32ResourceType), (int)depth0.Value.ID))
        {
            rsrcType = (Win32ResourceType)depth0.Value.ID;
            rsrcTypeName = rsrcType.ToString();
        }
        else if (depth0 != null)
        {
            rsrcType = depth0.Value.IsNamedEntry == true ? Win32ResourceType.UserNamedResource : Win32ResourceType.Unknown;
            rsrcTypeName = depth0.Value.NameString(rsrcSectionStart);
        }
        else
        {
            rsrcType = Win32ResourceType.Unknown;
            rsrcTypeName = rsrcType.ToString();
        }

        var depth1NameAsString = depth1?.NameString(rsrcSectionStart);

        this.RsrcSymbols.Add(directoryRVAStart, new RsrcDirectorySymbol(directoryRVAStart, directorySize, depth, rsrcType, rsrcTypeName, depth1NameAsString));

        var entryRVAStart = directoryRVAStart + (uint)Marshal.SizeOf<IMAGE_RESOURCE_DIRECTORY>();
        for (uint i = 0; i < directory.NumberOfIdEntries + directory.NumberOfNamedEntries; i++)
        {
            var entry = Marshal.PtrToStructure<IMAGE_RESOURCE_DIRECTORY_ENTRY>(new IntPtr(GetDataMemberPtrByRVA(entryRVAStart)));
            if (entry.IsNamedEntry)
            {
                var str = entry.NameString(rsrcSectionStart);
                var stringRVA = this.RsrcRange.RVAStart + entry.NameOffset;
                // Length is 2 bytes telling us how long the string is, and then 2 bytes per character since they're unicode
                // It's possible that we discover the same string at various levels of the resource directory - if so, this is
                // harmless, so we use TryAdd here to skip attempting to add multiple times and throwing due to duplicate keys.
                this.RsrcSymbols.TryAdd(stringRVA, new RsrcStringSymbol(stringRVA, (uint)(2 + (str.Length * 2)), str));
            }

            if (entry.DataIsDirectory)
            {
                if (depth == 0)
                {
                    depth0 = entry;
                }
                else if (depth == 1)
                {
                    depth1 = entry;
                }

                WalkResourceDirectory(this.RsrcRange.RVAStart + entry.OffsetToDirectory, ref stringTables, depth + 1, depth0, depth1);
            }
            else
            {
                var dataEntryRVAStart = this.RsrcRange.RVAStart + entry.OffsetToData;
                var dataEntry = Marshal.PtrToStructure<IMAGE_RESOURCE_DATA_ENTRY>(new IntPtr(GetDataMemberPtrByRVA(dataEntryRVAStart)));

                // By convention, the directory entry's ID is the language ID (LCID).  We try to resolve this to a friendly name but if the app is using a custom
                // culture or something, we'll fall back to just the ID in hex.
                string languageName;
                try
                {
                    if (entry.ID == 0)
                    {
                        languageName = "LANG_NEUTRAL";
                    }
                    else
                    {
                        languageName = CultureInfo.GetCultureInfo((int)entry.ID).DisplayName;
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    languageName = $"Unknown language";
                }
                catch (CultureNotFoundException)
                {
                    languageName = $"Unknown language";
                }

                var depth1NameAsStringWithFallback = depth1NameAsString ?? "<unknown rsrc name>";

                this.RsrcSymbols.Add(dataEntryRVAStart, new RsrcDataEntrySymbol(dataEntryRVAStart, (uint)Marshal.SizeOf<IMAGE_RESOURCE_DATA_ENTRY>(), depth, languageName, rsrcType, rsrcTypeName, depth1NameAsStringWithFallback, i));

                var dataSymbol = CreateRsrcDataSymbol(languageName, rsrcType, rsrcTypeName, depth1NameAsStringWithFallback, dataEntry);

                // We may get back null if we found an icon, but we already put it into an icon group.
                if (dataSymbol != null)
                {
                    if (dataSymbol is RsrcStringTableDataSymbol stringTable)
                    {
                        stringTables.Add(stringTable.RVA, stringTable);
                    }
                    else
                    {
                        this.RsrcSymbols.Add(dataSymbol.RVA, dataSymbol);
                    }
                }
            }

            entryRVAStart += (uint)Marshal.SizeOf<IMAGE_RESOURCE_DIRECTORY_ENTRY>();
        }
    }

    private unsafe RsrcDataSymbol? CreateRsrcDataSymbol(string languageName, Win32ResourceType rsrcType, string rsrcTypeName, string depth1NameAsString, IMAGE_RESOURCE_DATA_ENTRY dataEntry)
    {
        // ICONs are complicated, because they consist of a GROUP_ICON which is a data structure that describes a collection of ICONs, all of which
        // collectively make up what the user supplied in the .ico file.
        // See these two blog posts:
        //      https://devblogs.microsoft.com/oldnewthing/20120720-00/?p=7083
        //      https://devblogs.microsoft.com/oldnewthing/20101018-00/?p=12513
        // So when we find a GROUP_ICON, to make the output of SizeBench correlate to what users actually specify we'll parse this out specially.
        // Note that we may encounter the ICON resources before we get here, in which case we'd want to remove those from the final list of rsrcSymbols
        // so we don't count things twice.  But we may also encounter the ICON resources after the GROUP_ICON in which case we want to just ignore them,
        // so ordering is tricky here.
        return rsrcType switch
        {
            Win32ResourceType.GROUP_ICON => CreateGroupIconSymbol(languageName, depth1NameAsString, dataEntry),
            Win32ResourceType.ICON => null /* we'll find the GROUP_ICON and attribute there */,
            Win32ResourceType.STRINGTABLE => CreateStringTableSymbol(languageName, depth1NameAsString, dataEntry),
            Win32ResourceType.CURSOR => null /* we'll find the GROUP_CURSOR and attribute there */,
            Win32ResourceType.GROUP_CURSOR => CreateGroupCursorSymbol(languageName, depth1NameAsString, dataEntry),
            Win32ResourceType.FONTDIR => new RsrcDataSymbol(dataEntry.OffsetToData, dataEntry.Size, languageName, rsrcType, rsrcTypeName, depth1NameAsString),
            Win32ResourceType.FONT => new RsrcDataSymbol(dataEntry.OffsetToData, dataEntry.Size, languageName, rsrcType, rsrcTypeName, depth1NameAsString),
            _ => new RsrcDataSymbol(dataEntry.OffsetToData, dataEntry.Size, languageName, rsrcType, rsrcTypeName, depth1NameAsString),
        };
    }

    private unsafe RsrcGroupIconDataSymbol CreateGroupIconSymbol(string languageName, string depth1NameAsString, IMAGE_RESOURCE_DATA_ENTRY dataEntry)
    {
        var iconDir = Marshal.PtrToStructure<NEWHEADER>(new IntPtr(GetDataMemberPtrByRVA(dataEntry.OffsetToData)));
        var iconDirEntries = new ICONRESDIR[iconDir.idCount];
        var iconRVA = dataEntry.OffsetToData;
        var totalSizeOfGroupIcon = (uint)(Marshal.SizeOf<NEWHEADER>() +
                                           (Marshal.SizeOf<ICONRESDIR>() * iconDir.idCount));

        Debug.Assert(dataEntry.Size == totalSizeOfGroupIcon);

        var icons = new List<RsrcIconDataSymbol>(capacity: iconDir.idCount);

        // The icons get written out before the GROUP_ICON, so we walk them in reverse order to calculate their RVAs
        for (var iconEntryIdx = iconDir.idCount - 1; iconEntryIdx >= 0; iconEntryIdx--)
        {
            iconDirEntries[iconEntryIdx] = Marshal.PtrToStructure<ICONRESDIR>(new IntPtr(GetDataMemberPtrByRVA(dataEntry.OffsetToData + Marshal.SizeOf<NEWHEADER>() + (iconEntryIdx * Marshal.SizeOf<ICONRESDIR>()))));

            // Every icon is 8-byte aligned, so we need to round this up to the nearest multiple of 8 when we are walking backwards
            var sizeOfIconRoundedUpToNearest8ByteAlignment = RoundUpTo8ByteAlignment(iconDirEntries[iconEntryIdx].dwBytesInRes);
            iconRVA -= sizeOfIconRoundedUpToNearest8ByteAlignment;

            ushort width = iconDirEntries[iconEntryIdx].bWidth;
            if (width == 0)
            {
                width = 256; // As of Windows XP the value '0' now means 256 since it's just one byte
            }

            ushort height = iconDirEntries[iconEntryIdx].bHeight;
            if (height == 0)
            {
                height = 256;
            }

            var bpp = iconDirEntries[iconEntryIdx].wBitCount;

            icons.Insert(0, new RsrcIconDataSymbol(iconRVA, iconDirEntries[iconEntryIdx].dwBytesInRes, languageName, Win32ResourceType.ICON, "ICON", $"#{iconDirEntries[iconEntryIdx].nID}", width, height, bpp));
            totalSizeOfGroupIcon += sizeOfIconRoundedUpToNearest8ByteAlignment;
        }


        return new RsrcGroupIconDataSymbol(totalSizeOfGroupIcon, languageName, depth1NameAsString, icons);
    }

    private unsafe RsrcGroupCursorDataSymbol CreateGroupCursorSymbol(string languageName, string depth1NameAsString, IMAGE_RESOURCE_DATA_ENTRY dataEntry)
    {
        var cursorDir = Marshal.PtrToStructure<NEWHEADER>(new IntPtr(GetDataMemberPtrByRVA(dataEntry.OffsetToData)));
        var cursorDirEntries = new CURSORRESDIR[cursorDir.idCount];
        var cursorRVA = dataEntry.OffsetToData;
        var totalSizeOfGroupCursor = (uint)(Marshal.SizeOf<NEWHEADER>() +
                                            (Marshal.SizeOf<CURSORRESDIR>() * cursorDir.idCount));

        Debug.Assert(dataEntry.Size == totalSizeOfGroupCursor);

        var cursors = new List<RsrcCursorDataSymbol>(capacity: cursorDir.idCount);

        // The icons get written out before the GROUP_CURSOR, so we walk them in reverse order to calculate their RVAs
        for (var cursorEntryIdx = cursorDir.idCount - 1; cursorEntryIdx >= 0; cursorEntryIdx--)
        {
            cursorDirEntries[cursorEntryIdx] = Marshal.PtrToStructure<CURSORRESDIR>(new IntPtr(GetDataMemberPtrByRVA(dataEntry.OffsetToData + Marshal.SizeOf<NEWHEADER>() + (cursorEntryIdx * Marshal.SizeOf<CURSORRESDIR>()))));

            // Every cursor is 8-byte aligned, so we need to round this up to the nearest multiple of 8 when we are walking backwards
            var sizeOfCursorRoundedUpToNearest8ByteAlignment = RoundUpTo8ByteAlignment(cursorDirEntries[cursorEntryIdx].dwBytesInRes);
            cursorRVA -= sizeOfCursorRoundedUpToNearest8ByteAlignment;

            var width = cursorDirEntries[cursorEntryIdx].wWidth;
            // Why divide the height by 2?  Explained here: https://devblogs.microsoft.com/oldnewthing/20101019-00/?p=12503
            // "...the bmWidth is the width of the image and bmHeight is double the height of the image, followed by the bitmap color table, 
            // followed by the image pixels, followed by the mask pixels."
            var height = (ushort)(cursorDirEntries[cursorEntryIdx].wHeight / 2);
            var bpp = cursorDirEntries[cursorEntryIdx].wBitCount;

            cursors.Insert(0, new RsrcCursorDataSymbol(cursorRVA, cursorDirEntries[cursorEntryIdx].dwBytesInRes, languageName, $"#{cursorDirEntries[cursorEntryIdx].nID}", width, height, bpp));
            totalSizeOfGroupCursor += sizeOfCursorRoundedUpToNearest8ByteAlignment;
        }

        return new RsrcGroupCursorDataSymbol(totalSizeOfGroupCursor, languageName, depth1NameAsString, cursors);
    }

    private unsafe RsrcDataSymbol CreateStringTableSymbol(string languageName, string depth1NameAsString, IMAGE_RESOURCE_DATA_ENTRY dataEntry)
    {
        var strTable = (ushort*)GetDataMemberPtrByRVA(dataEntry.OffsetToData);
        var end = (byte*)strTable + dataEntry.Size;

        var strings = new List<string>();

        while (strTable < end)
        {
            var len = *strTable;
            strTable++; // Eat up the 'len'
#pragma warning disable CA1508 // Avoid dead conditional code - the analyzer can't seem to see that a pointer deref could make this any value.
            if (len != 0)
#pragma warning restore CA1508 // Avoid dead conditional code
            {
                Debug.Assert(strTable + len <= end);
                strings.Add(Marshal.PtrToStringUni(new IntPtr(strTable), len));
                strTable += len;
            }
        }

        return new RsrcStringTableDataSymbol(dataEntry.OffsetToData, dataEntry.Size, languageName, depth1NameAsString, strings);
    }

    private static uint RoundUpTo8ByteAlignment(uint val)
    {
        if (val % 8 == 0)
        {
            return val;
        }

        return val + (8 - (val % 8));
    }

    #endregion

    private unsafe byte* GetDataMemberPtrByRVA(long RVA) => this._libraryBaseAddress + RVA;

    public uint LoadUInt32ByRVAThatIsPreferredBaseRelative(long RVA)
    {
        uint retVal = 0;
        unsafe
        {
            var bpRVA = GetDataMemberPtrByRVA(RVA);
            var int32pRVA = (uint*)bpRVA;
            retVal = *int32pRVA;
        }

        return (uint)(retVal - this._libraryPreferredLoadAddress);
    }

    [Flags]
    private enum IsTextUnicodeFlags : int
    {
        IS_TEXT_UNICODE_ASCII16 = 0x0001,
        IS_TEXT_UNICODE_REVERSE_ASCII16 = 0x0010,

        IS_TEXT_UNICODE_STATISTICS = 0x0002,
        IS_TEXT_UNICODE_REVERSE_STATISTICS = 0x0020,

        IS_TEXT_UNICODE_CONTROLS = 0x0004,
        IS_TEXT_UNICODE_REVERSE_CONTROLS = 0x0040,

        IS_TEXT_UNICODE_SIGNATURE = 0x0008,
        IS_TEXT_UNICODE_REVERSE_SIGNATURE = 0x0080,

        IS_TEXT_UNICODE_ILLEGAL_CHARS = 0x0100,
        IS_TEXT_UNICODE_ODD_LENGTH = 0x0200,
        IS_TEXT_UNICODE_DBCS_LEADBYTE = 0x0400,
        IS_TEXT_UNICODE_NULL_BYTES = 0x1000,

        IS_TEXT_UNICODE_UNICODE_MASK = 0x000F,
        IS_TEXT_UNICODE_REVERSE_MASK = 0x00F0,
        IS_TEXT_UNICODE_NOT_UNICODE_MASK = 0x0F00,
        IS_TEXT_UNICODE_NOT_ASCII_MASK = 0xF000
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("Advapi32", CallingConvention = CallingConvention.Cdecl, EntryPoint = "IsTextUnicode", SetLastError = false, ExactSpelling = true)]
    private static extern unsafe bool IsTextUnicode(byte* buf, int len, ref IsTextUnicodeFlags opt);

    public string LoadStringByRVA(long RVA, ulong length, out bool isUnicodeString)
    {
        unsafe
        {
            var pRVA = GetDataMemberPtrByRVA(RVA);

            // We select all the flags, to try every test possible to discount this as Unicode
            var flags = (IsTextUnicodeFlags)0xFFFF;
            isUnicodeString = IsTextUnicode(pRVA, (int)length, ref flags);

            if (isUnicodeString)
            {
                // Not all strings are null-terminated, but for those that are we don't want the null terminator in the output so we'll cut that off
                if (*(char*)(pRVA + length - 2) == 0)
                {
                    return Marshal.PtrToStringUni((IntPtr)pRVA, (int)(length - 2) / 2) ?? String.Empty;
                }
                else
                {
                    return Marshal.PtrToStringUni((IntPtr)pRVA, (int)length / 2) ?? String.Empty;
                }
            }
            else
            {
                // Not all strings are null-terminated, but for those that are we don't want the null terminator in the output so we'll cut that off
                if (*(sbyte*)(pRVA + length - 1) == 0)
                {
                    return Marshal.PtrToStringAnsi((IntPtr)pRVA, (int)length - 1) ?? String.Empty;
                }
                else
                {
                    return Marshal.PtrToStringAnsi((IntPtr)pRVA, (int)length) ?? String.Empty;
                }
            }
        }
    }

    public bool CompareData(long RVA1, long RVA2, uint length)
    {
        unsafe
        {
            var ptr1 = GetDataMemberPtrByRVA(RVA1);
            var ptr2 = GetDataMemberPtrByRVA(RVA2);
            for (var i = 0; i < length; i++)
            {
                var val1 = ptr1 + i;
                var val2 = ptr2 + i;
                if (*val1 != *val2)
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal float CompareSimilarityOfBytesInBinary(IReadOnlyList<RVARange> ranges1, IReadOnlyList<RVARange> ranges2)
    {
        long bytesSame = 0;
        long bytesCompared = 0;

        if (ranges1.Count != ranges2.Count)
        {
            throw new ArgumentException("The two passed-in lists of RVA Ranges must be of equal length.  This is a bug in SizeBench's implementation, not your use of it", nameof(ranges1));
        }

        unsafe
        {
            for (var i = 0; i < ranges1.Count; i++)
            {
                var ptr1 = GetDataMemberPtrByRVA(ranges1[i].RVAStart);
                var ptr2 = GetDataMemberPtrByRVA(ranges2[i].RVAStart);
                long lengthToCompare = Math.Min(ranges1[i].Size, ranges2[i].Size);
                for (var byteIndex = 0; byteIndex < lengthToCompare; byteIndex++)
                {
                    var val1 = ptr1 + byteIndex;
                    var val2 = ptr2 + byteIndex;
                    if (*val1 == *val2)
                    {
                        bytesSame++;
                    }
                }
                bytesCompared += Math.Max(ranges1[i].Size, ranges2[i].Size);
            }
        }

        return bytesSame / (float)bytesCompared;
    }

    #region IDisposable Support

    private bool _isDisposed; // To detect redundant calls

    private void Dispose(bool _)
    {
        if (!this._isDisposed)
        {
            PInvokes.FreeLibrary(this._library);

            this.GuaranteedLocalCopyOfBinary?.Dispose();

            this._isDisposed = true;
        }
    }

    ~PEFile()
    {
        // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        Dispose(false);
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
