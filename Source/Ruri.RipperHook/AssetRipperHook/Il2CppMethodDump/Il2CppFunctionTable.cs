using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
    private const int ExceptionDirectoryIndex = 3;
    private const int DirectoriesOffset = 112;
    private const int HeaderBytes = 4096;
    private const int SectionHeaderSize = 40;
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
        Il2CppFunctionTable empty = new(0, Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>());
        long length = Math.Min(binary.RawLength, HeaderBytes);
        if (length < 0x200)
        {
            return empty;
        }
        byte[] header = binary.ReadByteArrayAtRawAddress(0, (int)length);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            return empty;
        }
        int pe = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C));
        if (pe <= 0 || pe + 24 > header.Length || header[pe] != (byte)'P' || header[pe + 1] != (byte)'E')
        {
            return empty;
        }
        int optional = pe + 24;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(optional));
        if (magic is not (0x20b or 0x10b))
        {
            return empty;
        }
        ulong imageBase = magic == 0x20b
            ? BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(optional + 24))
            : BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(optional + 28));
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 6));
        int sectionTable = optional + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 20));
        byte[] sectionHeaders = binary.ReadByteArrayAtRawAddress(sectionTable, sectionCount * SectionHeaderSize);
        Section[] sections = new Section[sectionCount];
        for (int index = 0; index < sectionCount; index++)
        {
            ReadOnlySpan<byte> section = sectionHeaders.AsSpan(index * SectionHeaderSize, SectionHeaderSize);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(8));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(12));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(16));
            uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(20));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(36));
            ulong available = (ulong)Math.Max(0L, Math.Min((long)rawSize, binary.RawLength - rawOffset));
            ulong size = Math.Min(virtualSize == 0 ? rawSize : virtualSize, available);
            sections[index] = new Section(imageBase + virtualAddress, imageBase + virtualAddress + size,
                rawOffset, (flags & 0x20000000) != 0, (flags & 0x80000000) != 0);
        }
        empty = new Il2CppFunctionTable(imageBase, Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>(), sections);
        if (magic != 0x20b)
            return empty;
        int directory = optional + DirectoriesOffset + ExceptionDirectoryIndex * 8;
        if (directory + 8 > header.Length)
        {
            return empty;
        }
        uint tableRva = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(directory));
        uint tableSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(directory + 4));
        if (tableRva == 0 || tableSize < EntrySize || !binary.TryMapVirtualAddressToRaw(imageBase + tableRva, out long tableRaw))
        {
            return empty;
        }

        byte[] table = binary.ReadByteArrayAtRawAddress(tableRaw, (int)tableSize);
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
