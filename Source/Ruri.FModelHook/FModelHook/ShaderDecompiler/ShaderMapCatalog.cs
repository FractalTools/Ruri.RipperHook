using System.Runtime.CompilerServices;
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
/// itself -- header tables only, code left where it is. Those tables are the cost: an archive of
/// half a million shaders states each one's hash, entry and index, and reading them is seconds.
/// So a catalog lives as long as the provider it reads through: the first request after a mount
/// opens the archives its maps live in, and every request after it finds them already open. A
/// new mount is a new provider and gets a catalog of its own.
/// </summary>
internal sealed class ShaderMapCatalog
{
    private const string ArchiveExtension = "ushaderbytecode";

    private static readonly ConditionalWeakTable<AbstractFileProvider, ShaderMapCatalog> Catalogs = new();

    /// <summary>One shader map inside one archive: the library to read from, the map's own entry, and what the archive is.</summary>
    public readonly record struct Placement(
        ShaderLibrary Library,
        string ArchivePath,
        string ArchiveName,
        string Platform,
        int MapIndex,
        ShaderMapEntry Map);

    private readonly AbstractFileProvider provider;
    private readonly Queue<GameFile> unopened;
    private readonly List<ShaderLibrary> opened = new();
    private readonly Dictionary<string, Placement> byMapHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    private ShaderMapCatalog(AbstractFileProvider provider)
    {
        this.provider = provider;
        unopened = new Queue<GameFile>(provider.Files.Values
            .Where(static file => file.Extension.Equals(ArchiveExtension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The catalog of a mounted provider, made on its first request and kept for its life.</summary>
    public static ShaderMapCatalog For(AbstractFileProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Catalogs.GetValue(provider, static mounted => new ShaderMapCatalog(mounted));
    }

    public int OpenedArchiveCount
    {
        get { lock (gate) { return opened.Count; } }
    }

    public int IndexedMapCount
    {
        get { lock (gate) { return byMapHash.Count; } }
    }

    /// <summary>Where that shader map lives, opening further archives only until it is found.</summary>
    public bool TryPlace(string shaderMapHash, Action<string> log, Action<string> logError, out Placement placement)
    {
        lock (gate)
        {
            if (byMapHash.TryGetValue(shaderMapHash, out placement))
            {
                return true;
            }
            while (unopened.Count > 0)
            {
                IndexOne(unopened.Dequeue(), log, logError);
                if (byMapHash.TryGetValue(shaderMapHash, out placement))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>The library-wide shader index of a map's <paramref name="resourceIndex"/>-th shader, in the order the map lists them.</summary>
    /// <summary>
    /// Every map ONE archive states, by hash, in the archive's own order.
    ///
    /// A GLOBAL shader -- the tonemapper, the blurs, the depth passes -- belongs to no material
    /// and no package, so nothing in the content tree names it and the per-asset road cannot
    /// reach it at all. The archive is the only thing that can say what is in it, so this is the
    /// question asked of the archive itself. Every archive is opened to answer, because which one
    /// holds a given name is not knowable without opening them.
    /// </summary>
    public IReadOnlyList<string> MapHashesOf(string archiveName, Action<string> log, Action<string> logError)
    {
        lock (gate)
        {
            while (unopened.Count > 0)
            {
                IndexOne(unopened.Dequeue(), log, logError);
            }
            return byMapHash
                .Where(pair => pair.Value.ArchiveName.Equals(archiveName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Value.MapIndex)
                .Select(pair => pair.Key)
                .ToList();
        }
    }

    /// <summary>The names of every archive this mount ships, once they have all been opened.</summary>
    public IReadOnlyList<string> ArchiveNames(Action<string> log, Action<string> logError)
    {
        lock (gate)
        {
            while (unopened.Count > 0)
            {
                IndexOne(unopened.Dequeue(), log, logError);
            }
            return byMapHash.Values.Select(placement => placement.ArchiveName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static int ShaderIndex(Placement placement, int resourceIndex)
    {
        long offset = placement.Map.ShaderIndicesOffset + resourceIndex;
        if (resourceIndex < 0 || resourceIndex >= placement.Map.NumShaders || offset < 0 || offset >= placement.Library.ShaderIndices.Length)
        {
            return -1;
        }
        return (int)placement.Library.ShaderIndices[offset];
    }

    private void IndexOne(GameFile file, Action<string> log, Action<string> logError)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
        log($"[ShaderMapCatalog] '{file.Name}': {mapCount} shader map(s) over {library.ShaderCount} shader(s), platform {platform}, {library.SourceType}, tables read in {stopwatch.ElapsedMilliseconds} ms.");
    }

    /// <summary>The platform token after the archive name's last dash: "ShaderArchive-Game-PCD3D_SM6" states PCD3D_SM6.</summary>
    private static string PlatformOf(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int dash = stem.LastIndexOf('-');
        return dash >= 0 ? stem[(dash + 1)..] : stem;
    }
}
