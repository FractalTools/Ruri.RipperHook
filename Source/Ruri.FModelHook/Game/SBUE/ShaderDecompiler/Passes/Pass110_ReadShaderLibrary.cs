using System;
using System.Collections.Generic;
using System.IO;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class Pass110_ReadShaderLibrary
{
    public static void DoPass(PipelineState state)
    {
        state.Library = ReadShaderLibrary(state.Options.LibraryPath);
        state.Log($"    Library v{state.Library.Version}: {state.Library.ShaderEntries.Length} shaders, {state.Library.ShaderMapHashes.Count} shader-map hashes, code-body={state.Library.CodeBodyLength:N0} bytes.");
    }

    private static ShaderLibrary ReadShaderLibrary(string path)
    {
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

            count = reader.ReadInt32();
            fs.Seek((long)count * 16L, SeekOrigin.Current);

            count = reader.ReadInt32();
            lib.ShaderIndices = new uint[count];
            for (int i = 0; i < count; i++) lib.ShaderIndices[i] = reader.ReadUInt32();

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

    private FileStream? _codeStream;
    private long _codeBaseOffset;
    private readonly object _streamLock = new();

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

        long entrySize = entry.Size;
        long entryOffset = (long)entry.Offset;
        if (entryOffset < 0 || entrySize < 0 || entryOffset + entrySize > CodeBodyLength) return null;
        if (entrySize == 0) return Array.Empty<byte>();
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
