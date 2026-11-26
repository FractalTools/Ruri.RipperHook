using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Native image bounds and the function portions described by its exception directory. Leaf functions need not
/// have unwind entries; chained portions describe shared unwind state, not necessarily cold or disjoint code.
/// </summary>
internal sealed class Il2CppFunctionTable
{
    private const int EntrySize = 12;
    private const byte ChainedUnwind = 4;
    private const int UnwindHeaderSize = 4;
    private const int ChainDepth = 8;

    private static Il2CppFunctionTable _built;
    private static ApplicationAnalysisContext _builtFor;

    private readonly ulong[] _begin;
    private readonly ulong[] _end;
    private readonly ulong[] _root;
    private readonly Dictionary<ulong, List<int>> _chunksByRoot;
    private readonly Section[] _sections;

    private readonly record struct Section(ulong Start, ulong End, long RawOffset, bool Executable, bool Writable);

    private Il2CppFunctionTable(ulong imageBase, ulong[] begin, ulong[] end, ulong[] root, Section[] sections = null)
    {
        ImageBase = imageBase;
        _begin = begin;
        _end = end;
        _root = root;
        _sections = sections ?? Array.Empty<Section>();
        _chunksByRoot = new Dictionary<ulong, List<int>>();
        for (int index = 0; index < begin.Length; index++)
        {
            if (root[index] == begin[index])
            {
                continue;
            }
            if (!_chunksByRoot.TryGetValue(root[index], out List<int> chunks))
            {
                chunks = new List<int>();
                _chunksByRoot[root[index]] = chunks;
            }
            chunks.Add(index);
        }
    }

    public int Count => _begin.Length;

    /// <summary>The address the image was linked to load at, which every relative address counts from.</summary>
    public ulong ImageBase { get; }

    public static Il2CppFunctionTable For(ApplicationAnalysisContext app)
    {
        if (ReferenceEquals(_builtFor, app))
        {
            return _built;
        }
        _built = Build(app.Binary);
        _builtFor = app;
        return _built;
    }

