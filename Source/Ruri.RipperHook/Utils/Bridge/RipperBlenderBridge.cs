using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Export.UnityProjects.Textures;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Extensions;
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
    /// Includes every <c>AlsoCoversVersions</c> alias id (via <see cref="Ruri.Hook.RuriHook.BuildHookIds"/>,
    /// same as the CLI's --list-hooks) -- e.g. the 1.2.4 class covering byte-identical 1.3.3 answers to
    /// BOTH "EndField_1.2.4" and "EndField_1.3.3". Listing only primaries made the addon's pre-ticked
    /// "EndField_1.3.3" default silently resolve to zero hooks after the alias refactor ("No VFS game
    /// hook active" on Discover Maps).
    /// </summary>
    public static string[] ListAvailableHooks() =>
        Ruri.Hook.RuriHook.GetAvailableHooks()
            .SelectMany(h => Ruri.Hook.RuriHook.BuildHookIds(h.Attribute))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Canonicalize a caller-supplied hook id to the exact id <see cref="Ruri.Hook.RuriHook"/> answers to,
    /// accepting '.'/'_' punctuation variants and <c>AlsoCoversVersions</c> aliases -- the same resolution
    /// the CLI's --hook option performs. Unknown ids pass through unchanged (ApplyHooks then simply
    /// enables nothing for them, matching previous behavior).
    /// </summary>
    private static string NormalizeHookId(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return id;
        }
        static string Canonicalize(string s) => s.Replace('.', '_');
        string target = Canonicalize(id);
        foreach ((_, Ruri.Hook.Attributes.GameHookAttribute attribute) in Ruri.Hook.RuriHook.GetAvailableHooks())
        {
            foreach (string candidate in Ruri.Hook.RuriHook.BuildHookIds(attribute))
            {
                if (string.Equals(Canonicalize(candidate), target, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return id;
    }

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
            config.EnabledHooks.Add(NormalizeHookId(id));
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

        // The table stores each chunk file ONCE (see CabTable's distinct-file invariant); the DTO
        // contract is per-row source strings, so expand the few dozen distinct rows back out here.
        int[] sourceOffsets = new int[count + 1];
        for (int id = 0; id < count; id++)
        {
            sourceOffsets[id + 1] = sourceOffsets[id] + table.DistinctFileUtf8(table.FileIndex[id]).Length;
        }
        byte[] sourceBlob = new byte[sourceOffsets[count]];
        for (int id = 0; id < count; id++)
        {
            table.DistinctFileUtf8(table.FileIndex[id]).CopyTo(sourceBlob.AsSpan(sourceOffsets[id]));
        }

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
            SourceBlob: sourceBlob,
            SourceOffsets: IntsToBytes(sourceOffsets, count + 1),
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

    /// <summary>
    /// Quick search + Include/Exclude rules + sort over the loaded cabmap, on the shared
    /// <see cref="CabTableSearch"/> engine (the same one the WinForms browser runs) -- the hosts'
    /// own per-row scalar filters are gone. <paramref name="flatRules"/> is the rule list
    /// flattened five strings per rule (field, relation, value, action, enabled: "1"/"0") so no
    /// custom DTO has to cross the reflection boundary; <paramref name="sortDirection"/> is
    /// 0 = load order, 1 = ascending, 2 = descending. Returns the visible row ids as
    /// little-endian int32 bytes -- one buffer crossing, numpy-viewable as-is.
    /// </summary>
    public static byte[] SearchTable(CabMapHandle map, string query, string[]? flatRules,
        string sortColumn, int sortDirection)
    {
        ArgumentNullException.ThrowIfNull(map);
        int[] ids = map.Search.Search(query, ParseFlatRules(flatRules), sortColumn, sortDirection);
        return IntsToBytes(ids, ids.Length);
    }

    /// <summary>Sort an explicit row-id subset (the folder view's own listing) by a display
    /// column, same engine and encoding as <see cref="SearchTable"/>.</summary>
    public static byte[] SortRows(CabMapHandle map, int[] rowIds, string sortColumn, int sortDirection)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rowIds);
        int[] ids = map.Search.SortIds(rowIds, sortColumn, sortDirection);
        return IntsToBytes(ids, ids.Length);
    }

    private static List<CabFilterRule>? ParseFlatRules(string[]? flatRules)
    {
        if (flatRules is null || flatRules.Length == 0)
        {
            return null;
        }
        if (flatRules.Length % 5 != 0)
        {
            throw new ArgumentException($"flatRules length {flatRules.Length} is not a multiple of 5 (field, relation, value, action, enabled)");
        }
        List<CabFilterRule> rules = new(flatRules.Length / 5);
        for (int i = 0; i < flatRules.Length; i += 5)
        {
            rules.Add(new CabFilterRule(
                Field: flatRules[i],
                Relation: flatRules[i + 1],
                Value: flatRules[i + 2],
                Include: flatRules[i + 3] != "exclude",
                Enabled: flatRules[i + 4] == "1"));
        }
        return rules;
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

    /// <summary>Transitive DEPENDENT closure by CAB name: every CAB that directly or indirectly
    /// references a seed — the full mirror of <see cref="ResolveClosureCabNames"/> on the transposed
    /// graph (<see cref="FindDirectDependents"/> is its one-hop special case). Same cost class: a
    /// pure in-memory walk over the eagerly built reverse adjacency.</summary>
    public static string[] ResolveReverseClosureCabNames(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveReverseClosureCabNames(map.Table, seedCabNames);
    }

    /// <summary>
    /// Reverse dependency lookup: every CAB that DIRECTLY depends on (references) any of the given
    /// seed CABs -- the one-hop mirror of <see cref="ResolveClosureCabNames"/>'s forward walk, via
    /// <see cref="CabTable.Dependents"/> (the transpose every loaded map carries). No VFS decrypt,
    /// no AssetRipper export; a pure in-memory graph lookup, same cost class as the forward closure.
    /// Useful when an asset's real usage context isn't reachable from its OWN forward dependencies
    /// at all -- e.g. a Mesh-only FBX sub-asset carries no Material of its own; the Prefab whose
    /// Renderer component pairs that mesh with a material is a direct DEPENDENT, never the other
    /// way around. Direct (one-hop) dependents only, not a transitive reverse closure: the caller
    /// typically feeds the results back into <see cref="ImportCabs"/> next, whose own forward
    /// closure resolution already pulls the seed CAB itself back in along with everything else.
    /// </summary>
    public static string[] FindDirectDependents(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(seedCabNames);
        CabTable table = map.Table;
        HashSet<int> found = new();
        foreach (string seedName in seedCabNames)
        {
            if (table.TryGetId(seedName, out int seedId))
            {
                foreach (int dependent in table.Dependents(seedId))
                {
                    found.Add(dependent);
                }
            }
        }
        string[] names = found.Select(table.CabName).ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// For a CAB that hosts AnimationClips (and typically nothing else), find EVERY CAB carrying an Avatar
    /// (ClassID 90) in the clip's dependency neighborhood, nearest first -- the assets a standalone clip
    /// import co-loads so (a) AssetRipper's own AnimationClipConverter can restore the clips' hashed curve
    /// paths to real transform-path strings (a clip CAB alone has NO dependencies, so its exported curve
    /// paths come out as "path_0x&lt;CRC32&gt;_&lt;suffix&gt;" placeholders; co-seeding the rig-FBX CAB
    /// resolves every one of them to a full "Root/Bip001/..." transform-path string, matching what a
    /// whole-character export produces), and (b) the caller can build a humanoid muscle retargeter from
    /// the rig's REAL Avatar.
    /// Returns ALL candidates (BFS order, capped) rather than the first hit, because the neighborhood
    /// routinely contains multiple Avatar assets of very different quality -- for example a small stub
    /// Avatar (empty m_TOS, all-zero m_ID, no usable skeleton) may surface before the real full Avatar
    /// (full m_TOS + muscle setup) a few hops later. WHICH one is usable is a content question the caller
    /// answers by trying to build a retargeter from each in order -- name/size heuristics here would be
    /// exactly the kind of guessing this bridge exists to avoid.
    /// Search shape mirrors the data's dependency topology: the Avatar is never among the clip's reverse
    /// dependents themselves (those are the AnimatorController, then the character prefabs) -- it lives in
    /// the FORWARD closure of those dependents. So: breadth-first over reverse dependents (nearest first,
    /// pure in-memory cabmap graph), scanning each one's forward closure for Avatar-classed CABs. Empty
    /// when the clip has no Avatar anywhere in its neighborhood. Cheap: every loaded map already
    /// carries the dependency transpose (<see cref="CabTable.Dependents"/>).
    /// </summary>
    public static string[] FindAssociatedAvatarCabs(CabMapHandle map, string clipCabName, int maxCandidates = 4)
    {
        ArgumentNullException.ThrowIfNull(map);
        CabTable table = map.Table;
        if (!table.TryGetId(clipCabName, out int clipId))
        {
            return Array.Empty<string>();
        }

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
            foreach (int dependent in table.Dependents(current))
            {
                if (visited[dependent])
                {
                    continue;
                }
                visited[dependent] = true;
                // Per-dependent forward closure, Avatar hits reported in case-insensitive
                // name order so results are deterministic across runs.
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
    /// of disk-backed), and return the result keyed by GUID.
    /// </summary>
    /// <param name="acceptedTextureFormats">The image containers THIS consumer can decode, lowercase
    /// extensions in preference order ("png", "tga", "exr", ...). Empty accepts everything: every
    /// texture keeps the container the game authored it in, byte-identical to a disk export. A
    /// texture whose natural container is not in the set is encoded into the set's first entry
    /// instead -- conversion happens exactly for what the consumer cannot read, never globally.
    /// The capability is the consumer's own data; nothing here prefers any format.</param>
    public static ClosureResult ImportCabs(CabMapHandle map, string[] seedCabNames, string[] acceptedTextureFormats)
    {
        return ImportCabsCore(map, seedCabNames, null, acceptedTextureFormats);
    }

    /// <summary>
    /// <see cref="ImportCabs"/> with an export-side allowlist of ClassIDs: the whole closure is
    /// still RESOLVED, LOADED and PROCESSED identically (a humanoid clip's muscle solve and hashed
    /// curve-path restore both need the rig in scope at load time), but only assets of the listed
    /// classes are exported/serialized. The standalone-clip flow consumes nothing but the exported
    /// .anim documents and their curve blobs, while its closure co-seeds the whole character for
    /// scope -- re-serializing that character's textures and meshes was most of its wall time.
    /// A distinct method name (not an overload): the pythonnet caller binds methods via
    /// Type.GetMethod(name), which throws on ambiguity.
    /// </summary>
    public static ClosureResult ImportCabsFiltered(CabMapHandle map, string[] seedCabNames, int[] exportClassIds,
        string[] acceptedTextureFormats)
    {
        ArgumentNullException.ThrowIfNull(exportClassIds);
        return ImportCabsCore(map, seedCabNames, exportClassIds, acceptedTextureFormats);
    }

    /// <summary>The consumer's accepted-container declaration, parsed. An extension nobody can
    /// encode is a caller bug and must say so at the boundary, not decode into a wrong image.</summary>
    private static AssetRipper.Export.Configuration.ImageExportFormat[] ParseTextureFormats(string[] extensions)
    {
        AssetRipper.Export.Configuration.ImageExportFormat[] formats =
            new AssetRipper.Export.Configuration.ImageExportFormat[extensions.Length];
        for (int i = 0; i < extensions.Length; i++)
        {
            if (!AssetRipper.Export.Configuration.ImageExportFormat.TryGetFromExtension(
                    extensions[i], out formats[i]))
            {
                throw new ArgumentException(
                    $"'{extensions[i]}' is not an image container this exporter can produce "
                    + "(bmp/exr/hdr/jpeg/jpg/png/tga).");
            }
        }
        return formats;
    }

    private static ClosureResult ImportCabsCore(CabMapHandle map, string[] seedCabNames, int[]? exportClassIds,
        string[] acceptedTextureFormats)
    {
        ArgumentNullException.ThrowIfNull(acceptedTextureFormats);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(seedCabNames);

        System.Diagnostics.Stopwatch phase = System.Diagnostics.Stopwatch.StartNew();
        CabClosure closure = new CabSelection { SeedCabNames = seedCabNames }.Resolve(map.Table);
        string[] closureFiles = closure.Files;
        HashSet<string> loadFilterFileNames = closure.LoadFilterFileNames;
        long resolveMs = phase.ElapsedMilliseconds;
        if (closureFiles.Length == 0)
        {
            return ClosureResult.Empty;
        }

        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
        // No bridge consumer reads scripts (MonoBehaviour structure comes from the bundles' own
        // typetrees), while Level1+ makes every ImportCabs call re-run the whole IL2Cpp assembly
        // scan and decompile-export ~1000 .cs stubs per closure -- pure fixed cost per call.
        settings.ImportSettings.ScriptContentLevel = AssetRipper.Import.Configuration.ScriptContentLevel.Level0;
        // The bridge's texture behavior is EXPLICIT, never inherited from whatever
        // AssetRipper.Settings.json a GUI run happened to save next to the DLL: naturals are the
        // game's own container whenever the asset records one, and png -- a lossless encode of the
        // decoded pixels -- when it records none. A settings file silently flipping this per bin
        // directory is exactly how the same closure once produced tga bytes under Debug and png
        // bytes under Release with nobody able to say why.
        settings.ExportSettings.PreferOriginalTextureExtension = true;
        settings.ExportSettings.ImageExportFormat = AssetRipper.Export.Configuration.ImageExportFormat.Png;
        ClipCaptureExporter clipCapture = new();
        MeshCaptureExporter meshCapture = new();
        PrewarmedTextureExporter textureExporter = new(settings, ParseTextureFormats(acceptedTextureFormats));
        BridgeExportHandler handler = new(settings, clipCapture, meshCapture, textureExporter, exportClassIds);

        GameData gameData;
        GameBundleHook.LoadIncludeFile = loadFilterFileNames.Count > 0 ? name => loadFilterFileNames.Contains(name) : null;
        phase.Restart();
        try
        {
            gameData = handler.Load(closureFiles, LocalFileSystem.Instance);
        }
        finally
        {
            GameBundleHook.LoadIncludeFile = null;
        }
        long loadMs = phase.ElapsedMilliseconds;

        phase.Restart();
        if (gameData.GameBundle.HasAnyAssetCollections())
        {
            handler.Process(gameData);
        }
        long processMs = phase.ElapsedMilliseconds;

        // Image encode is the dominant export cost, so it runs across all cores here -- and it
        // must FINISH before the export loop starts. Both stages resolve .resS-backed pixels and
        // vertex buffers through the same per-ResourceFile shared Stream (seek-then-read, not
        // atomic), so overlapping them is a torn read: an asset silently receives another's
        // bytes. Skipped entirely when a class filter excludes Texture2D.
        phase.Restart();
        if (exportClassIds is null || exportClassIds.Contains((int)ClassIDType.Texture2D))
        {
            textureExporter.Prewarm(gameData);
        }
        long prewarmMs = phase.ElapsedMilliseconds;

        InMemoryFileSystem memoryFileSystem = new();
        phase.Restart();
        handler.Export(gameData, "mem:/out", memoryFileSystem);
        long exportMs = phase.ElapsedMilliseconds;

        phase.Restart();
        ClosureResult result = Partition(memoryFileSystem.Files, map.Table, seedCabNames,
            clipCapture.Captured, meshCapture.Captured);
        Logger.Info(LogCategory.Export,
            $"[ImportCabs] closure={closure.ClosureCount} files={closureFiles.Length} " +
            $"resolve={resolveMs}ms load={loadMs}ms process={processMs}ms prewarm={prewarmMs}ms " +
            $"export={exportMs}ms partition={phase.ElapsedMilliseconds}ms " +
            $"texcache(hit={textureExporter.HitStats.Hits} miss={textureExporter.HitStats.Misses})");
        textureExporter.LogStats();
        LogExportCostByExtension(memoryFileSystem);
        return result;
    }

    /// <summary>Export-cost attribution from the in-memory commit timeline (sequential export:
    /// the gap before each commit is that file's own cost) -- by extension plus the slowest
    /// individual files, so "what are the export seconds spent on" is answerable from the log.</summary>
    private static void LogExportCostByExtension(InMemoryFileSystem memoryFileSystem)
    {
        Dictionary<string, (int Count, long Bytes, double Ms)> byExtension = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, long bytes, double ms) in memoryFileSystem.CommitTimeline)
        {
            string extension = Path.GetExtension(path);
            byExtension[extension] = byExtension.TryGetValue(extension, out (int Count, long Bytes, double Ms) sum)
                ? (sum.Count + 1, sum.Bytes + bytes, sum.Ms + ms)
                : (1, bytes, ms);
        }
        foreach ((string extension, (int count, long bytes, double ms)) in byExtension.OrderByDescending(static p => p.Value.Ms))
        {
            Logger.Info(LogCategory.Export,
                $"[ExportCost] {extension,-9} files={count,3} bytes={bytes,10} ms={ms,8:F1}");
        }
        foreach ((string path, long bytes, double ms) in memoryFileSystem.CommitTimeline.OrderByDescending(static c => c.Ms).Take(5))
        {
            Logger.Info(LogCategory.Export, $"[ExportCost] slowest {ms,8:F1}ms {bytes,10}B {path}");
        }
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
        private readonly MeshCaptureExporter _meshCapture;
        private readonly PrewarmedTextureExporter _textureExporter;
        private readonly int[]? _exportClassIds;

        public BridgeExportHandler(FullConfiguration settings, ClipCaptureExporter clipCapture,
            MeshCaptureExporter meshCapture, PrewarmedTextureExporter textureExporter,
            int[]? exportClassIds) : base(settings)
        {
            _clipCapture = clipCapture;
            _meshCapture = meshCapture;
            _textureExporter = textureExporter;
            _exportClassIds = exportClassIds;
        }

        protected override void BeforeExport(ProjectExporter projectExporter)
        {
            projectExporter.OverrideExporter<IAnimationClip>(_clipCapture, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh>(
                _meshCapture, allowInheritance: true);
            // Mirror the default stack's THREE texture registrations (ITexture2D + SpriteInformationObject
            // + ISprite): a texture's collection is routinely created via its SpriteInformationObject
            // MainAsset (whichever of the pair FetchAssets yields first claims both), so overriding
            // ITexture2D alone leaves the default exporter handling every texture reached that way.
            projectExporter.OverrideExporter<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D>(
                _textureExporter, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.Processing.Textures.SpriteInformationObject>(
                _textureExporter, allowInheritance: true);
            if (_exportClassIds is not null)
            {
                // Registered last = consulted first: allowed classes fall through to the normal
                // stack (including the two overrides above), everything else resolves to a
                // SkipExportCollection (references to it become missing refs, never a throw).
                projectExporter.OverrideExporter<IUnityObjectBase>(
                    new ClassFilterExporter(_exportClassIds), allowInheritance: true);
            }
        }
    }

    /// <summary>
    /// Export-side ClassID allowlist for <see cref="ImportCabsFiltered"/>: anything not listed is
    /// claimed into a <see cref="SkipExportCollection"/> (same collection AssetRipper's own
    /// DummyAssetExporter uses for "don't write this, missing-reference anyone who points at it").
    /// Allowed assets fall through to the rest of the exporter stack untouched.
    /// </summary>
    private sealed class ClassFilterExporter(int[] allowedClassIds) : IAssetExporter
    {
        public bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            if (allowedClassIds.Contains(asset.ClassID))
            {
                exportCollection = null;
                return false;
            }
            exportCollection = new SkipExportCollection(this, asset);
            return true;
        }

        public bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem) => false;

        public void Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem, Action<IExportContainer, IUnityObjectBase, string, FileSystem>? callback)
        {
        }

        public bool Export(IExportContainer container, IEnumerable<IUnityObjectBase> assets, string path, FileSystem fileSystem) => false;

        public void Export(IExportContainer container, IEnumerable<IUnityObjectBase> assets, string path, FileSystem fileSystem, Action<IExportContainer, IUnityObjectBase, string, FileSystem>? callback)
        {
        }

        public AssetType ToExportType(IUnityObjectBase asset) => AssetType.Serialized;

        public bool ToUnknownExportType(Type type, out AssetType assetType)
        {
            assetType = AssetType.Serialized;
            return true;
        }
    }

    /// <summary>
    /// <see cref="TextureAssetExporter"/> whose image decode+encode runs for every closure texture
    /// in parallel BEFORE the (sequential) export loop starts -- the loop's own Export call then
    /// just writes the finished bytes. Same converter, same per-texture container decision, same
    /// encoder as the base class, so output bytes are identical; a texture missing from the cache
    /// (or whose parallel encode failed) falls back to the base implementation, keeping its
    /// warnings and return codes exactly as before. Lightmap textures (MainAsset is
    /// ILightingDataAsset) are explicitly ceded to LightmapTextureAssetExporter, which this
    /// late-registered override would otherwise preempt.
    /// </summary>
    private sealed class PrewarmedTextureExporter : TextureAssetExporter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<IUnityObjectBase, byte[]?> _encoded =
            new(ReferenceEqualityComparer.Instance);
        private int _hits;
        private int _misses;
        private long _decodeTicks;
        private long _encodeTicks;
        private long _prewarmWallMs;
        private int _streamedFileCount;
        private int _streamedTextureCount;

        public (int Hits, int Misses) HitStats => (_hits, _misses);

        private readonly AssetRipper.Export.Configuration.ImageExportFormat[] _acceptedFormats;
        private readonly bool _exportSprites;

        public PrewarmedTextureExporter(FullConfiguration configuration,
            AssetRipper.Export.Configuration.ImageExportFormat[] acceptedFormats) : base(configuration)
        {
            _acceptedFormats = acceptedFormats;
            // Mirrors the base's private ExportSprites, from the same setting it reads.
            _exportSprites = configuration.ExportSettings.SpriteExportMode
                is not AssetRipper.Export.Configuration.SpriteExportMode.Yaml;
        }

        /// <summary>
        /// Content negotiation, the whole of it: the texture's NATURAL container is whatever a disk
        /// export would produce (the game's own authoring format); it survives untouched whenever
        /// the consumer declared nothing (raw truth) or declared it acceptable, and only a container
        /// the consumer cannot decode is re-encoded -- into the consumer's first preference. The
        /// producer holds no format opinion of its own.
        /// </summary>
        public AssetRipper.Export.Configuration.ImageExportFormat Negotiate(
            AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture)
        {
            AssetRipper.Export.Configuration.ImageExportFormat natural =
                texture.GetTextureExportFormat(PreferOriginalTextureExtension, ImageExportFormat);
            if (_acceptedFormats.Length == 0 || Array.IndexOf(_acceptedFormats, natural) >= 0)
            {
                return natural;
            }
            return _acceptedFormats[0];
        }

        public override bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            if (asset.MainAsset is AssetRipper.SourceGenerated.Classes.ClassID_1120.ILightingDataAsset)
            {
                exportCollection = null;
                return false;
            }
            // Same gate as the base, but the collection is the negotiating one, so the file
            // EXTENSION follows the same decision as the encoded bytes -- the two must never be
            // allowed to disagree (a .tga file holding png bytes is a silent lie to any consumer
            // that trusts names).
            if (asset.MainAsset is AssetRipper.Processing.Textures.SpriteInformationObject spriteInformation
                && (_exportSprites || asset is not AssetRipper.SourceGenerated.Classes.ClassID_213.ISprite))
            {
                exportCollection = new NegotiatedTextureExportCollection(this, spriteInformation, _exportSprites);
                return true;
            }
            exportCollection = null;
            return false;
        }

        public void Prewarm(GameData gameData)
        {
            // Exactly the set the export loop will route through this exporter -- the same
            // MainAsset gate TryCreateCollection applies. Engine/builtin textures (whose
            // MainAsset stays null; SpriteProcessor skips those collections) are never
            // exported, so encoding them here would be pure wasted work.
            List<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D> targets = new();
            foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
            {
                if (asset is AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture
                    && asset.MainAsset is AssetRipper.Processing.Textures.SpriteInformationObject
                    && texture.CheckAssetIntegrity())
                {
                    targets.Add(texture);
                }
            }

            // Pixels that live in a .resS stream cannot be read concurrently from the same
            // resource FILE: StreamedResourceExtensions.GetContent does `Stream.Position = offset`
            // then `ReadExactly` on the ResourceFile's ONE shared Stream, so two workers on the
            // same file interleave into a torn read -- one texture silently decodes another's
            // bytes (observed: a character face exporting as colour noise, intermittently).
            // Group by resource path: one worker per file serializes those reads, different
            // files never share a Stream, and inline-pixel textures touch no stream at all.
            List<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D> inlineTargets = new();
            Dictionary<string, List<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D>> streamedByFile =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (var texture in targets)
            {
                string resourcePath = texture.ImageData_C28.Length == 0 && texture.StreamData_C28 is { } stream && stream.IsSet()
                    ? stream.Path.String
                    : string.Empty;
                if (resourcePath.Length == 0)
                {
                    inlineTargets.Add(texture);
                }
                else if (streamedByFile.TryGetValue(resourcePath, out var group))
                {
                    group.Add(texture);
                }
                else
                {
                    streamedByFile[resourcePath] = [texture];
                }
            }
            _streamedFileCount = streamedByFile.Count;
            _streamedTextureCount = targets.Count - inlineTargets.Count;

            System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
            Parallel.ForEach(inlineTargets, texture => _encoded[texture] = EncodeOne(texture));
            Parallel.ForEach(streamedByFile.Values, group =>
            {
                foreach (var texture in group)
                {
                    _encoded[texture] = EncodeOne(texture);
                }
            });
            _prewarmWallMs = wall.ElapsedMilliseconds;
        }

        private byte[]? EncodeOne(AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!AssetRipper.Export.Modules.Textures.TextureConverter.TryConvertToBitmap(
                    texture, out AssetRipper.Export.Modules.Textures.DirectBitmap bitmap))
            {
                return null;
            }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _decodeTicks, t1 - t0);
            AssetRipper.Export.Configuration.ImageExportFormat natural =
                texture.GetTextureExportFormat(PreferOriginalTextureExtension, ImageExportFormat);
            AssetRipper.Export.Configuration.ImageExportFormat negotiated = Negotiate(texture);
            _naturalCounts.AddOrUpdate(natural, 1, static (_, count) => count + 1);
            if (negotiated != natural)
            {
                Interlocked.Increment(ref _convertedCount);
            }
            using MemoryStream stream = new();
            bitmap.Save(stream, negotiated);
            Interlocked.Add(ref _encodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t1);
            return stream.ToArray();
        }

        // Observability for the negotiation itself: which containers the game actually authored,
        // and how many the consumer's declaration forced into another one. A run whose naturals
        // all collapse to one format is how a broken OriginalPath pipeline gets NOTICED.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            AssetRipper.Export.Configuration.ImageExportFormat, int> _naturalCounts = new();
        private int _convertedCount;

        public void LogStats()
        {
            double frequency = System.Diagnostics.Stopwatch.Frequency;
            string naturals = string.Join(' ', _naturalCounts.OrderByDescending(static pair => pair.Value)
                .Select(static pair => $"{pair.Key.ToString().ToLowerInvariant()}={pair.Value}"));
            string accepted = _acceptedFormats.Length == 0
                ? "raw"
                : string.Join(',', _acceptedFormats.Select(static format => format.ToString().ToLowerInvariant()));
            Logger.Info(LogCategory.Export,
                $"[TexPrewarm] wall={_prewarmWallMs}ms decodeSum={_decodeTicks * 1000.0 / frequency:F0}ms " +
                $"encodeSum={_encodeTicks * 1000.0 / frequency:F0}ms cores={Environment.ProcessorCount} " +
                $"streamed={_streamedTextureCount} over {_streamedFileCount} resource file(s) " +
                $"naturals[{naturals}] accepted={accepted} converted={_convertedCount}");
        }

        public override bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
        {
            if (_encoded.TryRemove(asset, out byte[]? bytes) && bytes is not null)
            {
                Interlocked.Increment(ref _hits);
                fileSystem.File.WriteAllBytes(path, bytes);
                return true;
            }
            Interlocked.Increment(ref _misses);
            return base.Export(container, asset, path, fileSystem);
        }
    }

    /// <summary>The stock texture collection with one behavioural change: the exported file's
    /// extension is the NEGOTIATED container (see <see cref="PrewarmedTextureExporter.Negotiate"/>),
    /// the same decision the encoded bytes followed.</summary>
    private sealed class NegotiatedTextureExportCollection
        : AssetRipper.Export.UnityProjects.Textures.TextureExportCollection
    {
        private readonly PrewarmedTextureExporter _exporter;

        public NegotiatedTextureExportCollection(PrewarmedTextureExporter exporter,
            AssetRipper.Processing.Textures.SpriteInformationObject spriteInformation, bool exportSprites)
            : base(exporter, spriteInformation, exportSprites)
        {
            _exporter = exporter;
        }

        protected override string GetExportExtension(IUnityObjectBase asset)
            => asset is AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture
                ? _exporter.Negotiate(texture).GetFileExtension()
                : base.GetExportExtension(asset);
    }

    /// <summary>
    /// Decorator over AssetRipper's own <see cref="DefaultYamlExporter"/> that records, for every
    /// AnimationClip it exports, WHICH source collection (CAB) the asset came from and the exact file path
    /// the exporter actually wrote (including any name-collision uniquification suffix). This is the
    /// cabmap-identity bridge for clips: a clip's CAB container path is its host FBX
    /// ("...a_x_01.fbx") while its exported file is named after the clip's own m_Name
    /// ("...A_x_ACL.anim") -- the two stems genuinely differ, so no path/name normalization can join
    /// them after the fact. The asset object itself is the only thing
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

    /// <summary>
    /// The mesh counterpart of <see cref="ClipCaptureExporter"/>: same YAML output as the default
    /// stack (a <see cref="YamlStreamedAssetExportCollection"/>, which restores external stream
    /// data into VertexData before serializing -- this exporter's Export runs INSIDE that window,
    /// so <see cref="MeshRawBlob"/> reads the exact bytes the YAML inlines), plus the raw blob
    /// captured per exported path for the guid join in Partition. A mesh the blob builder declines
    /// (compressed / channel-less / unresolvable stream) is still exported as YAML alone and the
    /// host's existing fallback + diagnosis wording applies unchanged.
    /// </summary>
    internal sealed class MeshCaptureExporter : IAssetExporter
    {
        private readonly YamlStreamedAssetExporter _inner = new();

        /// <summary>(exported file path, blob JSON index, blob payload) per captured Mesh.</summary>
        public List<(string Path, string MetaJson, byte[] Payload)> Captured { get; } = new();

        public bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            exportCollection = new YamlStreamedAssetExportCollection(this, asset);
            return true;
        }

        public bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
        {
            if (asset is AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh mesh)
            {
                try
                {
                    (string MetaJson, byte[] Payload)? blob = MeshRawBlob.Build(mesh);
                    if (blob is not null)
                    {
                        Captured.Add((path, blob.Value.MetaJson, blob.Value.Payload));
                    }
                }
                catch (Exception exception)
                {
                    Logger.Warning(LogCategory.Export, $"Mesh raw blob failed for '{asset.GetBestName()}': {exception.Message} -- host side falls back to YAML parsing.");
                }
            }
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
    /// that patch didn't change it -- see EndfieldSceneBridge.cs).
    /// </summary>
    public static byte[] ExtractVfsFile(string[] vfsRoots, string fileName) =>
        VfsFuncOrThrow(GameBundleHook.ExtractVfsFile)(vfsRoots, fileName);

    /// <summary>Every distinct map name with streaming-chunk data across <paramref name="vfsRoots"/>
    /// (i.e. every "&lt;map&gt;" in "Data/Streaming/PC/&lt;map&gt;/Streaming/*.bytes").</summary>
    public static string[] EnumerateSceneMaps(string[] vfsRoots) =>
        VfsFuncOrThrow(GameBundleHook.EnumerateSceneMaps)(vfsRoots);

    /// <summary>
    /// What one map ships, summarized to the numbers a caller decides by: its scene states, and the
    /// split between cell-anchored chunks and the map-wide/dynamic ones a window can only bound. Read
    /// out of the VFS manifests alone -- no chunk byte is touched. See
    /// EndfieldSceneBridge.SceneChunkSummary.
    /// </summary>
    public static SceneChunkSummaryDto SceneChunkSummary(string[] vfsRoots, string mapName)
    {
        (int[] states, int anchoredFiles, long anchoredBytes, int floatingFiles, long floatingBytes) =
            VfsFuncOrThrow(GameBundleHook.SceneChunkSummary)(vfsRoots, mapName);
        return new SceneChunkSummaryDto(states, anchoredFiles, anchoredBytes, floatingFiles, floatingBytes);
    }

    /// <summary>
    /// Every named place the game's own map UI lists, with the world rect the game gives it -- the rect
    /// <see cref="DiscoverScenePlacements"/> takes as its window, so asking for "供能高地" never involves
    /// guessing a coordinate. <c>IsSingleLevel</c> separates a scene that is its own level (a dungeon, a
    /// station interior) from a place inside a bigger streaming map. See EndfieldSceneLandmarks.
    /// </summary>
    public static SceneLandmarkDto[] SceneLandmarks(string[] vfsRoots) =>
        VfsFuncOrThrow(GameBundleHook.SceneLandmarks)(vfsRoots)
            .Select(l => new SceneLandmarkDto(l.LevelId, l.IsSingleLevel, l.MinX, l.MinZ, l.MaxX, l.MaxZ))
            .ToArray();

    /// <summary>
    /// What one streaming window of <paramref name="mapName"/> places: the world rect
    /// (<paramref name="minX"/>, <paramref name="minZ"/>)..(<paramref name="maxX"/>,
    /// <paramref name="maxZ"/>) that the running game itself streams, gated by
    /// <paramref name="sceneStateIds"/> (empty = every state the map ships), across
    /// <paramref name="vfsRoots"/> in priority order. Reduced game-side (see EndfieldSceneBridge.Reduce):
    /// the placements are the importable rows only -- geometry with a verified transform, one detail
    /// level per instance when <paramref name="lod0Only"/> -- and SeedPaths is the distinct container
    /// path set (mesh + material) whose CABs an import needs, ready for
    /// <see cref="ResolveCabsForPaths"/>. An infinite rect is the whole map. Cheap per chunk: only the
    /// hash LUT + the selected files are extracted/decoded, no dependency closure is resolved and no
    /// CAB is loaded. See EndfieldSceneBridge.DiscoverScenePlacements for the full implementation notes
    /// (how the non-grid chunks are bounded, transform-resolution priority, and the
    /// STREAMING-vs-DynamicStreaming scope boundary).
    /// </summary>
    public static SceneDiscoveryDto DiscoverScenePlacements(string[] vfsRoots, string mapName,
        double minX, double minZ, double maxX, double maxZ, int[] sceneStateIds, bool lod0Only)
    {
        (int total, int noTransform, int lodFiltered, int distinctAssets, string[] seedPaths, var rows) =
            VfsFuncOrThrow(GameBundleHook.DiscoverScenePlacements)(
                vfsRoots, mapName, minX, minZ, maxX, maxZ, sceneStateIds, lod0Only);
        ScenePlacementDto[] placements = new ScenePlacementDto[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var p = rows[i];
            placements[i] = new ScenePlacementDto(p.AssetPath, p.AssetHash, p.EntityName, p.SourceChunk,
                p.Px, p.Py, p.Pz, p.Qx, p.Qy, p.Qz, p.Qw, p.Sx, p.Sy, p.Sz, p.MaterialAssetPaths);
        }
        return new SceneDiscoveryDto(total, noTransform, lodFiltered, distinctAssets, seedPaths, placements);
    }

    /// <summary>Binary/vtable-level schema-drift diagnostic for <paramref name="mapName"/>'s streaming
    /// chunks -- one report line per FlatBuffers table type, flagging any type where the source data
    /// declares more fields than the currently-compiled bindings know how to read, plus sample raw
    /// dumps of the extra field bytes. See EndfieldSceneBridge.DiagnoseSchemaDrift's doc comment.</summary>
    public static string[] DiagnoseSchemaDrift(string[] vfsRoots, string mapName) =>
        VfsFuncOrThrow(GameBundleHook.DiagnoseSchemaDrift)(vfsRoots, mapName);

    /// <summary>
    /// Project one of the game's own self-describing data containers into columns. The container
    /// names itself (<paramref name="containerFile"/> is a VFS file name such as
    /// "Data/TableCfg/CharacterTable.bytes") and carries its own schema, so nothing here -- and
    /// nothing generated -- knows what a character or a localization is.
    ///
    /// <paramref name="flatColumnSpecs"/> is four strings per column: display name, dotted path into
    /// the row, the container to resolve that value through (empty = no join), and the path taken
    /// inside the joined row. "name.id" through "Data/TableCfg/I18nTextTable_CN.bytes" with an empty
    /// joined path is how a roster gets its localized names. Column 0 of the result is always the
    /// row's own key.
    ///
    /// The returned buffers are the columns as-is: a python caller maps them with numpy and never
    /// parses a byte.
    /// </summary>
    /// <remarks><paramref name="cancellation"/> is required, not defaulted: the in-process caller
    /// reaches this through <c>MethodInfo.Invoke</c>, which binds by exact parameter count and would
    /// never supply an omitted optional argument.</remarks>
    public static ColumnTableDto QueryDataTable(string[] vfsRoots, string containerFile, string[] flatColumnSpecs,
        string distinctBy, string preferNonEmpty, CancellationToken cancellation)
    {
        (string handle, string name, int rowCount, string[] columns, string[] kinds, byte[][] blobs, byte[][] offsets) =
            VfsFuncOrThrow(GameBundleHook.QueryDataTable)(vfsRoots, containerFile, flatColumnSpecs,
                distinctBy, preferNonEmpty, cancellation);
        return new ColumnTableDto(handle, name, rowCount, columns, kinds, blobs, offsets);
    }

    /// <summary>Row ids of a table returned by <see cref="QueryDataTable"/> whose text matches
    /// <paramref name="query"/> -- the SAME vectorized engine the cabmap browser searches with
    /// (<see cref="CabMapping.Utf8Search"/>: one ASCII fold per column, then a parallel IndexOf
    /// sweep), so a game's own config tables search exactly as fast as the row table does and
    /// there is only one implementation of "does this row match". Returns little-endian int32
    /// bytes, numpy-viewable as-is.</summary>
    public static byte[] SearchDataTable(string handle, string query)
    {
        int[] rows = VfsFuncOrThrow(GameBundleHook.SearchDataTable)(handle, query);
        return IntsToBytes(rows, rows.Length);
    }

    /// <summary>
    /// Each character's authoritative model prefab name and expression-table tag, read out of the
    /// character data assets in <paramref name="cabNames"/>. Four strings per character: id, model
    /// prefab name, morph tag id, source asset name.
    ///
    /// The closure is loaded here (generic) and only MonoBehaviours are serialized, so pulling
    /// three fields does not cost a full character export; the field names live with the game (see
    /// <see cref="GameBundleHook.CharacterModels"/>). A character's model is NOT derivable from its
    /// id -- no config table carries one -- which is why this asset has to be read at all.
    /// </summary>
    public static string[] ReadCharacterModels(CabMapHandle map, string[] cabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        // Textures are excluded by the class filter, so there is no container to negotiate.
        ClosureResult closure = ImportCabsFiltered(map, cabNames, [(int)ClassIDType.MonoBehaviour], []);
        UTF8Encoding utf8 = new(false);
        string[] texts = closure.Assets.Values.Select(bytes => utf8.GetString(bytes)).ToArray();
        return VfsFuncOrThrow(GameBundleHook.CharacterModels)(texts);
    }

    /// <summary>Every npc template the game ships an assembled model for.</summary>
    public static string[] NpcPrefabManifest(string[] vfsRoots)
        => VfsFuncOrThrow(GameBundleHook.NpcPrefabManifest)(vfsRoots);

    /// <summary>What an npc template is assembled from -- see
    /// <see cref="GameBundleHook.NpcPrefabParts"/>. Flattened to strings so no DTO crosses the
    /// reflection boundary: [characterId, lodCount, facialMorph, avatarTemplet, part, part, ...].</summary>
    public static string[] NpcPrefabParts(string[] vfsRoots, string templateId)
    {
        (string[] parts, string characterId, int lodCount, string facialMorph, string avatarTemplet) =
            VfsFuncOrThrow(GameBundleHook.NpcPrefabParts)(vfsRoots, templateId);
        string[] flat = new string[4 + parts.Length];
        flat[0] = characterId;
        flat[1] = lodCount.ToString();
        flat[2] = facialMorph;
        flat[3] = avatarTemplet;
        parts.CopyTo(flat, 4);
        return flat;
    }

    private static T VfsFuncOrThrow<T>(T? func) where T : class =>
        func ?? throw new InvalidOperationException(
            "No VFS game hook active -- call Initialize(...) with a VFS-game hook id (e.g. \"EndField_1.3.3\") first.");

    private static ClosureResult Partition(IReadOnlyDictionary<string, byte[]> files,
        CabTable table, string[] seedCabNames,
        List<(string Cab, string Path, string MetaJson, byte[] Curves)> capturedClips,
        List<(string Path, string MetaJson, byte[] Payload)> capturedMeshes)
    {
        Dictionary<string, byte[]> assets = new(StringComparer.Ordinal);
        // The exported path IS the asset's real name (AssetRipper names files from m_Name /
        // the addressable path) -- dropping it here was how every texture crossed the bridge
        // as a bare guid and hosts had nothing better to display.
        Dictionary<string, string> assetPaths = new(StringComparer.Ordinal);
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

        // Root guid -> its normalized export path, feeding the CAB-attribution pass below.
        List<(string Guid, string NormalizedPath)> rootPaths = new();

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

            // Every exported asset is handed over as its own bytes, whatever AssetRipper wrote. The
            // caller already knows what it asked for -- a material's texture slot wants image bytes, a
            // prefab walk wants YAML -- so there is nothing here to classify. Sniffing the payload
            // instead (this used to keep PNGs and utf8-decode everything else) silently destroyed any
            // asset the exporter emitted in another format the moment one appeared: a TGA or EXR
            // texture became mojibake in the document stream and vanished from every material.
            assets[guid] = bytes;
            assetPaths[guid] = path;
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(guid);
                rootPaths.Add((guid, NormalizeExportPath(path)));
            }
            else if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                // A non-bundled build's GameObject hierarchies (level0/level1/... -- the actual
                // level + character models) export as SCENE files, not prefabs; without this the
                // whole scene body is absent from the importable roots (the level itself exports as
                // Assets/Scenes/level0.unity alongside any shared SFX .prefabs, so treating only
                // .prefab as a root would drop all level geometry). A scene document is the same
                // GameObject/Transform/renderer document stream a .prefab is, so the same importer
                // consumes it.
                roots.Add(guid);
                sceneRoots.Add(guid);
                rootPaths.Add((guid, NormalizeExportPath(path)));
            }
        }

        Dictionary<string, string> rootCabs = BuildRootCabs(table, rootPaths);

        Dictionary<string, string> seedRoots = new(StringComparer.Ordinal);
        foreach (string seedCab in seedCabNames)
        {
            if (!table.TryGetId(seedCab, out int seedId) || seedId >= table.Count)
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

        // And the mesh raw blobs (see MeshRawBlob) -- the geometry counterpart of the clip fast path.
        Dictionary<string, string> meshBlobMeta = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> meshBlobData = new(StringComparer.Ordinal);
        foreach ((string path, string metaJson, byte[] payload) in capturedMeshes)
        {
            string? guid = pathToGuid.GetValueOrDefault(NormalizeExportPath(path));
            if (guid is not null)
            {
                meshBlobMeta[guid] = metaJson;
                meshBlobData[guid] = payload;
            }
        }

        return new ClosureResult(assets, other, roots.ToArray(), seedRoots, clipGuidsByCab,
            sceneRoots.ToArray(), clipCurveMeta, clipCurveData, meshBlobMeta, meshBlobData, rootCabs,
            assetPaths);
    }

    /// <summary>
    /// Root guid -> hosting CAB name, through the cabmap's own container-path identity (the same
    /// join <see cref="ClosureResult.SeedRoots"/> uses, generalized to EVERY root): one parallel
    /// pass over the path column probing the root paths, plus the non-bundled scene spelling
    /// ("assets/scenes/&lt;cab&gt;.unity", whose stem IS the cab key). This is what lets a caller
    /// that resolved a UNION closure (hierarchy rows + clip co-seeds in one call) attribute each
    /// root to the sub-closure it belongs to, instead of paying a second load+export per group.
    /// </summary>
    private static Dictionary<string, string> BuildRootCabs(CabTable table,
        List<(string Guid, string NormalizedPath)> rootPaths)
    {
        Dictionary<string, string> rootCabs = new(StringComparer.Ordinal);
        if (rootPaths.Count == 0)
        {
            return rootCabs;
        }

        Dictionary<string, List<string>> guidsByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string guid, string normalizedPath) in rootPaths)
        {
            // Non-bundled scene: the file name is the cabmap key itself.
            if (normalizedPath.StartsWith("assets/scenes/", StringComparison.Ordinal)
                && normalizedPath.EndsWith(".unity", StringComparison.Ordinal))
            {
                string stem = Path.GetFileNameWithoutExtension(normalizedPath);
                if (table.TryGetId(stem, out _))
                {
                    rootCabs[guid] = stem;
                    continue;
                }
            }
            if (!guidsByPath.TryGetValue(normalizedPath, out List<string>? guids))
            {
                guidsByPath[normalizedPath] = guids = new List<string>();
            }
            guids.Add(guid);
        }
        if (guidsByPath.Count == 0)
        {
            return rootCabs;
        }

        System.Collections.Concurrent.ConcurrentDictionary<string, string> pathToCab = new(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, table.Count), range =>
        {
            (int start, int end) = range;
            for (int id = start; id < end; id++)
            {
                int pathCount = table.ContainerPathCount(id);
                for (int i = 0; i < pathCount; i++)
                {
                    string containerPath = table.ContainerPath(id, i);
                    if (guidsByPath.ContainsKey(containerPath))
                    {
                        pathToCab.TryAdd(containerPath, table.CabName(id));
                    }
                }
            }
        });
        foreach ((string path, List<string> guids) in guidsByPath)
        {
            if (pathToCab.TryGetValue(path, out string? cab))
            {
                foreach (string guid in guids)
                {
                    rootCabs[guid] = cab;
                }
            }
        }
        return rootCabs;
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
            (int)ClassIDType.GameObject => ".prefab",
            (int)ClassIDType.MonoScript => ".cs",
            // Images and audio: the exporter picks the container from the asset's own pixel/sample
            // format (png/tga/exr/..., ogg/wav/...), so there is no single extension to expect.
            (int)ClassIDType.Texture2D or (int)ClassIDType.Cubemap or (int)ClassIDType.AudioClip => null,
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

    /// <summary>Normalizes an export-side path (on Windows this is
    /// "mem:/out\ExportedProject\Assets\beyond\...\x.prefab" -- backslashes throughout, plus an
    /// "ExportedProject\" segment the cabmap side does not carry) or a cabmap-side
    /// <see cref="CabMap.Entry.ContainerPaths"/>
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

}

