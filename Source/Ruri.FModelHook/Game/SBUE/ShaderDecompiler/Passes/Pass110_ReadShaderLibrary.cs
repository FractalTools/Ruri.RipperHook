using System;
using System.Collections.Generic;
using System.IO;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 110 — Read the on-disk `.ushaderlib` (FSerializedShaderArchive
// header v2 written by Pass 010) into a structured `ShaderLibrary`
// object. The on-disk shape mirrors UE's serialized shader archive so
// anything downstream stays decoupled from FModel's IoStore-only read
// path.
//
// Layout (uint32 LE; SHA1 hashes are uppercase hex 40-char strings):
//   uint32 Version = 2
//   uint32 numShaderMapHashes; SHA1[20] * N
//   uint32 numShaderHashes;    SHA1[20] * N
//   uint32 numShaderMapEntries; FShaderMapEntry * N (4×uint32 each)
//   uint32 numShaderEntries;    FShaderCodeEntry * N (uint64 + 3×uint32, last byte=Frequency)
//   uint32 numPreloadEntries;   skipped (16 bytes each — we don't need them post-merge)
//   uint32 numShaderIndices;    uint32 * N
//   <remaining stream>          packed shader code buffer
//
// **Streaming code body**: the master ShaderArchive's code buffer is
// 6.5 GB. Loading it into a `byte[]` is impossible — `Array.MaxLength`
// caps at ~2.1 GB, and even the ReadBytes((int)remaining) cast wraps
// negative. Instead, the code-body region of the file is left on disk
// and `ShaderLibrary` exposes seek+read access via `GetShaderCode(int)`.
// The ShaderLibrary owns the FileStream and disposes it when the
// pipeline finishes. Peak RAM for a single shader read is bounded by
// max(per-shader Size), which the cook caps at well under 1 MB.
internal static class Pass110_ReadShaderLibrary
{
    public static void DoPass(PipelineState state)
    {
        state.Library = ReadShaderLibrary(state.Options.LibraryPath);
        state.Log($"    Library v{state.Library.Version}: {state.Library.ShaderEntries.Length} shaders, {state.Library.ShaderMapHashes.Count} shader-map hashes, code-body={state.Library.CodeBodyLength:N0} bytes.");
    }

    private static ShaderLibrary ReadShaderLibrary(string path)
    {
        // FileStream ownership transfers to the returned ShaderLibrary
        // on success. On failure the stream is disposed before propagating.
        FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using BinaryReader reader = new(fs, System.Text.Encoding.UTF8, leaveOpen: true);

            ShaderLibrary lib = new() { Version = reader.ReadUInt32() };

            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++) lib.ShaderMapHashes.Add(ReadShaHash(reader));

            count = reader.ReadInt32();
            for (int i = 0; i < count; i++) lib.ShaderHashes.Add(ReadShaHash(reader));

            count = reader.ReadInt32();
            lib.ShaderMapEntries = new ShaderMapEntry[count];
            for (int i = 0; i < count; i++)
            {
                lib.ShaderMapEntries[i] = new ShaderMapEntry
                {
                    ShaderIndicesOffset = reader.ReadUInt32(),
                    NumShaders = reader.ReadUInt32(),
                    FirstPreloadIndex = reader.ReadUInt32(),
                    NumPreloadEntries = reader.ReadUInt32(),
                };
            }

            count = reader.ReadInt32();
            lib.ShaderEntries = new ShaderCodeEntry[count];
            for (int i = 0; i < count; i++)
            {
                lib.ShaderEntries[i] = new ShaderCodeEntry
                {
                    Offset = reader.ReadUInt64(),
                    Size = reader.ReadUInt32(),
                    UncompressedSize = reader.ReadUInt32(),
                    Frequency = reader.ReadByte(),
                };
            }

            // Preload entries are 16 bytes each (long offset + long size); skipped.
            count = reader.ReadInt32();
            fs.Seek((long)count * 16L, SeekOrigin.Current);

            count = reader.ReadInt32();
            lib.ShaderIndices = new uint[count];
            for (int i = 0; i < count; i++) lib.ShaderIndices[i] = reader.ReadUInt32();

            // Capture the offset where the packed code body begins. Everything
            // after this point is `<size>` bytes of shader code per entry,
            // addressed by ShaderEntries[i].Offset (relative to CodeBaseOffset).
            // The remaining file length is left on disk — see GetShaderCode.
            lib.AttachCodeStream(fs, codeBaseOffset: fs.Position);
            return lib;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static string ReadShaHash(BinaryReader reader)
        => BitConverter.ToString(reader.ReadBytes(20)).Replace("-", string.Empty);
}

internal struct ShaderCodeEntry
{
    public ulong Offset;
    public uint Size;
    public uint UncompressedSize;
    public byte Frequency;
}

internal struct ShaderMapEntry
{
    public uint ShaderIndicesOffset;
    public uint NumShaders;
    public uint FirstPreloadIndex;
    public uint NumPreloadEntries;
}

internal sealed class ShaderLibrary : IDisposable
{
    public uint Version;
    public List<string> ShaderMapHashes = new();
    public List<string> ShaderHashes = new();
    public ShaderMapEntry[] ShaderMapEntries = Array.Empty<ShaderMapEntry>();
    public ShaderCodeEntry[] ShaderEntries = Array.Empty<ShaderCodeEntry>();
    public uint[] ShaderIndices = Array.Empty<uint>();

    // Streaming code-body access. The .ushaderlib FileStream stays open
    // for the lifetime of the ShaderLibrary; GetShaderCode does a
    // seek+read on demand. Necessary because the master archive's
    // code body is ~6.5 GB and won't fit in a `byte[]` (Array.MaxLength
    // ~2.1 GB).
    private FileStream? _codeStream;
    private long _codeBaseOffset;
    private readonly object _streamLock = new();

    // Total length of the packed code body (file length minus header).
    // Used by callers to bounds-check ShaderEntries[i].Offset+Size.
    public long CodeBodyLength { get; private set; }

    internal void AttachCodeStream(FileStream stream, long codeBaseOffset)
    {
        _codeStream = stream;
        _codeBaseOffset = codeBaseOffset;
        CodeBodyLength = stream.Length - codeBaseOffset;
    }

    public byte[]? GetShaderCode(int index)
    {
        if (_codeStream is null) return null;
        if (index < 0 || index >= ShaderEntries.Length) return null;
        ShaderCodeEntry entry = ShaderEntries[index];

        // Bounds-check on the disk region. `(long)entry.Offset + entry.Size`
        // stays in long arithmetic so a 6 GB+ code body doesn't wrap.
        long entrySize = entry.Size;
        long entryOffset = (long)entry.Offset;
        if (entryOffset < 0 || entrySize < 0 || entryOffset + entrySize > CodeBodyLength) return null;
        if (entrySize == 0) return Array.Empty<byte>();
        // Per-shader size is bounded by uint32, but Array.MaxLength is the
        // hard runtime cap. Anything bigger means corrupted metadata.
        if (entrySize > Array.MaxLength) return null;

        byte[] code = new byte[entry.Size];
        lock (_streamLock)
        {
            _codeStream!.Position = _codeBaseOffset + entryOffset;
            int read = 0;
            while (read < code.Length)
            {
                int n = _codeStream.Read(code, read, code.Length - read);
                if (n <= 0) return null;
                read += n;
            }
        }
        return code;
    }

    public void Dispose()
    {
        FileStream? stream = _codeStream;
        _codeStream = null;
        stream?.Dispose();
    }
}
