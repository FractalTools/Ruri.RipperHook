using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
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
    /// Every hook id (<c>GameName_Version</c>, e.g. "EndField_1.3.3") compiled into this build, discovered
    /// via <see cref="Ruri.Hook.RuriHook.GetAvailableHooks"/> reflection over already-loaded assemblies --
    /// no <see cref="Initialize"/> call required first, since hook discovery only needs this DLL's own
    /// assembly (which carries every <c>AssetRipperGameHook</c> hook type) to already be loaded, which it
    /// is by the time a caller can reach this static class at all. This is what an in-process caller (the
    /// Blender addon's Hook picker) should populate its selectable hook list from, instead of hardcoding
    /// or free-typing ids.
    /// </summary>
    public static string[] ListAvailableHooks() =>
        Ruri.Hook.RuriHook.GetAvailableHooks()
            .Select(h => $"{h.Attribute.GameName}_{h.Attribute.Version}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    /// <summary>Per-row cap on the joined Container display/search string. A non-bundled
    /// resources.assets row can carry tens of thousands of harvested asset names (see
    /// GameBundleHook.HarvestAssetNames) -- joining ALL of them into every row's search string would
    /// multiply the browser's memory by orders of magnitude for no search benefit past this point.
    /// The truncation is explicit in the string itself (never silent), and the full name list stays
    /// intact in the map/Entry for programmatic use.</summary>
    private const int MaxContainerJoinChars = 16384;

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
                Container: JoinContainerPaths(entry.ContainerPaths),
                TypeNames: TypeNames(entry.ClassIds),
                Source: entry.RelativePath,
                DependencyCount: entry.Dependencies.Count);
        }
        return rows;
    }

    private static string JoinContainerPaths(IReadOnlyList<string> containerPaths)
    {
        StringBuilder sb = new();
        for (int i = 0; i < containerPaths.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  |  ");
            }
            if (sb.Length + containerPaths[i].Length > MaxContainerJoinChars)
            {
                sb.Append($"…(+{containerPaths.Count - i} more names)");
                break;
            }
            sb.Append(containerPaths[i]);
        }
        return sb.ToString();
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

    /// <summary>Pure in-memory dependency-closure CAB-name enumeration for the given seed CABs -- see
    /// <see cref="CabMap.ResolveClosureCabNames"/>. No VFS decrypt, no AssetRipper export; just the
    /// already-loaded cabmap's own dependency graph. Pair with <see cref="EnumerateRows"/>' own
    /// TypeNames (already loaded per CAB) to answer "does this prefab's closure include an
    /// AnimationClip" without resolving/exporting anything.</summary>
    public static string[] ResolveClosureCabNames(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveClosureCabNames(map.Entries, seedCabNames);
    }

    /// <summary>
    /// For a CAB that hosts AnimationClips (and typically nothing else), find EVERY CAB carrying an Avatar
    /// (ClassID 90) in the clip's dependency neighborhood, nearest first -- the assets a standalone clip
    /// import co-loads so (a) AssetRipper's own AnimationClipConverter can restore the clips' hashed curve
    /// paths to real transform-path strings (confirmed against the real game: a clip CAB alone has NO
    /// dependencies, its exported curve paths come out as "path_0x&lt;CRC32&gt;_&lt;suffix&gt;"
    /// placeholders; co-seeding the rig-FBX CAB flips every one of them to a full "Root/Bip001/..."
    /// string, byte-identical to what a whole-character export produces), and (b) the caller can build a
    /// humanoid muscle retargeter from the rig's REAL Avatar.
    /// Returns ALL candidates (BFS order, capped) rather than the first hit, because the neighborhood
    /// routinely contains multiple Avatar assets of very different quality -- confirmed against the real
    /// game: pelica's battle rig neighborhood surfaces a 7KB stub Avatar (empty m_TOS, all-zero m_ID,
    /// no usable skeleton) BEFORE the real 334KB SK_actor_pelica_01Avatar (full m_TOS + muscle
    /// referential). WHICH one is usable is a content question the caller answers by trying to build a
    /// retargeter from each in order -- name/size heuristics here would be exactly the kind of guessing
    /// this bridge exists to avoid.
    /// Search shape mirrors the data's real topology (verified via harness): the Avatar is never among the
    /// clip's reverse dependents themselves (those are the AnimatorController, then the character prefabs)
    /// -- it lives in the FORWARD closure of those dependents. So: breadth-first over reverse dependents
    /// (nearest first, pure in-memory cabmap graph), scanning each one's forward closure for Avatar-classed
    /// CABs. Empty when the clip has no Avatar anywhere in its neighborhood. Cheap: the reverse adjacency
    /// index is built once per loaded map (lazily, cached on the handle).
    /// </summary>
    public static string[] FindAssociatedAvatarCabs(CabMapHandle map, string clipCabName, int maxCandidates = 4)
    {
        ArgumentNullException.ThrowIfNull(map);
        Dictionary<string, List<string>> reverse = map.ReverseIndex;

        List<string> found = new();
        HashSet<string> foundSet = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { clipCabName };
        Queue<string> queue = new();
        queue.Enqueue(clipCabName);
        while (queue.Count > 0 && found.Count < maxCandidates)
        {
            string current = queue.Dequeue();
            if (!reverse.TryGetValue(current, out List<string>? dependents))
            {
                continue;
            }
            foreach (string dependent in dependents)
            {
                if (!visited.Add(dependent))
                {
                    continue;
                }
                foreach (string cab in CabMap.ResolveClosureCabNames(map.Entries, new[] { dependent }))
                {
                    if (map.Entries.TryGetValue(cab, out CabMap.Entry? entry)
                        && entry.ClassIds.Contains((int)ClassIDType.Avatar)
                        && foundSet.Add(cab))
                    {
                        found.Add(cab);
                        if (found.Count >= maxCandidates)
                        {
                            break;
                        }
                    }
                }
                if (found.Count >= maxCandidates)
                {
                    break;
                }
                queue.Enqueue(dependent);
            }
        }
        return found.ToArray();
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
        ClipCaptureExporter clipCapture = new();
        BridgeExportHandler handler = new(settings, clipCapture);

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

        return Partition(memoryFileSystem.Files, map.Entries, seedCabNames, clipCapture.Captured);
    }

    /// <summary>
    /// <see cref="ExportHandler"/> whose only delta is surfacing the <see cref="ExportHandler.BeforeExport"/>
    /// extension point (upstream's own customization seam -- "Needed for the premium edition") to register
    /// <see cref="ClipCaptureExporter"/> on the freshly-built <see cref="ProjectExporter"/>. Pure composition
    /// over AssetRipper's public exporter stack; no AOP hook, so this works identically under every hook
    /// configuration (including none) and in the $(PureRelease) build that strips AssetRipperGameHook/.
    /// </summary>
    private sealed class BridgeExportHandler : ExportHandler
    {
        private readonly ClipCaptureExporter _clipCapture;

        public BridgeExportHandler(FullConfiguration settings, ClipCaptureExporter clipCapture) : base(settings)
        {
            _clipCapture = clipCapture;
        }

        protected override void BeforeExport(ProjectExporter projectExporter) =>
            projectExporter.OverrideExporter<IAnimationClip>(_clipCapture, allowInheritance: true);
    }

    /// <summary>
    /// Decorator over AssetRipper's own <see cref="DefaultYamlExporter"/> that records, for every
    /// AnimationClip it exports, WHICH source collection (CAB) the asset came from and the exact file path
    /// the exporter actually wrote (including any name-collision uniquification suffix). This is the
    /// cabmap-identity bridge for clips: a clip's CAB container path is its host FBX
    /// ("...a_x_01.fbx") while its exported file is named after the clip's own m_Name
    /// ("...A_x_ACL.anim") -- confirmed against the real game, the two stems genuinely differ, so no
    /// path/name normalization can join them after the fact. The asset object itself is the only thing
    /// that carries both identities (asset.Collection.Name == the cabmap's CAB key), and the export call
    /// is the only point where that asset meets its final output path -- so capture exactly there.
    /// TryCreateCollection mirrors DefaultYamlExporter's body verbatim, just with THIS exporter installed
    /// on the collection so the collection's ExportInner routes back through the capturing Export below.
    /// </summary>
    private sealed class ClipCaptureExporter : IAssetExporter
    {
        private readonly DefaultYamlExporter _inner = new();

        /// <summary>(lowercased CAB name, exported file path) per exported AnimationClip.</summary>
        public List<(string Cab, string Path)> Captured { get; } = new();

        public bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            exportCollection = new AssetExportCollection<IUnityObjectBase>(this, asset);
            return true;
        }

        public bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
        {
            Captured.Add((asset.Collection.Name.ToLowerInvariant(), path));
            return _inner.Export(container, asset, path, fileSystem);
        }

        public void Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem, Action<IExportContainer, IUnityObjectBase, string, FileSystem>? callback)
        {
            Export(container, asset, path, fileSystem);
            callback?.Invoke(container, asset, path, fileSystem);
        }

        public bool Export(IExportContainer container, IEnumerable<IUnityObjectBase> assets, string path, FileSystem fileSystem) =>
            _inner.Export(container, assets, path, fileSystem);

        public void Export(IExportContainer container, IEnumerable<IUnityObjectBase> assets, string path, FileSystem fileSystem, Action<IExportContainer, IUnityObjectBase, string, FileSystem>? callback) =>
            _inner.Export(container, assets, path, fileSystem, callback);

        public AssetType ToExportType(IUnityObjectBase asset) => _inner.ToExportType(asset);

        public bool ToUnknownExportType(Type type, out AssetType assetType) => _inner.ToUnknownExportType(type, out assetType);
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

    /// <summary>Binary/vtable-level schema-drift diagnostic for <paramref name="mapName"/>'s streaming
    /// chunks -- one report line per FlatBuffers table type, flagging any type where the live game's
    /// data declares more fields than our (1.2.4-era) generated bindings know how to read, plus sample
    /// raw dumps of the extra field bytes. See EndfieldSceneBridge.DiagnoseSchemaDrift's doc comment.</summary>
    public static string[] DiagnoseSchemaDrift(string[] vfsRoots, string mapName) =>
        VfsFuncOrThrow(GameBundleHook.DiagnoseSchemaDrift)(vfsRoots, mapName);

    private static T VfsFuncOrThrow<T>(T? func) where T : class =>
        func ?? throw new InvalidOperationException(
            "No VFS game hook active -- call Initialize(...) with a VFS-game hook id (e.g. \"EndField_1.3.3\") first.");

    private static ClosureResult Partition(IReadOnlyDictionary<string, byte[]> files,
        Dictionary<string, CabMap.Entry> entries, string[] seedCabNames,
        List<(string Cab, string Path)> capturedClips)
    {
        Dictionary<string, string> documents = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> textures = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> other = new(StringComparer.OrdinalIgnoreCase);
        List<string> roots = new();
        List<string> sceneRoots = new();
        // Normalized export path -> guid, used ONLY to resolve each seed CAB's own root below (see
        // SeedRoots) -- not part of the returned payload itself. AssetRipper's OriginalPathProcessor
        // sets every asset's export path (asset.OriginalPath, "Assets/<...>") straight from the SAME
        // AssetBundle.Container addressable-path key CabMap.Entry.ContainerPaths is itself built from
        // (see AssetRipper.Processing/Scenes/OriginalPathProcessor.cs's SetOriginalPaths) -- so a seed
        // CAB's own ContainerPaths entries key into this map directly, no name/identity guessing.
        Dictionary<string, string> pathToGuid = new(StringComparer.OrdinalIgnoreCase);
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
            pathToGuid[NormalizeExportPath(path)] = guid;
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
            else if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                // A non-bundled build's GameObject hierarchies (level0/level1/... -- the actual
                // level + character models) export as SCENE files, not prefabs; without this the
                // whole scene body silently vanished from the importable roots (confirmed against
                // a real plain 2019.4 player build: the level closure exported 33 shared SFX
                // .prefabs and the level itself as Assets/Scenes/level0.unity -- "imported fine",
                // zero level geometry). A scene document is the same GameObject/Transform/renderer
                // document stream a .prefab is, so the same importer consumes it.
                roots.Add(guid);
                sceneRoots.Add(guid);
            }
        }

        Dictionary<string, string> seedRoots = new(StringComparer.Ordinal);
        foreach (string seedCab in seedCabNames)
        {
            if (!entries.TryGetValue(seedCab, out CabMap.Entry? entry))
            {
                continue;
            }

            // Per-asset virtual row ("<hostFile>::<pathID>", see GameBundleHook.ReadFullMetadataRows):
            // its ContainerPaths[0] is the asset's own m_Name and ClassIds[0] its real class. The
            // exporter names the output file from the SAME m_Name field (GetUniqueFileName <-
            // GetBestName), so stem+class-extension is a same-field round trip, not a heterogeneous
            // display-name guess.
            if (seedCab.Contains(GameBundleHook.AssetRowSeparator, StringComparison.Ordinal)
                && entry.ContainerPaths.Count == 1 && entry.ClassIds.Count == 1)
            {
                string? assetGuid = ResolveAssetRowGuid(pathToGuid, entry.ContainerPaths[0], entry.ClassIds[0]);
                if (assetGuid is not null)
                {
                    seedRoots[seedCab] = assetGuid;
                }
                continue;
            }

            foreach (string containerPath in entry.ContainerPaths)
            {
                if (pathToGuid.TryGetValue(NormalizeExportPath(containerPath), out string? guid))
                {
                    seedRoots[seedCab] = guid;
                    break;
                }
            }
            if (seedRoots.ContainsKey(seedCab))
            {
                continue;
            }
            // Non-bundled seed (no addressable container path exists anywhere): its own GameObject
            // hierarchy exports as a scene named after the serialized FILE itself
            // (SceneDefinitionProcessor derives the scene name from the file name), so the seed's
            // identity join is "assets/scenes/<cab>.unity" -- still the cabmap's own key, no
            // display-name guessing.
            if (entry.ContainerPaths.Count == 0
                && pathToGuid.TryGetValue($"assets/scenes/{seedCab.ToLowerInvariant()}.unity", out string? sceneGuid))
            {
                seedRoots[seedCab] = sceneGuid;
            }
        }

        // ClipCaptureExporter recorded (CAB, actual exported path) per AnimationClip; the .meta pass above
        // already mapped every exported path to its guid, so the join here is exact -- the SAME path string
        // the exporter wrote, not a reconstruction, so name-collision uniquification suffixes can't desync it.
        Dictionary<string, string[]> clipGuidsByCab = capturedClips
            .Select(c => (c.Cab, Guid: pathToGuid.GetValueOrDefault(NormalizeExportPath(c.Path))))
            .Where(c => c.Guid is not null)
            .GroupBy(c => c.Cab, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Guid!).Distinct().ToArray(), StringComparer.Ordinal);

        return new ClosureResult(documents, textures, other, roots.ToArray(), seedRoots, clipGuidsByCab,
            sceneRoots.ToArray());
    }

    /// <summary>Resolve a per-asset virtual row (asset m_Name + ClassID) to its exported guid: scan the
    /// export's path->guid table for files whose stem equals the name, preferring one whose extension
    /// matches the class (AssetRipper's per-class output extensions, mirroring ExportCollection.
    /// GetExportExtension). Unique-stem hits with a foreign extension still count (a class this table
    /// doesn't list exports as ".asset" anyway); ambiguous stems without an extension match resolve to
    /// nothing rather than to a guess.</summary>
    private static string? ResolveAssetRowGuid(Dictionary<string, string> pathToGuid, string assetName, int classId)
    {
        string wantStem = assetName.ToLowerInvariant();
        string? wantExt = classId switch
        {
            (int)ClassIDType.AnimationClip => ".anim",
            (int)ClassIDType.Material => ".mat",
            (int)ClassIDType.Shader => ".shader",
            (int)ClassIDType.AnimatorController => ".controller",
            (int)ClassIDType.Texture2D or (int)ClassIDType.Cubemap => ".png",
            (int)ClassIDType.GameObject => ".prefab",
            (int)ClassIDType.MonoScript => ".cs",
            (int)ClassIDType.AudioClip => null, // exporter emits the source container format (.ogg/.wav/...)
            _ => ".asset",
        };

        string? extMatch = null;
        string? stemMatch = null;
        int stemMatches = 0;
        foreach ((string path, string guid) in pathToGuid)
        {
            int slash = path.LastIndexOf('/');
            ReadOnlySpan<char> leaf = slash >= 0 ? path.AsSpan(slash + 1) : path.AsSpan();
            int dot = leaf.LastIndexOf('.');
            ReadOnlySpan<char> stem = dot >= 0 ? leaf[..dot] : leaf;
            if (!stem.Equals(wantStem, StringComparison.Ordinal))
            {
                continue;
            }
            if (wantExt is not null && dot >= 0 && leaf[dot..].Equals(wantExt, StringComparison.Ordinal))
            {
                if (extMatch is not null)
                {
                    return null; // two same-named same-class assets -- refuse to guess
                }
                extMatch = guid;
            }
            stemMatch = guid;
            stemMatches++;
        }
        return extMatch ?? (stemMatches == 1 ? stemMatch : null);
    }

    /// <summary>Normalizes an export-side path (actually "mem:/out\ExportedProject\Assets\beyond\...\x.prefab"
    /// on Windows -- backslashes throughout, plus an "ExportedProject\" segment neither the cabmap side nor
    /// the old forward-slash-only doc comment here accounted for; confirmed via a direct dump of
    /// InMemoryFileSystem.Files' actual keys, not assumed) or a cabmap-side <see cref="CabMap.Entry.ContainerPaths"/>
    /// entry ("assets/beyond/.../x.prefab", forward slashes, no export-root prefix) to the same comparable
    /// key: backslashes normalized to forward slashes first (so "Assets/" search works on both sides
    /// regardless of Path.DirectorySeparatorChar), anchored at "Assets/" (dropping any export root prefix),
    /// "##subObjectName" suffix stripped (mirrors CabMap's own container-path normalization), lowercased.</summary>
    private static string NormalizeExportPath(string path)
    {
        string slashed = path.Replace('\\', '/');
        int hashIdx = slashed.IndexOf("##", StringComparison.Ordinal);
        string trimmed = hashIdx >= 0 ? slashed[..hashIdx] : slashed;
        int assetsIdx = trimmed.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
        if (assetsIdx >= 0)
        {
            trimmed = trimmed[assetsIdx..];
        }
        return trimmed.ToLowerInvariant();
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

    private Dictionary<string, List<string>>? _reverseIndex;

    /// <summary>dependency CAB -> the CABs that directly depend on it, built lazily once per handle
    /// (~1M edges over a full EndField map, sub-second) -- backs
    /// <see cref="RipperBlenderBridge.FindAssociatedAvatarCab"/>'s nearest-first reverse walk.</summary>
    internal Dictionary<string, List<string>> ReverseIndex
    {
        get
        {
            if (_reverseIndex is null)
            {
                Dictionary<string, List<string>> reverse = new(StringComparer.OrdinalIgnoreCase);
                foreach ((string cab, CabMap.Entry entry) in Entries)
                {
                    foreach (string dep in entry.Dependencies)
                    {
                        if (!reverse.TryGetValue(dep, out List<string>? list))
                        {
                            reverse[dep] = list = new List<string>();
                        }
                        list.Add(cab);
                    }
                }
                _reverseIndex = reverse;
            }
            return _reverseIndex;
        }
    }

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
/// itself), plus anything else the exporter wrote that isn't a recognized text/image pair, the GUIDs
/// of the top-level (.prefab) assets that should actually be imported, and -- for each requested seed
/// CAB name that resolved to its own exported asset -- that seed's own guid (<see cref="SeedRoots"/>).
/// A single seed CAB's closure routinely resolves to MORE than one root .prefab (e.g. an actor prefab
/// pulling in a separate portrait/"uimodel" variant as a second top-level asset); SeedRoots is how a
/// caller identifies WHICH of <see cref="Roots"/> is the one it actually asked for, directly through the
/// cabmap's own CAB-name/addressable-path identity (see RipperBlenderBridge.Partition/NormalizeExportPath)
/// -- never by comparing display names or GameObject names, which Unity gives no guarantee equal each
/// other even for a single unambiguous seed.
/// <see cref="ClipGuidsByCab"/> is the same identity principle applied to AnimationClips: lowercased
/// CAB name -> the exported clip guid(s) that CAB hosts, captured asset-side during export
/// (<see cref="RipperBlenderBridge.ClipCaptureExporter"/>) because a clip CAB's addressable path is its
/// host FBX while the exported .anim is named after the clip's own m_Name -- one CAB can host several
/// clips, hence guid array. This is how a caller translates a cheaply-discovered clip CAB row (cabmap
/// metadata only, no export yet) into the real clip documents once the closure HAS been exported,
/// without any display-name/m_Name matching.
/// </summary>
public sealed record ClosureResult(
    IReadOnlyDictionary<string, string> Documents,
    IReadOnlyDictionary<string, byte[]> Textures,
    IReadOnlyDictionary<string, byte[]> OtherFiles,
    string[] Roots,
    IReadOnlyDictionary<string, string> SeedRoots,
    IReadOnlyDictionary<string, string[]> ClipGuidsByCab,
    string[] SceneRoots)
{
    public static ClosureResult Empty { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, byte[]>(),
        new Dictionary<string, byte[]>(),
        Array.Empty<string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string[]>(),
        Array.Empty<string>());
}