/// <summary>Opaque handle to a loaded cabmap — the columnar <see cref="CabTable"/> (blobs +
/// offsets + int dependency graph; see CabTable.cs for why nothing per-entry is materialized).</summary>
public sealed class CabMapHandle
{
    private CabTableSearch? _search;

    public string CabMapPath { get; }
    public CabTable Table { get; }
    public string BaseFolder => Table.BaseFolder;

    /// <summary>The handle's one search engine -- lazily built so its folded-blob and
    /// derived-column caches live exactly as long as the loaded map does.</summary>
    public CabTableSearch Search => _search ??= new CabTableSearch(Table);

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

/// <summary>One projected data table as raw buffers -- see <see cref="RipperBlenderBridge.QueryDataTable"/>.
/// One entry per column, parallel across all four arrays. <c>Kinds[i]</c> is "text" (Blobs[i] is UTF-8,
/// Offsets[i] is RowCount+1 little-endian int32s), "int" (Blobs[i] is RowCount little-endian int64s,
/// Offsets[i] empty) or "real" (RowCount little-endian float64s). Column 0 is the row key.</summary>
public sealed record ColumnTableDto(
    string Handle, string Name, int RowCount, string[] Names, string[] Kinds, byte[][] Blobs, byte[][] Offsets);

/// <summary>One file inside the VFS, as returned by <see cref="RipperBlenderBridge.EnumerateVfsFiles"/> — its
/// exact original name (the lookup key <see cref="RipperBlenderBridge.ExtractVfsFile"/> takes), its
/// EVFSBlockType name (e.g. "Streaming", "ExtendData"), its decrypted length, and which .chk it lives in
/// (informational only; callers extract by name, not by chunk path).</summary>
public sealed record VfsFileDto(string FileName, long FileNameHash, string BlockType, long Length, string ChkPath);

/// <summary>One map's chunk inventory, summarized by <see cref="RipperBlenderBridge.SceneChunkSummary"/> —
/// which scene states it ships, and its split between cell-anchored chunk files (a window selects these by
/// rect) and the map-wide/dynamic ones (a window can only bound their decoded content). Byte counts are the
/// highest-priority root's manifest lengths: a cost estimate.</summary>
public sealed record SceneChunkSummaryDto(
    int[] SceneStateIds, int AnchoredFiles, long AnchoredBytes, int FloatingFiles, long FloatingBytes);

/// <summary>One streaming window's importable content, as
/// <see cref="RipperBlenderBridge.DiscoverScenePlacements"/> returns it: the kept placements (each one
/// geometry with a verified transform, already one detail level per instance when lod0Only was set), the
/// distinct container paths whose CABs an import needs (SeedPaths, sorted — feed straight to
/// <see cref="RipperBlenderBridge.ResolveCabsForPaths"/>), and the counts explaining what the reduction
/// dropped: Total raw rows, NoTransform (not geometry — no verified transform source or unresolved asset
/// path), LodFiltered (non-best detail siblings of an instance already covered).</summary>
public sealed record SceneDiscoveryDto(
    int Total, int NoTransform, int LodFiltered, int DistinctAssets, string[] SeedPaths,
    ScenePlacementDto[] Placements);

/// <summary>One named place listed by <see cref="RipperBlenderBridge.SceneLandmarks"/> — the level id the
/// game keys it by, whether it is a self-contained scene of its own rather than a place inside a bigger
/// streaming map (IsSingleLevel), and the world XZ rect the game itself gives it, which is the window
/// <see cref="RipperBlenderBridge.DiscoverScenePlacements"/> takes.</summary>
public sealed record SceneLandmarkDto(
    string LevelId, bool IsSingleLevel, float MinX, float MinZ, float MaxX, float MaxZ);

/// <summary>One importable placement inside a <see cref="SceneDiscoveryDto"/>. Every row that reaches
/// this type IS geometry: its transform came from one of the verified sources (ECS blob LocalToWorld,
/// validated FBPropertyBytesData pose, or FBPropertyBoundsData centre) and its AssetPath resolved through
/// the hash LUT -- rows without both are counted in the discovery's NoTransform, never emitted.
/// MaterialAssetPaths is this entity's own resolved material(s) -- same hash-LUT source as AssetPath,
/// just the sibling AssetType==1 property entries instead of ==2; empty when the entity carries none or
/// none resolved.</summary>
public sealed record ScenePlacementDto(
    string AssetPath, long AssetHash, string EntityName, string SourceChunk,
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
/// <see cref="Assets"/> is every exported asset keyed by guid, each one exactly the bytes AssetRipper
/// wrote. There is deliberately no split by kind here: a consumer asks for a guid because it already
/// knows what it wants it as, so decoding is its business and nothing in the bridge has to guess a
/// payload's format.
public sealed record ClosureResult(
    IReadOnlyDictionary<string, byte[]> Assets,
    IReadOnlyDictionary<string, byte[]> OtherFiles,
    string[] Roots,
    IReadOnlyDictionary<string, string> SeedRoots,
    IReadOnlyDictionary<string, string[]> ClipGuidsByCab,
    string[] SceneRoots,
    IReadOnlyDictionary<string, string> ClipCurveMeta,
    IReadOnlyDictionary<string, byte[]> ClipCurveData,
    IReadOnlyDictionary<string, string> MeshBlobMeta,
    IReadOnlyDictionary<string, byte[]> MeshBlobData,
    IReadOnlyDictionary<string, string> RootCabs,
    IReadOnlyDictionary<string, string> AssetPaths)
{
    public static ClosureResult Empty { get; } = new(
        new Dictionary<string, byte[]>(),
        new Dictionary<string, byte[]>(),
        Array.Empty<string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string[]>(),
        Array.Empty<string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, byte[]>(),
        new Dictionary<string, string>(),
        new Dictionary<string, byte[]>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>());
}
