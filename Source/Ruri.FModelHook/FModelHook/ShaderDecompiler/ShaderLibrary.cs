using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// A shader-code library: the hashes of its shader maps and shaders, each map's run of the
/// shared shader-index list, each shader's entry, and the code behind them.
///
/// Opened over the archive the game ships and never over a copy of it. Both shapes leave the
/// code where it is until a shader is asked for: an IoStore archive's code lives in its
/// container's group chunks, and a serialized archive's body is read a shader at a time through
/// a ranged view of the container entry. Nothing a library does writes a file.
/// </summary>
internal sealed class ShaderLibrary : IDisposable
{
    private IShaderCodeSource? source;
    private GameFileWindow? window;

    public List<string> ShaderMapHashes = new();
    private FSHAHash[] shaderHashes = Array.Empty<FSHAHash>();
    public ShaderMapEntry[] ShaderMapEntries = Array.Empty<ShaderMapEntry>();

    /// <summary>How many shaders the archive holds; a hash is spelled out only for the ones asked about.</summary>
    public int ShaderCount => shaderHashes.Length;

    /// <summary>
    /// One shader's hash as text. The archive holds hundreds of thousands of them; spelling every
    /// one out on open was a hundred thousand strings nobody read, so each is spelled when named.
    /// </summary>
    public string ShaderHash(int index)
        => index >= 0 && index < shaderHashes.Length ? shaderHashes[index].ToString() : string.Empty;
    public ShaderCodeEntry[] ShaderEntries = Array.Empty<ShaderCodeEntry>();
    public uint[] ShaderIndices = Array.Empty<uint>();

    /// <summary>Which of the engine's two archive shapes this library was opened over.</summary>
    public string SourceType { get; private init; } = string.Empty;

    public long CodeBodyLength => source?.Length ?? 0;

    /// <summary>How many bytes the shipped archive holds in all.</summary>
    public long Size { get; private set; }

    /// <summary>How many of them this run actually read; the rest was never fetched.</summary>
    public long BytesRead => window?.BytesFetched ?? 0;

    /// <summary>
    /// The library of one shipped shader archive, or null when the file is neither an IoStore
    /// archive of a mounted container nor a serialized one.
    ///
    /// The archive's own tables are read by CUE4Parse's readers so that every dialect it knows --
    /// the short hashes UE5.8 writes, the wide ones one title writes -- is read the way it states
    /// them. Only WHICH reader applies is decided here, the same way
    /// CUE4Parse/UE4/Shaders/FShaderCodeArchive.cs decides it, because that type reads the whole
    /// code body into per-shader arrays as it parses and a caller after one character wants none
    /// of it.
    /// </summary>
    public static ShaderLibrary? Open(GameFile file, VersionContainer versions)
    {
        GameFileWindow window = new(file);
        FArchive archive = new FStreamArchive(file.Path, window, versions);
        try
        {
            uint archiveVersion = archive.Read<uint>();
            bool isIoStore = archive.Game >= EGame.GAME_UE5_0 && archiveVersion == 1;
            if (archive.Game is EGame.GAME_ArenaBreakoutMobile)
            {
                archiveVersion = 2;
            }
            if (archiveVersion == 2)
            {
                ShaderLibrary serialized = FromSerialized(new FSerializedShaderArchive(archive), archive);
                serialized.window = window;
                serialized.Size = file.Size;
                return serialized;
            }
            if (isIoStore && file is VfsEntry { Vfs: IoStoreReader store })
            {
                ShaderLibrary library = FromIoStore(new FIoStoreShaderCodeArchive(archive), store);
                library.Size = file.Size;
                archive.Dispose();
                return library;
            }
            archive.Dispose();
            return null;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>A library over an IoStore archive, reading each shader's group from the container that holds the archive.</summary>
    private static ShaderLibrary FromIoStore(FIoStoreShaderCodeArchive archive, IoStoreReader store)
    {
        IoStoreShaderCodeSource code = new(archive, store);
        return new ShaderLibrary
        {
            SourceType = nameof(FIoStoreShaderCodeArchive),
            ShaderMapHashes = Hashes(archive.ShaderMapHashes),
            shaderHashes = archive.ShaderHashes,
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

    /// <summary>A library over a serialized archive, whose code body starts where its tables end.</summary>
    private static ShaderLibrary FromSerialized(FSerializedShaderArchive archive, FArchive reader)
    {
        ShaderCodeEntry[] entries = Array.ConvertAll(archive.ShaderEntries, entry => new ShaderCodeEntry
        {
            Offset = entry.Offset,
            Size = entry.Size,
            UncompressedSize = entry.UncompressedSize,
            Frequency = entry.Frequency,
        });
        return new ShaderLibrary
        {
            SourceType = nameof(FSerializedShaderArchive),
            ShaderMapHashes = Hashes(archive.ShaderMapHashes),
            shaderHashes = archive.ShaderHashes,
            ShaderMapEntries = Array.ConvertAll(archive.ShaderMapEntries, map => new ShaderMapEntry
            {
                ShaderIndicesOffset = map.ShaderIndicesOffset,
                NumShaders = map.NumShaders,
                FirstPreloadIndex = map.FirstPreloadIndex,
                NumPreloadEntries = map.NumPreloadEntries,
            }),
            ShaderEntries = entries,
            ShaderIndices = archive.ShaderIndices,
            source = new ArchiveShaderCodeSource(reader, reader.Position, entries),
        };
    }

    public byte[]? GetShaderCode(int index) => source?.Read(index);

    private static List<string> Hashes(FSHAHash[] hashes)
    {
        List<string> texts = new(hashes.Length);
        foreach (FSHAHash hash in hashes)
        {
            texts.Add(hash.ToString());
        }
        return texts;
    }

    public void Dispose()
    {
        IShaderCodeSource? code = source;
        source = null;
        code?.Dispose();
    }
}
