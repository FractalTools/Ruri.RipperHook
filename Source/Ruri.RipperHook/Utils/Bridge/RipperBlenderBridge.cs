using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using Ruri.Hook.Config;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.Bridge;

/// <summary>
/// Public, self-contained entry point for an in-process caller (pythonnet, hosted inside Blender) to
/// build/load a cabmap, browse its rows, and pull a selection's fully-resolved model/animation/material/
/// texture data straight into memory. Every method here composes existing, already-working pieces
/// (<see cref="CabMap"/>, AssetRipper's own <see cref="ExportHandler"/>, <see cref="InMemoryFileSystem"/>)
/// — no new export/serialization logic, no AOP hook. The only thing this class adds is: (1) a
/// cabmap-name-seeded scoped load, and (2) pairing the exporter's own ".meta" sidecars back into
/// GUID-keyed dictionaries, exactly the shape a GUID-keyed consumer (a Unity-YAML parser) already expects.
/// </summary>
public static class RipperBlenderBridge
{
    private static bool _loggingConfigured;

    /// <summary>
    /// One-call bootstrap: assembly resolver, a stderr logger sink (AssetRipper logging is a black hole
    /// with no sink attached), and every hook in <paramref name="enabledHookIds"/> (e.g. "EndField_1.3.3").
    /// Safe to call more than once per process — the resolver install and hook application are both
    /// idempotent; the logger sink is only added once.
    /// </summary>
    public static void Initialize(IEnumerable<string> enabledHookIds)
    {
        Bootstrap.InstallAssemblyResolver();

        if (!_loggingConfigured)
        {
            _loggingConfigured = true;
            Logger.Clear();
            Logger.Add(new BridgeLogger { MinLevel = LogType.Info });
        }

        HookConfig config = new();
        foreach (string id in enabledHookIds)
        {
            config.EnabledHooks.Add(id);
        }
        Bootstrap.ApplyHooks(config);
    }

    /// <summary>Scan <paramref name="gameRoot"/> and write a fresh cabmap to <paramref name="outPath"/>.</summary>
    public static int BuildCabMap(string gameRoot, string outPath) => CabMap.Build(gameRoot, outPath);

    /// <summary>Load an existing cabmap file. Returns an opaque handle for <see cref="EnumerateRows"/>/<see cref="ImportCabs"/>.</summary>
    public static CabMapHandle LoadCabMap(string cabMapPath)
    {
        (string baseFolder, Dictionary<string, CabMap.Entry> entries) = CabMap.Load(cabMapPath);
        return new CabMapHandle(cabMapPath, baseFolder, entries);
    }

    /// <summary>Every CAB in the map, projected to the flat browsable row shape (Name/Container/Type/Source/Deps).</summary>
    public static CabRowDto[] EnumerateRows(CabMapHandle map)
    {
        ArgumentNullException.ThrowIfNull(map);
        CabRowDto[] rows = new CabRowDto[map.Entries.Count];
        int i = 0;
        foreach ((string cab, CabMap.Entry entry) in map.Entries)
        {
            rows[i++] = new CabRowDto(
                Cab: cab,
                Name: DisplayName(entry.ContainerPaths),
                Container: string.Join("  |  ", entry.ContainerPaths),
                TypeNames: TypeNames(entry.ClassIds),
                Source: entry.RelativePath,
                DependencyCount: entry.Dependencies.Count);
        }
        return rows;
    }

