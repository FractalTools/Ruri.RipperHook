using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.VirtualFileSystem;

namespace Ruri.FModelHook.ShaderDecompiler.Semantics;

/// <summary>
/// Every shader-code library the game ships, read out of the mounted archives and indexed by
/// shader-map hash, so a material's inline shader map -- which under shared shader code holds
/// only that hash -- leads to the bytecode of each shader the map lists. An IoStore archive is
/// opened over the container that holds it, its code staying there until a shader is asked
/// for; a serialized archive is parsed whole. A library's platform is the token its file name
/// states (PCD3D_SM5, PCD3D_SM6, ...).
/// </summary>
internal sealed class MaterialShaderLibraryIndex : IDisposable
{
    private const string LibraryExtension = "ushaderbytecode";

    /// <summary>One shader map inside one library: where the map's shader list starts and how long it is.</summary>
    public readonly record struct Entry(ShaderLibrary Library, string Platform, ShaderMapEntry Map);

    private readonly List<ShaderLibrary> libraries = new();
    private readonly Dictionary<string, Entry> byMapHash = new(StringComparer.OrdinalIgnoreCase);

    public int LibraryCount => libraries.Count;

    public int MapCount => byMapHash.Count;

    public static MaterialShaderLibraryIndex Load(AbstractFileProvider provider, Action<string> log)
    {
        MaterialShaderLibraryIndex index = new();
        foreach (GameFile file in provider.Files.Values)
        {
            if (!string.Equals(file.Extension, LibraryExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                ShaderLibrary? library = Open(file);
                if (library is null)
                {
                    log($"[Unreal] Shader library '{file.Name}' is neither an IoStore archive of a mounted container nor a serialized archive; skipped.");
                    continue;
                }
                string platform = PlatformOf(file.Name);
                index.libraries.Add(library);
                int mapCount = Math.Min(library.ShaderMapEntries.Length, library.ShaderMapHashes.Count);
                for (int mapIndex = 0; mapIndex < mapCount; mapIndex++)
                {
                    index.byMapHash.TryAdd(library.ShaderMapHashes[mapIndex], new Entry(library, platform, library.ShaderMapEntries[mapIndex]));
                }
                log($"[Unreal] Shader library '{file.Name}': {library.ShaderEntries.Length} shader(s), {mapCount} shader map(s), {library.CodeBodyLength} byte(s) of code, platform {platform}.");
            }
            catch (Exception exception)
            {
                log($"[Unreal] Shader library '{file.Name}' unreadable: {exception.GetType().Name}: {exception.Message}");
            }
        }
        return index;
    }

    /// <summary>An IoStore archive reads its groups from the container that holds it; a serialized archive already carries its code.</summary>
    private static ShaderLibrary? Open(GameFile file)
    {
        FShaderCodeArchive archive = new(file.CreateReader());
        return archive.SerializedShaders switch
        {
            FIoStoreShaderCodeArchive ioStore when file is VfsEntry { Vfs: IoStoreReader store } => ShaderLibrary.FromIoStore(ioStore, store),
            FSerializedShaderArchive serialized => ShaderLibrary.FromSerialized(serialized, archive.ShaderCode),
            _ => null,
        };
    }

    public bool TryFind(string shaderMapHash, out Entry entry) => byMapHash.TryGetValue(shaderMapHash, out entry);

    /// <summary>The library-wide shader index of the map's <paramref name="resourceIndex"/>-th shader, in the order the map's inline code lists them.</summary>
    public static int ShaderIndex(Entry entry, int resourceIndex)
    {
        long offset = entry.Map.ShaderIndicesOffset + resourceIndex;
        if (resourceIndex < 0 || resourceIndex >= entry.Map.NumShaders || offset < 0 || offset >= entry.Library.ShaderIndices.Length)
        {
            return -1;
        }
        return (int)entry.Library.ShaderIndices[offset];
    }

    /// <summary>The platform token after the archive name's last dash: "ShaderArchive-Game-PCD3D_SM6.ushaderbytecode" states PCD3D_SM6.</summary>
    private static string PlatformOf(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int dash = stem.LastIndexOf('-');
        return dash >= 0 ? stem[(dash + 1)..] : stem;
    }

    public void Dispose()
    {
        foreach (ShaderLibrary library in libraries)
        {
            library.Dispose();
        }
        libraries.Clear();
        byMapHash.Clear();
    }
}
