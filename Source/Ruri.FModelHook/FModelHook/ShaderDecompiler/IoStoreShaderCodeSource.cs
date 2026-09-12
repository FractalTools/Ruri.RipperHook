using System.Diagnostics.CodeAnalysis;
using CUE4Parse.Compression;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Shaders;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The shader bytecode of an IoStore shader archive. Such an archive's file is a header only:
/// each shader is placed as an offset into a shader group, and each group is a separate chunk
/// of the container that holds the archive, compressed when its compressed size is the
/// smaller. The header states where shaders start, not how long they are, so a shader runs to
/// the next start in its group, or to the group's end. Groups are read and decompressed as
/// asked for, the latest kept, since a shader map's shaders sit together in one group. The
/// entries name each shader the way a library file would: sizes, running offsets, frequency.
/// </summary>
internal sealed class IoStoreShaderCodeSource : IShaderCodeSource
{
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    private readonly FIoStoreShaderCodeArchive archive;
    private readonly IoStoreReader store;
    private readonly object gate = new();
    private int cachedGroupIndex = -1;
    private byte[]? cachedGroup;

    public IoStoreShaderCodeSource(FIoStoreShaderCodeArchive archive, IoStoreReader store)
    {
        this.archive = archive ?? throw new ArgumentNullException(nameof(archive));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        Entries = Layout(archive);
        Length = Entries.Length == 0 ? 0 : (long)Entries[^1].Offset + Entries[^1].Size;
    }

    /// <summary>Every shader of the archive in archive order, as a library file lists them.</summary>
    public ShaderCodeEntry[] Entries { get; }

    public long Length { get; }

    public byte[]? Read(int shaderIndex)
    {
        lock (gate)
        {
            if (!Slice(shaderIndex, out byte[]? group, out int offset, out int size))
            {
                return null;
            }
            byte[] code = new byte[size];
            Buffer.BlockCopy(group, offset, code, 0, size);
            return code;
        }
    }

    public bool CopyTo(int shaderIndex, Stream destination)
    {
        lock (gate)
        {
            if (!Slice(shaderIndex, out byte[]? group, out int offset, out int size))
            {
                return false;
            }
            destination.Write(group, offset, size);
            return true;
        }
    }

    /// <summary>Each shader's slice: to the next shader's start in its group, or the group's end; offsets run through the shaders in archive order.</summary>
    private static ShaderCodeEntry[] Layout(FIoStoreShaderCodeArchive archive)
    {
        List<(int ShaderIndex, int Offset)>[] groups = new List<(int ShaderIndex, int Offset)>[archive.ShaderGroupEntries.Length];
        for (int group = 0; group < groups.Length; group++)
        {
            groups[group] = new List<(int ShaderIndex, int Offset)>();
        }
        for (int shader = 0; shader < archive.ShaderEntries.Length; shader++)
        {
            FIoStoreShaderCodeEntry entry = archive.ShaderEntries[shader];
            groups[(int)entry.ShaderGroupIndex].Add((shader, (int)entry.UncompressedOffsetInGroup));
        }
        int[] sizes = new int[archive.ShaderEntries.Length];
        for (int group = 0; group < groups.Length; group++)
        {
            List<(int ShaderIndex, int Offset)> slices = groups[group];
            slices.Sort((left, right) => left.Offset.CompareTo(right.Offset));
            int groupEnd = (int)archive.ShaderGroupEntries[group].UncompressedSize;
            for (int slice = 0; slice < slices.Count; slice++)
            {
                int end = slice == slices.Count - 1 ? groupEnd : slices[slice + 1].Offset;
                sizes[slices[slice].ShaderIndex] = Math.Max(0, end - slices[slice].Offset);
            }
        }
        ShaderCodeEntry[] entries = new ShaderCodeEntry[sizes.Length];
        long offset = 0;
        for (int shader = 0; shader < entries.Length; shader++)
        {
            entries[shader] = new ShaderCodeEntry
            {
                Offset = (ulong)offset,
                Size = (uint)sizes[shader],
                UncompressedSize = (uint)sizes[shader],
                Frequency = (byte)archive.ShaderEntries[shader].Frequency,
            };
            offset += sizes[shader];
        }
        return entries;
    }

    /// <summary>The group bytes holding one shader and the shader's slice of them; false when the archive places the slice past the group's end.</summary>
    private bool Slice(int shaderIndex, [NotNullWhen(true)] out byte[]? group, out int offset, out int size)
    {
        group = null;
        offset = 0;
        size = 0;
        if (shaderIndex < 0 || shaderIndex >= Entries.Length)
        {
            return false;
        }
        FIoStoreShaderCodeEntry entry = archive.ShaderEntries[shaderIndex];
        offset = (int)entry.UncompressedOffsetInGroup;
        size = (int)Entries[shaderIndex].Size;
        group = Group((int)entry.ShaderGroupIndex);
        return offset + size <= group.Length;
    }

    private byte[] Group(int groupIndex)
    {
        if (groupIndex == cachedGroupIndex && cachedGroup is not null)
        {
            return cachedGroup;
        }
        byte[] chunk = store.Read(archive.ShaderGroupIoHashes[groupIndex]);
        FIoStoreShaderGroupEntry group = archive.ShaderGroupEntries[groupIndex];
        cachedGroup = group.CompressedSize < group.UncompressedSize ? ShaderCodeCompression.Decompress(chunk, (int)group.UncompressedSize) : chunk;
        cachedGroupIndex = groupIndex;
        return cachedGroup;
    }

    public void Dispose()
    {
        lock (gate)
        {
            cachedGroup = null;
            cachedGroupIndex = -1;
        }
    }
}