    /// <summary>Resolve a set of addressable container paths (e.g. <see cref="DiscoverScenePlacements"/>'
    /// <see cref="ScenePlacementDto.AssetPath"/> values) to the CAB names that host them, via
    /// <see cref="CabMap.ResolveCabsForPaths(Dictionary{string, CabMap.Entry}, IEnumerable{string})"/>.
    /// Paths with no match are silently skipped -- compare the input count against the result to check
    /// coverage.</summary>
    public static string[] ResolveCabsForPaths(CabMapHandle map, string[] containerPaths)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveCabsForPaths(map.Entries, containerPaths);
    }

    /// <summary>
    /// Resolve the seed CABs' full dependency closure, load exactly those bundles, run AssetRipper's real
    /// Unity-project exporter against an <see cref="InMemoryFileSystem"/> (the same exporter that backs
    /// the CLI's --export and the GUI's project export — byte-identical output, just memory-backed instead
    /// of disk-backed), and return the result partitioned into GUID-keyed YAML text / PNG bytes.
    /// </summary>
    public static ClosureResult ImportCabs(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(seedCabNames);

        (string[] closureFiles, HashSet<string> loadFilterFileNames) =
            CabMap.ResolveScopedClosure(map.BaseFolder, map.Entries, seedCabNames);
        if (closureFiles.Length == 0)
        {
            return ClosureResult.Empty;
        }

        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
        ExportHandler handler = new(settings);

        GameData gameData;
        GameBundleHook.LoadIncludeFile = loadFilterFileNames.Count > 0 ? name => loadFilterFileNames.Contains(name) : null;
        try
        {
            gameData = handler.LoadAndProcess(closureFiles, LocalFileSystem.Instance);
        }
        finally
        {
            GameBundleHook.LoadIncludeFile = null;
        }

        InMemoryFileSystem memoryFileSystem = new();
        handler.Export(gameData, "mem:/out", memoryFileSystem);

        return Partition(memoryFileSystem.Files);
    }

    // ── raw VFS access + scene-placement discovery ──────────────────────────────────────────────────
    //
    // All four methods below are thin conversions over GameBundleHook's generic (primitive/tuple-typed)
    // delegates -- never touching VirtualFileSystem/SceneChunkReader/EcsBlobDecoder or any other
    // concrete Endfield type directly. The real implementation lives in AssetRipperGameHook/
    // UnityHypergryph/EndField/Utils/StreamingScene/EndfieldSceneBridge.cs, which a VFS game hook wires
    // into those delegates (mirroring the existing ScanChunk/ScanChunkNames/ScanChunkFull pattern).
    // This file lives OUTSIDE AssetRipperGameHook/ and must keep compiling when that whole tree is
    // stripped ($(PureRelease)==true in Ruri.RipperHook.csproj) -- a concrete reference here would break
    // that build the same way GameBundleHook.ActiveVfs (a typed field, since removed) did.

    /// <summary>
    /// Enumerate every file recorded in every .blc manifest across <paramref name="vfsRoots"/> (priority
    /// order, e.g. [Persistent/VFS, StreamingAssets/VFS] -- a hot-update overlay's listing wins over the
    /// base client's when both list the same file), of ANY block type -- not just the Unity-CAB-shaped
    /// entries <see cref="ImportCabs"/> resolves through. This does not extract/decrypt any payload, only
    /// reads the (small, CRC-verified) file tables, so scanning the whole VFS tree is cheap.
    /// <paramref name="blockTypeFilter" /> is an optional set of block-type names (e.g. "Streaming",
    /// "ExtendData") to pre-filter by, to avoid materializing every non-relevant entry.
    /// </summary>
    public static VfsFileDto[] EnumerateVfsFiles(string[] vfsRoots, string[]? blockTypeFilter = null) =>
        VfsFuncOrThrow(GameBundleHook.EnumerateVfsFiles)(vfsRoots, blockTypeFilter)
            .Select(f => new VfsFileDto(f.FileName, f.FileNameHash, f.BlockType, f.Length, f.ChkPath))
            .ToArray();

    /// <summary>
    /// Extract + decrypt one VFS-packed file's raw bytes by its exact original name (as returned by
    /// <see cref="EnumerateVfsFiles"/>'s <see cref="VfsFileDto.FileName"/>), trying <paramref name="vfsRoots"/>
    /// in priority order with fallback (a hot-update overlay can list a chunk it never duplicated because
    /// that patch didn't change it; confirmed against the real game -- see EndfieldSceneBridge.cs).
    /// </summary>
    public static byte[] ExtractVfsFile(string[] vfsRoots, string fileName) =>
        VfsFuncOrThrow(GameBundleHook.ExtractVfsFile)(vfsRoots, fileName);

    /// <summary>Every distinct map name with streaming-chunk data across <paramref name="vfsRoots"/>
    /// (i.e. every "&lt;map&gt;" in "Data/Streaming/PC/&lt;map&gt;/Streaming/*.bytes").</summary>
    public static string[] EnumerateSceneMaps(string[] vfsRoots) =>
        VfsFuncOrThrow(GameBundleHook.EnumerateSceneMaps)(vfsRoots);

    /// <summary>
    /// Discover every mesh-bearing entity placement for <paramref name="mapName"/>'s STREAMING chunks
    /// (Data/Streaming/PC/&lt;map&gt;/Streaming/*.bytes -- static world geometry/props/colliders), across
    /// <paramref name="vfsRoots"/> in priority order. Cheap: only the hash LUT + chunk files are
    /// extracted/decoded, no dependency closure is resolved and no CAB is loaded -- the caller resolves
    /// AssetPath -> CAB separately (see <see cref="ResolveCabsForPaths"/>) only for whichever placements
    /// it actually wants to import. See EndfieldSceneBridge.DiscoverScenePlacements for the full
    /// implementation notes (transform-resolution priority, the STREAMING-vs-DynamicStreaming scope
    /// boundary, and the ReverseNotes.md caveat on Mono/Proxy entities).
    /// </summary>
    public static ScenePlacementDto[] DiscoverScenePlacements(string[] vfsRoots, string mapName) =>
        VfsFuncOrThrow(GameBundleHook.DiscoverScenePlacements)(vfsRoots, mapName)
            .Select(p => new ScenePlacementDto(p.AssetPath, p.AssetHash, p.EntityName, p.SourceChunk, p.HasTransform,
                p.Px, p.Py, p.Pz, p.Qx, p.Qy, p.Qz, p.Qw, p.Sx, p.Sy, p.Sz, p.MaterialAssetPaths))
            .ToArray();

    private static T VfsFuncOrThrow<T>(T? func) where T : class =>
        func ?? throw new InvalidOperationException(
            "No VFS game hook active -- call Initialize(...) with a VFS-game hook id (e.g. \"EndField_1.3.3\") first.");

    private static ClosureResult Partition(IReadOnlyDictionary<string, byte[]> files)
    {
        Dictionary<string, string> documents = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> textures = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> other = new(StringComparer.OrdinalIgnoreCase);
        List<string> roots = new();
        UTF8Encoding utf8 = new(false);

        foreach ((string path, byte[] bytes) in files)
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue; // paired below, from the content file's side
            }
            if (!files.TryGetValue(path + ".meta", out byte[]? metaBytes))
            {
                other[path] = bytes;
                continue;
            }
            string? guid = ExtractGuid(metaBytes, utf8);
            if (guid is null)
            {
                other[path] = bytes;
                continue;
            }
            if (IsPng(bytes))
            {
                textures[guid] = bytes;
                continue;
            }

            documents[guid] = utf8.GetString(bytes);
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(guid);
            }
        }

        return new ClosureResult(documents, textures, other, roots.ToArray());
    }

    private static readonly Regex GuidPattern = new(@"guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

    private static string? ExtractGuid(byte[] metaBytes, UTF8Encoding utf8)
    {
        Match match = GuidPattern.Match(utf8.GetString(metaBytes));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    /// <summary>First container path's leaf name, "(+N)" suffixed when the CAB has more than one; the raw CAB hash has already been substituted by the caller when there are none.</summary>
    private static string DisplayName(IReadOnlyList<string> containerPaths)
    {
        if (containerPaths.Count == 0)
        {
            return string.Empty;
        }
        string path = containerPaths[0];
        int slash = path.LastIndexOf('/');
        string leaf = slash >= 0 ? path[(slash + 1)..] : path;
        return containerPaths.Count > 1 ? $"{leaf} (+{containerPaths.Count - 1})" : leaf;
    }

    private static string TypeNames(IReadOnlyList<int> classIds)
    {
        List<string> names = new();
        foreach (int id in classIds)
        {
            if (id == (int)ClassIDType.AssetBundle)
            {
                continue;
            }
            names.Add(Enum.IsDefined(typeof(ClassIDType), id) ? ((ClassIDType)id).ToString() : id.ToString());
        }
        return names.Count > 0 ? string.Join(", ", names) : nameof(ClassIDType.AssetBundle);
    }
}

/// <summary>Opaque handle to a loaded cabmap — the base folder it was built from plus its CAB entries.</summary>
public sealed class CabMapHandle
{
    public string CabMapPath { get; }
    public string BaseFolder { get; }
    public Dictionary<string, CabMap.Entry> Entries { get; }

    internal CabMapHandle(string cabMapPath, string baseFolder, Dictionary<string, CabMap.Entry> entries)
    {
        CabMapPath = cabMapPath;
        BaseFolder = baseFolder;
        Entries = entries;
    }
}

/// <summary>One browsable row — mirrors the WinForms "Virtual Asset List" columns 1:1.</summary>
public sealed record CabRowDto(string Cab, string Name, string Container, string TypeNames, string Source, int DependencyCount);

/// <summary>One file inside the VFS, as returned by <see cref="RipperBlenderBridge.EnumerateVfsFiles"/> — its
/// exact original name (the lookup key <see cref="RipperBlenderBridge.ExtractVfsFile"/> takes), its
/// EVFSBlockType name (e.g. "Streaming", "ExtendData"), its decrypted length, and which .chk it lives in
/// (informational only; callers extract by name, not by chunk path).</summary>
public sealed record VfsFileDto(string FileName, long FileNameHash, string BlockType, long Length, string ChkPath);

/// <summary>One mesh-bearing entity placement discovered by <see cref="RipperBlenderBridge.DiscoverScenePlacements"/>.
/// AssetPath is the resolved (hash-LUT) original addressable path -- empty when the hash didn't resolve.
/// HasTransform false means no ground-truth-verified transform source was found for this entity (see the
/// method's doc comment); Px..Sz are all zero/identity in that case and callers should treat this as "don't
/// place," not "place at the origin." MaterialAssetPaths is this entity's own resolved material(s) -- same
/// hash-LUT source as AssetPath, just the sibling AssetType==1 property entries instead of ==2; empty when
/// the entity carries none or none resolved.</summary>
public sealed record ScenePlacementDto(
    string AssetPath, long AssetHash, string EntityName, string SourceChunk, bool HasTransform,
    float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw, float Sx, float Sy, float Sz,
    string[] MaterialAssetPaths);

/// <summary>
/// The in-memory import payload for a resolved selection: Unity-project YAML text and texture PNG bytes,
/// both GUID-keyed (matching the {fileID, guid} cross-references already embedded in the YAML text
/// itself), plus anything else the exporter wrote that isn't a recognized text/image pair, and the GUIDs
/// of the top-level (.prefab) assets that should actually be imported.
/// </summary>
public sealed record ClosureResult(
    IReadOnlyDictionary<string, string> Documents,
    IReadOnlyDictionary<string, byte[]> Textures,
    IReadOnlyDictionary<string, byte[]> OtherFiles,
    string[] Roots)
{
    public static ClosureResult Empty { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, byte[]>(),
        new Dictionary<string, byte[]>(),
        Array.Empty<string>());
}
