using System;
using System.Collections.Generic;
using System.IO;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 000 — Read the on-disk `.ushaderlib` (FSerializedShaderArchive
// header v2 written by ShaderArchiveExporter) into a structured
// `ShaderLibrary` object. The on-disk shape mirrors UE's serialized
// shader archive so anything downstream stays decoupled from FModel's
// IoStore-only read path.
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
internal static class Pass000_ReadLibrary
{
    public static void DoPass(PipelineState state)
    {
        state.Library = ReadShaderLibrary(state.Options.LibraryPath);
        state.Log($"    Library v{state.Library.Version}: {state.Library.ShaderEntries.Length} shaders, {state.Library.ShaderMapHashes.Count} shader-map hashes.");
    }

    private static ShaderLibrary ReadShaderLibrary(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using BinaryReader reader = new(fs);

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
        fs.Seek(count * 16, SeekOrigin.Current);

        count = reader.ReadInt32();
        lib.ShaderIndices = new uint[count];
        for (int i = 0; i < count; i++) lib.ShaderIndices[i] = reader.ReadUInt32();

        long remaining = fs.Length - fs.Position;
        lib.CodeBuffer = reader.ReadBytes((int)remaining);

        return lib;
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

internal sealed class ShaderLibrary
{
    public uint Version;
    public List<string> ShaderMapHashes = new();
    public List<string> ShaderHashes = new();
    public ShaderMapEntry[] ShaderMapEntries = Array.Empty<ShaderMapEntry>();
    public ShaderCodeEntry[] ShaderEntries = Array.Empty<ShaderCodeEntry>();
    public uint[] ShaderIndices = Array.Empty<uint>();
    public byte[] CodeBuffer = Array.Empty<byte>();

    public byte[]? GetShaderCode(int index)
    {
        if (index < 0 || index >= ShaderEntries.Length) return null;
        ShaderCodeEntry entry = ShaderEntries[index];
        if ((long)entry.Offset + entry.Size > CodeBuffer.Length) return null;

        byte[] code = new byte[entry.Size];
        Array.Copy(CodeBuffer, (long)entry.Offset, code, 0, entry.Size);
        return code;
    }
}
