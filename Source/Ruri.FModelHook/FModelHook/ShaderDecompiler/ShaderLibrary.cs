using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Shaders;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// A shader-code library: the hashes of its shader maps and shaders, each map's run of the
/// shared shader-index list, each shader's entry, and the code behind them. Comes from a
/// library file the exporter wrote, a serialized archive the parser read whole, or an IoStore
/// archive whose code stays in its container's chunks until asked for. The file layout is the
/// engine's serialized archive: version, shader-map hashes, shader hashes, map entries, shader
/// entries, preload entries, shader indices, then the code body.
/// </summary>
internal sealed class ShaderLibrary : IDisposable
{
    public const uint FileVersion = 2;
    private const int HashLength = 20;
    private const int PreloadEntryLength = 16;

    private IShaderCodeSource? source;

    public uint Version;
    public List<string> ShaderMapHashes = new();
    public List<string> ShaderHashes = new();
    public ShaderMapEntry[] ShaderMapEntries = Array.Empty<ShaderMapEntry>();
    public ShaderCodeEntry[] ShaderEntries = Array.Empty<ShaderCodeEntry>();
    public uint[] ShaderIndices = Array.Empty<uint>();

    public long CodeBodyLength => source?.Length ?? 0;

    /// <summary>A library out of any seekable stream, which the library then keeps for reading code.</summary>
    public static ShaderLibrary Read(Stream stream)
    {
        try
        {
            using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            ShaderLibrary library = new() { Version = reader.ReadUInt32() };
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                library.ShaderMapHashes.Add(ReadHash(reader));
            }
            count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                library.ShaderHashes.Add(ReadHash(reader));
            }
            count = reader.ReadInt32();
            library.ShaderMapEntries = new ShaderMapEntry[count];
            for (int i = 0; i < count; i++)
            {
                library.ShaderMapEntries[i] = new ShaderMapEntry
                {
                    ShaderIndicesOffset = reader.ReadUInt32(),
                    NumShaders = reader.ReadUInt32(),
                    FirstPreloadIndex = reader.ReadUInt32(),
                    NumPreloadEntries = reader.ReadUInt32(),
                };
            }
            count = reader.ReadInt32();
            library.ShaderEntries = new ShaderCodeEntry[count];
            for (int i = 0; i < count; i++)
            {
                library.ShaderEntries[i] = new ShaderCodeEntry
                {
                    Offset = reader.ReadUInt64(),
                    Size = reader.ReadUInt32(),
                    UncompressedSize = reader.ReadUInt32(),
                    Frequency = reader.ReadByte(),
                };
            }
            count = reader.ReadInt32();
            stream.Seek((long)count * PreloadEntryLength, SeekOrigin.Current);
            count = reader.ReadInt32();
            library.ShaderIndices = new uint[count];
            for (int i = 0; i < count; i++)
            {
                library.ShaderIndices[i] = reader.ReadUInt32();
            }
            library.source = new StreamShaderCodeSource(stream, stream.Position, library.ShaderEntries);
            return library;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A library over an IoStore archive, reading each shader's group from the container that holds the archive.</summary>
    public static ShaderLibrary FromIoStore(FIoStoreShaderCodeArchive archive, IoStoreReader store)
    {
        IoStoreShaderCodeSource code = new(archive, store);
        return new ShaderLibrary
        {
            Version = FileVersion,
            ShaderMapHashes = Hashes(archive.ShaderMapHashes),
            ShaderHashes = Hashes(archive.ShaderHashes),
            ShaderMapEntries = Array.ConvertAll(archive.ShaderMapEntries, map => new ShaderMapEntry
            {
                ShaderIndicesOffset = map.ShaderIndicesOffset,
                NumShaders = map.NumShaders,
            }),
            ShaderEntries = code.Entries,
            ShaderIndices = archive.ShaderIndices,
            source = code,
        };
    }

    /// <summary>A library over a serialized archive the parser has already split into per-shader arrays.</summary>
    public static ShaderLibrary FromSerialized(FSerializedShaderArchive archive, byte[][] code)
    {
        return new ShaderLibrary
        {
            Version = FileVersion,
            ShaderMapHashes = Hashes(archive.ShaderMapHashes),
            ShaderHashes = Hashes(archive.ShaderHashes),
            ShaderMapEntries = Array.ConvertAll(archive.ShaderMapEntries, map => new ShaderMapEntry
            {
                ShaderIndicesOffset = map.ShaderIndicesOffset,
                NumShaders = map.NumShaders,
                FirstPreloadIndex = map.FirstPreloadIndex,
                NumPreloadEntries = map.NumPreloadEntries,
            }),
            ShaderEntries = Array.ConvertAll(archive.ShaderEntries, entry => new ShaderCodeEntry
            {
                Offset = entry.Offset,
                Size = entry.Size,
                UncompressedSize = entry.UncompressedSize,
                Frequency = entry.Frequency,
            }),
            ShaderIndices = archive.ShaderIndices,
            source = new ArrayShaderCodeSource(code),
        };
    }

    public byte[]? GetShaderCode(int index) => source?.Read(index);

    /// <summary>Writes one shader's bytes to the destination; false when the library has no code for it.</summary>
    public bool CopyShaderCode(int index, Stream destination) => source?.CopyTo(index, destination) ?? false;

    private static List<string> Hashes(FSHAHash[] hashes)
    {
        List<string> texts = new(hashes.Length);
        foreach (FSHAHash hash in hashes)
        {
            texts.Add(hash.ToString());
        }
        return texts;
    }

    private static string ReadHash(BinaryReader reader) => Convert.ToHexString(reader.ReadBytes(HashLength));

    public void Dispose()
    {
        IShaderCodeSource? code = source;
        source = null;
        code?.Dispose();
    }
}