    /// <summary>The function whose first byte is <paramref name="address"/>, or whose chunk starts there: the
    /// entries a person names as a function of their own (<c>sub_</c>) and the ones that continue another.</summary>
    public bool TryFind(ulong address, out int index)
    {
        int low = 0;
        int high = _begin.Length - 1;
        index = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (_begin[middle] <= address)
            {
                index = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return index >= 0 && address < _end[index];
    }

    public bool IsPrimaryStart(ulong address) =>
        TryFind(address, out int index) && _begin[index] == address && _root[index] == address;

    public ulong StartOf(int index) => _begin[index];

    public ulong EndOf(int index) => _end[index];

    /// <summary>The first byte of the function the entry belongs to: its own for a function, the parent's for a
    /// chunk.</summary>
    public ulong RootOf(int index) => _root[index];

    public bool IsReadOnly(ulong start, ulong end)
    {
        foreach (Section section in _sections)
            if (start >= section.Start && end <= section.End)
                return !section.Writable;
        return false;
    }

    public bool TryGetRawRange(Il2CppBinary binary, ulong address, out long rawOffset, out ulong end, out bool executable)
    {
        foreach (Section section in _sections)
        {
            if (address < section.Start || address >= section.End)
                continue;
            rawOffset = section.RawOffset + (long)(address - section.Start);
            end = section.End;
            executable = section.Executable;
            return true;
        }
        rawOffset = 0;
        end = address;
        executable = false;
        if (_sections.Length != 0 || !binary.TryMapVirtualAddressToRaw(address, out rawOffset))
            return false;
        end = address + (ulong)(binary.RawLength - rawOffset);
        executable = true;
        return true;
    }

    public ulong NextStart(ulong address)
    {
        int index = Array.BinarySearch(_begin, address);
        index = index >= 0 ? index + 1 : ~index;
        return index < _begin.Length ? _begin[index] : 0;
    }

    public IReadOnlyList<(ulong Start, ulong End)> ChunksOf(ulong root)
    {
        if (!_chunksByRoot.TryGetValue(root, out List<int> chunks))
        {
            return Array.Empty<(ulong, ulong)>();
        }
        List<(ulong, ulong)> found = new(chunks.Count);
        foreach (int index in chunks)
        {
            found.Add((_begin[index], _end[index]));
        }
        return found;
    }

    private static Il2CppFunctionTable Build(Il2CppBinary binary)
    {
        if (binary is not LibCpp2IL.PE.PE image)
        {
            return new Il2CppFunctionTable(0, Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>());
        }
        PEHeaders headers;
        using (Stream stream = image.ToStream())
        {
            headers = new PEHeaders(stream);
        }
        PEHeader header = headers.PEHeader;
        ulong imageBase = header.ImageBase;
        Section[] sections = new Section[headers.SectionHeaders.Length];
        for (int index = 0; index < sections.Length; index++)
        {
            SectionHeader section = headers.SectionHeaders[index];
            ulong available = (ulong)Math.Max(0L, Math.Min((long)section.SizeOfRawData, binary.RawLength - section.PointerToRawData));
            ulong size = Math.Min(section.VirtualSize == 0 ? (ulong)section.SizeOfRawData : (ulong)section.VirtualSize, available);
            ulong start = imageBase + (ulong)section.VirtualAddress;
            sections[index] = new Section(start, start + size, section.PointerToRawData,
                (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0,
                (section.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0);
        }
        DirectoryEntry exceptions = header.ExceptionTableDirectory;
        if (header.Magic != PEMagic.PE32Plus || exceptions.Size < EntrySize || !headers.TryGetDirectoryOffset(exceptions, out int tableRaw))
        {
            return new Il2CppFunctionTable(imageBase, Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>(), sections);
        }

        byte[] table = binary.ReadByteArrayAtRawAddress(tableRaw, exceptions.Size);
        int count = table.Length / EntrySize;
        ulong[] begin = new ulong[count];
        ulong[] end = new ulong[count];
        uint[] unwind = new uint[count];
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(index * EntrySize, EntrySize);
            begin[index] = imageBase + BinaryPrimitives.ReadUInt32LittleEndian(entry);
            end[index] = imageBase + BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4));
            unwind[index] = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
        }

        ulong[] root = new ulong[count];
        ulong[] parent = new ulong[count];
        for (int index = 0; index < count; index++)
        {
            root[index] = begin[index];
            parent[index] = ChainedParent(binary, imageBase, unwind[index]);
        }
        for (int index = 0; index < count; index++)
        {
            ulong current = parent[index];
            for (int depth = 0; current != 0 && depth < ChainDepth; depth++)
            {
                root[index] = current;
                int next = Array.BinarySearch(begin, current);
                current = next >= 0 ? parent[next] : 0;
            }
        }
        return new Il2CppFunctionTable(imageBase, begin, end, root, sections);
    }

    /// <summary>The first byte of the function an unwind record continues, or 0 when it is a function of its own.
    /// A chained record states its parent's table entry after its unwind codes.</summary>
    private static ulong ChainedParent(Il2CppBinary binary, ulong imageBase, uint unwindRva)
    {
        if ((unwindRva & 1) != 0 || unwindRva == 0 || !binary.TryMapVirtualAddressToRaw(imageBase + unwindRva, out long raw))
        {
            return 0;
        }
        byte[] head = binary.ReadByteArrayAtRawAddress(raw, UnwindHeaderSize);
        if ((head[0] >> 3 & ChainedUnwind) == 0)
        {
            return 0;
        }
        int codes = head[2];
        long parentRaw = raw + UnwindHeaderSize + ((codes + 1) & ~1) * 2;
        byte[] parent = binary.ReadByteArrayAtRawAddress(parentRaw, 4);
        return imageBase + BinaryPrimitives.ReadUInt32LittleEndian(parent);
    }
}
