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
        return new CabMapHandle(cabMapPath, CabMap.LoadTable(cabMapPath));
    }

    /// <summary>
    /// The row table as the RAW columnar buffers the map was loaded as -- ZERO per-row work on
    /// either side of the interop boundary. The python consumer decodes each blob once, derives
    /// display strings (leaf name, joined container list, type names) lazily for just the ~500
    /// rows in its visible window, and runs its quick-search directly over the blobs' text.
    /// Offset buffers are little-endian int32; ClassIdNames maps every distinct class id present
    /// to its <see cref="ClassIDType"/> name ("id=Name" per line).
    /// </summary>
    public static PackedTableDto EnumerateTablePacked(CabMapHandle map)
    {
        ArgumentNullException.ThrowIfNull(map);
        CabTable table = map.Table;
        int count = table.Count;

        // Entry-only cab column: offsets [0..Count] index the shared blob whose tail may hold
        // phantom names -- slice the blob to the last real entry's end.
        byte[] cabBlob = new byte[table.CabOffsets[count]];
        Buffer.BlockCopy(table.CabBlob, 0, cabBlob, 0, cabBlob.Length);

        int[] dependencyCounts = new int[count];
        for (int id = 0; id < count; id++)
        {
            dependencyCounts[id] = table.DependencyCount(id);
        }

        HashSet<int> distinctClassIds = new();
        foreach (int classId in table.ClassIdsFlat)
        {
            distinctClassIds.Add(classId);
        }
        StringBuilder classNames = new();
        foreach (int classId in distinctClassIds)
        {
            classNames.Append(classId).Append('=')
                .Append(Enum.IsDefined(typeof(ClassIDType), classId)
                    ? ((ClassIDType)classId).ToString() : classId.ToString())
                .Append('\n');
        }

        return new PackedTableDto(
            Count: count,
            CabBlob: cabBlob,
            CabOffsets: IntsToBytes(table.CabOffsets, count + 1),
            SourceBlob: table.RelativePathBlob,
            SourceOffsets: IntsToBytes(table.RelativePathOffsets, count + 1),
            PathBlob: table.ContainerPathBlob,
            PathOffsets: IntsToBytes(table.ContainerPathOffsets, table.ContainerPathOffsets.Length),
            PathStarts: IntsToBytes(table.ContainerPathStarts, count + 1),
            ClassFlat: IntsToBytes(table.ClassIdsFlat, table.ClassIdsFlat.Length),
            ClassStarts: IntsToBytes(table.ClassIdStarts, count + 1),
            DependencyCounts: IntsToBytes(dependencyCounts, count),
            ClassIdNames: classNames.ToString());
    }

    private static byte[] IntsToBytes(int[] values, int count)
    {
        byte[] bytes = new byte[count * sizeof(int)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Resolve a set of addressable container paths (e.g. <see cref="DiscoverScenePlacements"/>'
    /// <see cref="ScenePlacementDto.AssetPath"/> values) to the CAB names that host them, via
    /// <see cref="CabMap.ResolveCabsForPaths(CabTable, IEnumerable{string})"/>.
    /// Paths with no match are silently skipped -- compare the input count against the result to check
    /// coverage.</summary>
    public static string[] ResolveCabsForPaths(CabMapHandle map, string[] containerPaths)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveCabsForPaths(map.Table, containerPaths);
    }

    /// <summary>Pure in-memory dependency-closure CAB-name enumeration for the given seed CABs -- see
    /// <see cref="CabMap.ResolveClosureCabNames"/>. No VFS decrypt, no AssetRipper export; just the
    /// already-loaded cabmap's own dependency graph. Pair with <see cref="EnumerateRows"/>' own
    /// TypeNames (already loaded per CAB) to answer "does this prefab's closure include an
    /// AnimationClip" without resolving/exporting anything.</summary>
    public static string[] ResolveClosureCabNames(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveClosureCabNames(map.Table, seedCabNames);
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
        CabTable table = map.Table;
        if (!table.CabToId.TryGetValue(clipCabName, out int clipId))
        {
            return Array.Empty<string>();
        }
        int[][] reverse = table.ReverseAdjacency;

        List<string> found = new();
        HashSet<int> foundSet = new();
        bool[] visited = new bool[table.Count + table.PhantomCount];
        visited[clipId] = true;
        Queue<int> queue = new();
        queue.Enqueue(clipId);
        int avatarClassId = (int)ClassIDType.Avatar;
        while (queue.Count > 0 && found.Count < maxCandidates)
        {
            int current = queue.Dequeue();
            foreach (int dependent in reverse[current])
            {
                if (visited[dependent])
                {
                    continue;
                }
                visited[dependent] = true;
                // Per-dependent forward closure, Avatar hits reported in case-insensitive
                // name order -- matching the classic implementation, whose per-dependent
                // scan iterated an alphabetically sorted closure.
                List<string> hits = new();
                foreach (int id in table.ClosureIds(new[] { dependent }))
                {
                    if (id < table.Count && !foundSet.Contains(id)
                        && table.ClassIds(id).Contains(avatarClassId))
                    {
                        foundSet.Add(id);
                        hits.Add(table.CabName(id));
                    }
                }
                hits.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string hit in hits)
                {
                    found.Add(hit);
                    if (found.Count >= maxCandidates)
                    {
                        break;
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
            CabMap.ResolveScopedClosure(map.Table, seedCabNames);
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

        return Partition(memoryFileSystem.Files, map.Table, seedCabNames, clipCapture.Captured);
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

        /// <summary>(lowercased CAB name, exported file path, curve blob) per exported AnimationClip.
        /// MetaJson/Curves are the clip's <see cref="ClipCurveBlob"/> payload -- the editor-format
        /// curves handed straight across the bridge so the Blender side never re-parses them out of
        /// the (potentially 80+MB) YAML text; empty for a clip whose blob build failed (the YAML
        /// document still exists, so the consumer just falls back to parsing it).</summary>
        public List<(string Cab, string Path, string MetaJson, byte[] Curves)> Captured { get; } = new();

        public bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            exportCollection = new AssetExportCollection<IUnityObjectBase>(this, asset);
            return true;
        }

        public bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
        {
            string metaJson = string.Empty;
            byte[] curves = Array.Empty<byte>();
            if (asset is IAnimationClip animationClip)
            {
                try
                {
                    (metaJson, curves) = ClipCurveBlob.Build(animationClip);
                }
                catch (Exception exception)
                {
                    Logger.Warning(LogCategory.Export, $"Clip curve blob failed for '{asset.GetBestName()}': {exception.Message} -- Blender side falls back to YAML parsing.");
                }
            }
            Captured.Add((asset.Collection.Name.ToLowerInvariant(), path, metaJson, curves));
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
        CabTable table, string[] seedCabNames,
        List<(string Cab, string Path, string MetaJson, byte[] Curves)> capturedClips)
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
            if (!table.CabToId.TryGetValue(seedCab, out int seedId) || seedId >= table.Count)
            {
                continue;
            }
            int pathCount = table.ContainerPathCount(seedId);

            // Per-asset virtual row ("<hostFile>::<pathID>", see GameBundleHook.ReadFullMetadataRows):
            // its ContainerPaths[0] is the asset's own m_Name and ClassIds[0] its real class. The
            // exporter names the output file from the SAME m_Name field (GetUniqueFileName <-
            // GetBestName), so stem+class-extension is a same-field round trip, not a heterogeneous
            // display-name guess.
            if (seedCab.Contains(GameBundleHook.AssetRowSeparator, StringComparison.Ordinal)
                && pathCount == 1 && table.ClassIds(seedId).Length == 1)
            {
                string? assetGuid = ResolveAssetRowGuid(pathToGuid, table.ContainerPath(seedId, 0),
                    table.ClassIds(seedId)[0]);
                if (assetGuid is not null)
                {
                    seedRoots[seedCab] = assetGuid;
                }
                continue;
            }

            for (int p = 0; p < pathCount; p++)
            {
                if (pathToGuid.TryGetValue(NormalizeExportPath(table.ContainerPath(seedId, p)), out string? guid))
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
            if (pathCount == 0
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

        // Same exact join for the per-clip curve blobs (see ClipCurveBlob): guid-keyed so the
        // Blender side can look one up straight from a clip guid and skip YAML parsing entirely.
        Dictionary<string, string> clipCurveMeta = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> clipCurveData = new(StringComparer.Ordinal);
        foreach ((string _, string path, string metaJson, byte[] curves) in capturedClips)
        {
            if (metaJson.Length == 0)
            {
                continue; // blob build failed for this clip -- YAML fallback
            }
            string? guid = pathToGuid.GetValueOrDefault(NormalizeExportPath(path));
            if (guid is not null)
            {
                clipCurveMeta[guid] = metaJson;
                clipCurveData[guid] = curves;
            }
        }

        return new ClosureResult(documents, textures, other, roots.ToArray(), seedRoots, clipGuidsByCab,
            sceneRoots.ToArray(), clipCurveMeta, clipCurveData);
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
}

/// <summary>Opaque handle to a loaded cabmap — the columnar <see cref="CabTable"/> (blobs +
/// offsets + int dependency graph; see CabTable.cs for why nothing per-entry is materialized).</summary>
public sealed class CabMapHandle
{
    public string CabMapPath { get; }
    public CabTable Table { get; }
    public string BaseFolder => Table.BaseFolder;

    internal CabMapHandle(string cabMapPath, CabTable table)
    {
        CabMapPath = cabMapPath;
        Table = table;
    }
}

/// <summary>The row table as RAW columnar buffers (UTF-8 blobs + little-endian int32 offset/range
/// tables), straight from the loaded <see cref="CabTable"/> -- see
/// <see cref="RipperBlenderBridge.EnumerateTablePacked"/>. Display columns (leaf name, joined
/// container string, type names) are deliberately absent: the consumer derives them lazily for
/// its visible window only.</summary>
public sealed record PackedTableDto(
    int Count,
    byte[] CabBlob, byte[] CabOffsets,
    byte[] SourceBlob, byte[] SourceOffsets,
    byte[] PathBlob, byte[] PathOffsets, byte[] PathStarts,
    byte[] ClassFlat, byte[] ClassStarts,
    byte[] DependencyCounts,
    string ClassIdNames);

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
/// <see cref="ClipCurveMeta"/>/<see cref="ClipCurveData"/> are the per-clip curve payloads
/// (guid-keyed JSON index + float32 blob, see <see cref="ClipCurveBlob"/>): the same curves the
/// YAML document carries, handed over as raw numbers so the Blender side never spends seconds
/// re-parsing them out of the text. A guid absent here (blob build failed) still has its YAML
/// document -- consumers fall back to parsing.
public sealed record ClosureResult(
    IReadOnlyDictionary<string, string> Documents,
    IReadOnlyDictionary<string, byte[]> Textures,
    IReadOnlyDictionary<string, byte[]> OtherFiles,
    string[] Roots,
    IReadOnlyDictionary<string, string> SeedRoots,
    IReadOnlyDictionary<string, string[]> ClipGuidsByCab,
    string[] SceneRoots,
    IReadOnlyDictionary<string, string> ClipCurveMeta,
    IReadOnlyDictionary<string, byte[]> ClipCurveData)
{
    public static ClosureResult Empty { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, byte[]>(),
        new Dictionary<string, byte[]>(),
        Array.Empty<string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string[]>(),
        Array.Empty<string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, byte[]>());
}
