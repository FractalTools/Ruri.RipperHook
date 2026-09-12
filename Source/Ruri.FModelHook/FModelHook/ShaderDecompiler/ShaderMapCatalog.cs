using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// Where the shader map of a given hash lives: which archive carries it, which of that archive's
/// maps it is, and the library to read its shaders out of.
///
/// Unreal ships no shader asset. A material's compiled program is a run of blobs inside an
/// archive shared by every other material of its platform, and the material states only the hash
/// of the map. This is the one place that turns that hash into a place to read from.
///
/// Archives are opened one at a time, as asked for, and each is opened over the shipped file
/// itself -- header tables only, code left where it is. So a caller after one character opens
/// the archives that carry its maps and no others, and a caller that walks many hashes pays for
/// each archive once. Every caller that needs shader bytecode comes through here, so there is
/// one cost model and not one per lane.
/// </summary>
internal sealed class ShaderMapCatalog : IDisposable
{
    private const string ArchiveExtension = "ushaderbytecode";

    /// <summary>One shader map inside one archive: the library to read from, the map's own entry, and what the archive is.</summary>
    public readonly record struct Placement(
        ShaderLibrary Library,
        string ArchivePath,
        string ArchiveName,
        string Platform,
        int MapIndex,
        ShaderMapEntry Map);

    private readonly AbstractFileProvider provider;
    private readonly Action<string> log;
    private readonly Action<string> logError;
    private readonly Queue<GameFile> unopened;
    private readonly List<ShaderLibrary> opened = new();
    private readonly Dictionary<string, Placement> byMapHash = new(StringComparer.OrdinalIgnoreCase);

    private ShaderMapCatalog(AbstractFileProvider provider, Queue<GameFile> archives, Action<string> log, Action<string> logError)
    {
        this.provider = provider;
        this.unopened = archives;
        this.log = log;
        this.logError = logError;
    }

    public static ShaderMapCatalog Open(AbstractFileProvider provider, Action<string> log, Action<string> logError)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Queue<GameFile> archives = new(provider.Files.Values
            .Where(static file => file.Extension.Equals(ArchiveExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase));
        return new ShaderMapCatalog(provider, archives, log, logError);
    }

    public int OpenedArchiveCount => opened.Count;

    public int IndexedMapCount => byMapHash.Count;

    /// <summary>Where that shader map lives, opening further archives only until it is found.</summary>
    public bool TryPlace(string shaderMapHash, out Placement placement)
    {
        if (byMapHash.TryGetValue(shaderMapHash, out placement))
        {
            return true;
        }
        while (unopened.Count > 0)
        {
            IndexOne(unopened.Dequeue());
            if (byMapHash.TryGetValue(shaderMapHash, out placement))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The library-wide shader index of a map's <paramref name="resourceIndex"/>-th shader, in the order the map lists them.</summary>
    public static int ShaderIndex(Placement placement, int resourceIndex)
    {
        long offset = placement.Map.ShaderIndicesOffset + resourceIndex;
        if (resourceIndex < 0 || resourceIndex >= placement.Map.NumShaders || offset < 0 || offset >= placement.Library.ShaderIndices.Length)
        {
            return -1;
        }
        return (int)placement.Library.ShaderIndices[offset];
    }

    private void IndexOne(GameFile file)
    {
        ShaderLibrary? library;
        try
        {
            library = ShaderLibrary.Open(file, provider.Versions);
        }
        catch (Exception exception)
        {
            logError($"[ShaderMapCatalog] '{file.Name}' unreadable: {exception.GetType().Name}: {exception.Message}");
            return;
        }
        if (library is null)
        {
            logError($"[ShaderMapCatalog] '{file.Name}' is neither an IoStore archive of a mounted container nor a serialized one; skipped.");
            return;
        }
        opened.Add(library);
        string platform = PlatformOf(file.Name);
        int mapCount = Math.Min(library.ShaderMapEntries.Length, library.ShaderMapHashes.Count);
        for (int mapIndex = 0; mapIndex < mapCount; mapIndex++)
        {
            byMapHash.TryAdd(library.ShaderMapHashes[mapIndex], new Placement(
                library, file.Path, file.NameWithoutExtension, platform, mapIndex, library.ShaderMapEntries[mapIndex]));
        }
        log($"[ShaderMapCatalog] '{file.Name}': {mapCount} shader map(s) over {library.ShaderEntries.Length} shader(s), platform {platform}, {library.SourceType}.");
    }

    /// <summary>The platform token after the archive name's last dash: "ShaderArchive-Game-PCD3D_SM6" states PCD3D_SM6.</summary>
    private static string PlatformOf(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int dash = stem.LastIndexOf('-');
        return dash >= 0 ? stem[(dash + 1)..] : stem;
    }

    public void Dispose()
    {
        foreach (ShaderLibrary library in opened)
        {
            library.Dispose();
        }
        opened.Clear();
        byMapHash.Clear();
        unopened.Clear();
    }
}
