using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using Ruri.RipperHook.Tables;
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
using AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.Bones;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Core.Install;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.Bridge;

public static class RipperBlenderBridge
{
    private static bool _loggingConfigured;

    /// <summary>
    /// The features THIS host runs with, always. They are not a user choice: Blender takes the
    /// add-on's own in-memory path, where a Unity humanoid rig has no equivalent at all and must
    /// arrive as a generic one. Everything else an AssetRipper feature does (shader decompilation,
    /// method dumps, file-tree exports) is about writing a project to disk, which this path never
    /// does. A name that no feature claims is a build error at the first Initialize, not a silent
    /// no-op.
    /// </summary>
    public static readonly string[] HostFeatures = { "HumanoidToGeneric" };

    /// <summary>
    /// Every decoder compiled in, as flat triples (product, version, engineVersion) -- what a
    /// host's decoder picker lists. Ordered by product then version, newest version first within
    /// a product.
    /// </summary>
    public static string[] ListDecoders()
    {
        List<string> flat = new();
        foreach (string product in HookCatalog.Products)
        {
            foreach (DecoderHook decoder in HookCatalog.VersionsOf(product))
            {
                flat.Add(decoder.Product);
                flat.Add(decoder.Version);
                flat.Add(decoder.EngineVersion);
            }
        }
        return flat.ToArray();
    }

    /// <summary>
    /// What the players under <paramref name="gameRoot"/> say they are, as flat sextuples
    /// (dataFolder, company, product, gameVersion, engineVersion, isProject). Reads two small
    /// files per player and nothing else -- see <see cref="InstallProbe"/>.
    /// </summary>
    public static string[] ReadInstall(string gameRoot)
    {
        List<PlayerIdentity> players = InstallProbe.Read(gameRoot);
        PlayerIdentity? project = InstallProbe.Project(gameRoot);
        List<string> flat = new(players.Count * 6);
        foreach (PlayerIdentity player in players)
        {
            flat.Add(player.DataFolder);
            flat.Add(player.Company);
            flat.Add(player.Product);
            flat.Add(player.GameVersion);
            flat.Add(player.EngineVersion);
            flat.Add(project is not null && player.DataFolder == project.DataFolder ? "1" : "0");
        }
        return flat.ToArray();
    }

    /// <summary>
    /// The decoder id this install is read through, or "" when none applies (a plain Unity build
    /// needs no decoder). See <see cref="HookCatalog.Resolve"/>.
    /// </summary>
    public static string ResolveDecoder(string product, string gameVersion, string engineVersion) =>
        HookCatalog.Resolve(product, gameVersion, engineVersion)?.Id ?? string.Empty;

    /// <summary>
    /// Open the session on ONE install through ONE decoder. The host states which install and
    /// which decoder; which features run is this host's own fact (<see cref="HostFeatures"/>),
    /// never part of that statement. An empty decoder id is a valid configuration: a plain
    /// un-bundled Unity build is read by the generic path.
    /// </summary>
    public static void Initialize(string decoderId, string gameRoot)
    {
        Bootstrap.InstallAssemblyResolver();

        if (!_loggingConfigured)
        {
            _loggingConfigured = true;
            Logger.Clear();
            Logger.Add(new BridgeLogger { MinLevel = LogType.Info });
        }

        Data.CoreDatasets.Register();

        HookConfig config = new();
        foreach (string feature in HostFeatures)
        {
            if (HookCatalog.FeatureByName(feature) is null)
            {
                throw new InvalidOperationException(
                    $"[RipperBlenderBridge] Host feature '{feature}' names no RipperFeature in this build.");
            }
            config.EnabledHooks.Add(feature);
        }
        if (!string.IsNullOrEmpty(decoderId))
        {
            if (HookCatalog.DecoderById(decoderId) is null)
            {
                throw new InvalidOperationException(
                    $"[RipperBlenderBridge] '{decoderId}' names no decoder in this build. "
                    + $"Known: {string.Join(", ", HookCatalog.Decoders.Select(static decoder => decoder.Id))}.");
            }
            config.EnabledHooks.Add(decoderId);
        }
        Bootstrap.ApplyHooks(config);
        Data.Session.Open(gameRoot ?? string.Empty, config.EnabledHooks);
    }

    public static int BuildCabMap(string gameRoot, string outPath) => CabMap.Build(gameRoot, outPath);

    public static CabMapHandle LoadCabMap(string cabMapPath)
    {
        return new CabMapHandle(cabMapPath, CabMap.LoadTable(cabMapPath));
    }

    public static PackedTableDto EnumerateTablePacked(CabMapHandle map)
    {
        ArgumentNullException.ThrowIfNull(map);
        CabTable table = map.Table;
        int count = table.Count;

        byte[] cabBlob = new byte[table.CabOffsets[count]];
        Buffer.BlockCopy(table.CabBlob, 0, cabBlob, 0, cabBlob.Length);

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

    public static byte[] SearchTable(CabMapHandle map, string query, string[]? flatRules,
        string sortColumn, int sortDirection)
    {
        ArgumentNullException.ThrowIfNull(map);
        int[] ids = map.Search.Search(query, ParseFlatRules(flatRules), sortColumn, sortDirection);
        return IntsToBytes(ids, ids.Length);
    }

    public static byte[] SortRows(CabMapHandle map, int[] rowIds, string sortColumn, int sortDirection)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rowIds);
        int[] ids = map.Search.SortIds(rowIds, sortColumn, sortDirection);
        return IntsToBytes(ids, ids.Length);
    }

    private static List<FilterRule>? ParseFlatRules(string[]? flatRules)
    {
        if (flatRules is null || flatRules.Length == 0)
        {
            return null;
        }
        if (flatRules.Length % 5 != 0)
        {
            throw new ArgumentException($"flatRules length {flatRules.Length} is not a multiple of 5 (field, relation, value, action, enabled)");
        }
        List<FilterRule> rules = new(flatRules.Length / 5);
        for (int i = 0; i < flatRules.Length; i += 5)
        {
            rules.Add(new FilterRule(
                Field: flatRules[i],
                Relation: flatRules[i + 1],
                Value: flatRules[i + 2],
                Include: flatRules[i + 3] != "exclude",
                Enabled: flatRules[i + 4] == "1"));
        }
        return rules;
    }

    public static string[] ResolveCabsForPaths(CabMapHandle map, string[] containerPaths)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveCabsForPaths(map.Table, containerPaths);
    }

    public static string[] ResolveClosureCabNames(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveClosureCabNames(map.Table, seedCabNames);
    }

    public static string[] ResolveReverseClosureCabNames(CabMapHandle map, string[] seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(map);
        return CabMap.ResolveReverseClosureCabNames(map.Table, seedCabNames);
    }

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

    public sealed class SolvedHumanoidClipDto
    {
        public required string MetaJson { get; init; }
        public required byte[] Curves { get; init; }
        public required string[] ConsumedAttributes { get; init; }
        public required int SolvedCurveCount { get; init; }
    }

    /// <summary>
    /// Flat (bone name, human slot name) pairs an avatar's human rig states, or an empty
    /// array when it has none. The slot vocabulary is Unity's own humanoid enum, and the
    /// index chain that resolves a slot to a real bone (HumanBoneIndex -> node -> id ->
    /// TOS path) already lives here, so a consumer that wants to classify a skeleton's
    /// bones asks rather than restating either. A generic avatar is silent by
    /// construction: it has no human rig to describe.
    /// </summary>
    public static string[] DescribeHumanoidBones(string avatarDocumentJson)
    {
        Humanoid.AvatarRigInput? input = Humanoid.AvatarRigInput.FromDocumentJson(avatarDocumentJson);
        if (input is null || input.HumanBoneIndex.Length == 0)
        {
            return Array.Empty<string>();
        }

        List<string> pairs = new();
        void Add(int node, string slot)
        {
            if ((uint)node >= (uint)input.NodeId.Length)
            {
                return;
            }
            if (!input.Tos.TryGetValue(input.NodeId[node], out string? path) || path.Length == 0)
            {
                return;
            }
            int cut = path.LastIndexOf('/');
            pairs.Add(cut < 0 ? path : path[(cut + 1)..]);
            pairs.Add(slot);
        }

        for (int slot = 0; slot < input.HumanBoneIndex.Length; slot++)
        {
            Add(input.HumanBoneIndex[slot],
                Enum.IsDefined(typeof(BoneType), slot)
                    ? ((BoneType)slot).ToString()
                    : "Body" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach ((int[] hand, string side) in
                 new[] { (input.LeftHandBoneIndex, "Left"), (input.RightHandBoneIndex, "Right") })
        {
            foreach (int node in hand)
            {
                Add(node, side + "Finger");
            }
        }
        return pairs.ToArray();
    }

    public static SolvedHumanoidClipDto? SolveHumanoidClip(string avatarDocumentJson,
        string clipMetaJson, byte[] clipFloatCurves)
    {
        (List<(string Attribute, Animation.HermiteCurve Curve)> channels, float sampleRate,
            bool keepXZ, bool keepY, bool keepOrientation) =
            ClipCurveBlob.ReadFloatChannels(clipMetaJson, clipFloatCurves);
        if (!Humanoid.HumanoidClipGenericizer.HasMuscleChannel(channels))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(avatarDocumentJson))
        {
            throw new InvalidOperationException(
                "this clip is muscle-encoded but the target armature carries no avatar stamp "
                + "(ruri_unity_avatar) -- re-import the character once; the stamp then travels "
                + "with the skeleton and any clip retargets onto it.");
        }

        Humanoid.AvatarMuscleReferential referential =
            Humanoid.AvatarMuscleReferential.TryCreateFromDocument(avatarDocumentJson)
            ?? throw new InvalidOperationException(
                "the stamped avatar carries no human rig (a generic avatar, or a stub with an "
                + "empty m_TOS) -- a muscle-encoded clip cannot be solved against this skeleton.");

        Humanoid.SolvedHumanoidPose? pose = Humanoid.HumanoidClipGenericizer.Solve(
            referential, channels, sampleRate, keepXZ, keepY, keepOrientation);
        if (pose is null)
        {
            return null;
        }

        string[] consumed = channels
            .Select(channel => channel.Attribute)
            .Where(attribute => Humanoid.AvatarMuscleReferential.IsMuscleAttribute(attribute)
                || Humanoid.AvatarMuscleReferential.IsRootAttribute(attribute))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        (string metaJson, byte[] curves) = ClipCurveBlob.BuildSolved(
            pose, consumed, keepXZ, keepY, keepOrientation);
        return new SolvedHumanoidClipDto
        {
            MetaJson = metaJson,
            Curves = curves,
            ConsumedAttributes = consumed,
            SolvedCurveCount = pose.BoneRotations.Count
                + (pose.HipsPositions is null ? 0 : 1)
                + (pose.Motion is null ? 0 : 2),
        };
    }

    /// <summary>
    /// What a facial retarget answers with: the JSON diagnosis, and the posed bones as raw
    /// float32 (frame-major [frame][bone][10] -- position xyz, rotation xyzw, scale xyz).
    /// Neutral on purpose: a game states what a face IS, this layer only carries the answer.
    /// </summary>
    public sealed record SolvedFaceDto(string Json, byte[] Poses);

    /// <summary>
    /// The game that knows what a face is, or null in a build with none.
    ///
    /// A facial system is a GAME's own vocabulary -- ctrl-driven bone deltas, avatar tables,
    /// a shared expression library -- so the solver lives with that game's hook and registers
    /// itself here (see Endfield.EndfieldFaceRetarget). This file must not name it: the pure
    /// release compiles with every game hook removed, and a direct reference would take the
    /// generic bridge down with them.
    /// </summary>
    private static Func<CabTable, string, byte[], SolvedFaceDto>? _faceRetargetSolver;

    public static void RegisterFaceRetargetSolver(Func<CabTable, string, byte[], SolvedFaceDto> solver)
    {
        _faceRetargetSolver = solver;
    }

    /// <summary>
    /// Play one character's baked facial performance on another character's face, in the
    /// destination's own ctrl vocabulary.
    ///
    /// The caller states only what it alone knows -- the bones its rig has, with their rest
    /// transforms, and the sampled clip. Which face tables exist, which one the clip was
    /// authored on, which Euler convention they are written in and which named expressions
    /// the performance IS are all measured by whichever game registered itself above,
    /// against that game's own data read straight out of the cabmap.
    /// </summary>
    public static SolvedFaceDto SolveFaceRetarget(CabMapHandle map, string requestJson,
        byte[] performance)
    {
        if (_faceRetargetSolver is null)
        {
            throw new InvalidOperationException(
                "no game in this build states what a face is, so a facial performance cannot "
                + "be restated on another character");
        }
        return _faceRetargetSolver(map.Table, requestJson, performance);
    }

    public static ClosureResult ImportCabs(CabMapHandle map, string[] seedCabNames, int[]? exportClassIds,
        string[]? exportAssetKeys, bool buildGraph, string[] acceptedTextureFormats)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(seedCabNames);
        return ImportCabsCore(map, seedCabNames, new ExportSelection(exportClassIds, exportAssetKeys),
            buildGraph, acceptedTextureFormats, null, null);
    }

    /// <summary>
    /// The massed-placement primitive: resolve seed CONTAINER PATHS (a scene window's distinct
    /// mesh/prefab/material paths, "##sub" sub-asset suffixes included) to their CABs, load and
    /// process that dependency closure IN FULL, but serialize ONLY the assets reachable from the
    /// seeds over real PPtr edges (renderer -> material -> texture, prefab -> hierarchy), minus
    /// the excluded classes. A streaming closure's shared CABs co-host an order of magnitude more
    /// assets than a window actually places; full-closure export priced every one of them in YAML
    /// text and PNG encode. ClosureResult.SeedAssetGuids maps every resolved seed
    /// path to its exported asset guid ("a.fbx##sub" to that named sub-mesh), which is the whole
    /// join a host needs -- no name-index scan over the closure on the other side.
    /// </summary>
    public static ClosureResult ImportReachable(CabMapHandle map, string[] seedContainerPaths,
        string[]? excludedClassNames, string[] acceptedTextureFormats)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(seedContainerPaths);
        HashSet<int> excluded = new();
        foreach (string name in excludedClassNames ?? Array.Empty<string>())
        {
            excluded.UnionWith(ResolveClassIds(name));
        }
        string[] seedCabs = CabMap.ResolveCabsForPaths(map.Table, seedContainerPaths);
        return ImportCabsCore(map, seedCabs, null, false, acceptedTextureFormats,
            seedContainerPaths, excluded);
    }

    /// <summary>
    /// Every class id a UNITY class name covers. One Unity class can occupy several ids across
    /// versions, which this enum disambiguates by suffixing the number ("VideoClip" is stored as
    /// VideoClip_327 and VideoClip_329) -- an artifact of how the enum is generated, not a fact
    /// about Unity, so a caller states the Unity name and this resolves the spread. An unknown
    /// name throws with the near misses rather than silently excluding nothing.
    /// </summary>
    private static List<int> ResolveClassIds(string className)
    {
        List<int> ids = new();
        foreach (string candidate in Enum.GetNames<ClassIDType>())
        {
            int underscore = candidate.LastIndexOf('_');
            bool suffixed = underscore > 0
                && candidate.AsSpan(underscore + 1).ToString() is { Length: > 0 } tail
                && tail.All(char.IsAsciiDigit);
            string bare = suffixed ? candidate[..underscore] : candidate;
            if (string.Equals(bare, className, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add((int)Enum.Parse<ClassIDType>(candidate));
            }
        }
        if (ids.Count == 0)
        {
            throw new ArgumentException(
                $"'{className}' is not a Unity class name (ClassIDType). Nearest: " +
                string.Join(", ", Enum.GetNames<ClassIDType>()
                    .Where(name => name.Contains(className, StringComparison.OrdinalIgnoreCase))
                    .Take(5)));
        }
        return ids;
    }

    private sealed class ExportSelection
    {
        private readonly HashSet<int>? _classIds;
        private readonly HashSet<string>? _assetKeys;
        private readonly HashSet<IUnityObjectBase>? _admittedObjects;
        private Dictionary<AssetCollection, string>? _identities;

        public ExportSelection(int[]? classIds, string[]? assetKeys)
        {
            _classIds = classIds is null ? null : new HashSet<int>(classIds);
            _assetKeys = assetKeys is null ? null : new HashSet<string>(assetKeys, StringComparer.Ordinal);
        }

        public ExportSelection(HashSet<IUnityObjectBase> admittedObjects)
        {
            _admittedObjects = admittedObjects;
        }

        public bool HasObjectSet => _admittedObjects is not null;

        public bool IsUnrestricted => _admittedObjects is null && _classIds is null && _assetKeys is null;

        public bool MightExportEveryAssetOfClass(int classId) =>
            _admittedObjects is null && _assetKeys is null && (_classIds is null || _classIds.Contains(classId));

        public bool NeedsIdentities => _assetKeys is not null;

        public void BindIdentities(GameData gameData) =>
            _identities = ClosureGraphBlob.CollectionIdentities(gameData);

        public string KeyOf(IUnityObjectBase asset) =>
            _identities is null
                ? string.Empty
                : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{_identities[asset.Collection]}|{asset.PathID}");

        public bool Admits(IUnityObjectBase asset)
        {
            if (_admittedObjects is not null)
            {
                if (_admittedObjects.Contains(asset))
                {
                    return true;
                }
                // An export collection routes some assets through a wrapper (a texture's
                // SpriteInformationObject MainAsset); admit each side whenever the other is in.
                if (asset.MainAsset is { } main && !ReferenceEquals(main, asset)
                    && _admittedObjects.Contains(main))
                {
                    return true;
                }
                return asset is AssetRipper.Processing.Textures.SpriteInformationObject sprite
                    && sprite.Texture is { } texture && _admittedObjects.Contains(texture);
            }
            return (_classIds is null || _classIds.Contains(asset.ClassID))
                && (_assetKeys is null || _assetKeys.Contains(KeyOf(asset)));
        }
    }

    private static HashSet<IUnityObjectBase> CollectReachable(GameData gameData,
        string[] seedContainerPaths, HashSet<int> excludedClassIds, out int unresolvedSeeds)
    {
        // Three keyings of the same assets, because a seed states the path the GAME files an
        // asset under and OriginalPath states the path this export believes in, and the two
        // disagree in two measured ways: the extension (a ".mesh" container is written as
        // ".asset") and the whole folder ("Packages/com.hg.../M_x.mat" against an Assets-rooted
        // export path). Full path, then path-without-extension, then the bare leaf name --
        // each strictly weaker, and the leaf is only trusted when it names exactly one asset,
        // since admitting the wrong asset is worse than reporting the seed.
        Dictionary<string, List<IUnityObjectBase>> byPath = new(StringComparer.Ordinal);
        Dictionary<string, List<IUnityObjectBase>> byLeaf = new(StringComparer.Ordinal);
        static void Index(Dictionary<string, List<IUnityObjectBase>> index, string key,
            IUnityObjectBase asset)
        {
            if (!index.TryGetValue(key, out List<IUnityObjectBase>? list))
            {
                index[key] = list = new List<IUnityObjectBase>();
            }
            list.Add(asset);
        }
        foreach (AssetCollection collection in gameData.GameBundle.FetchAssetCollections())
        {
            foreach (IUnityObjectBase asset in collection)
            {
                string? original = asset.OriginalPath;
                if (string.IsNullOrEmpty(original))
                {
                    continue;
                }
                string normalized = NormalizeExportPath(original);
                Index(byPath, normalized, asset);
                string stem = StripExtension(normalized);
                if (!string.Equals(stem, normalized, StringComparison.Ordinal))
                {
                    Index(byPath, stem, asset);
                }
                Index(byLeaf, LeafOf(stem), asset);
            }
        }

        HashSet<IUnityObjectBase> admitted = new(ReferenceEqualityComparer.Instance);
        Queue<IUnityObjectBase> frontier = new();
        unresolvedSeeds = 0;
        foreach (string seed in seedContainerPaths)
        {
            string normalized = NormalizeExportPath(seed);
            if (!byPath.TryGetValue(normalized, out List<IUnityObjectBase>? assets)
                && !byPath.TryGetValue(StripExtension(normalized), out assets))
            {
                byLeaf.TryGetValue(LeafOf(StripExtension(normalized)), out List<IUnityObjectBase>? byName);
                if (byName is not { Count: 1 })
                {
                    unresolvedSeeds++;
                    continue;
                }
                assets = byName;
            }
            foreach (IUnityObjectBase asset in assets)
            {
                if (!excludedClassIds.Contains(asset.ClassID) && admitted.Add(asset))
                {
                    frontier.Enqueue(asset);
                }
            }
        }

        while (frontier.Count > 0)
        {
            IUnityObjectBase asset = frontier.Dequeue();
            foreach ((string _, PPtr pointer) in asset.FetchDependencies())
            {
                if (pointer.PathID == 0)
                {
                    continue;
                }
                IUnityObjectBase? target = asset.Collection.TryGetAsset(pointer);
                if (target is null || excludedClassIds.Contains(target.ClassID))
                {
                    continue;
                }
                if (admitted.Add(target))
                {
                    frontier.Enqueue(target);
                }
            }
        }
        return admitted;
    }

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

    private static ClosureResult ImportCabsCore(CabMapHandle map, string[] seedCabNames, ExportSelection? selection,
        bool buildGraph, string[] acceptedTextureFormats,
        string[]? seedContainerPaths, HashSet<int>? reachableExcludedClassIds)
    {
        ArgumentNullException.ThrowIfNull(acceptedTextureFormats);

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
        // Dummy, always: no host on this bridge can USE a shader (Blender rebuilds the game's
        // shading as its own node graphs), and a material only ever reads the shader's NAME --
        // which the dummy exporter still writes as its `Shader "<name>"` head line. Decompiling
        // HLSL was pure cost with no consumer.
        settings.ExportSettings.ShaderExportMode = ShaderExportMode.Dummy;
        settings.ImportSettings.ScriptContentLevel = AssetRipper.Import.Configuration.ScriptContentLevel.Level0;
        settings.ExportSettings.PreferOriginalTextureExtension = true;
        settings.ExportSettings.ImageExportFormat = AssetRipper.Export.Configuration.ImageExportFormat.Png;
        ClipCaptureExporter clipCapture = new();
        MeshCaptureExporter meshCapture = new();
        PrewarmedTextureExporter textureExporter = new(settings, ParseTextureFormats(acceptedTextureFormats));
        BridgeExportHandler handler = new(settings, clipCapture, meshCapture, textureExporter);

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

        // Reachable mode: the admitted set only exists once the closure is loaded and
        // processed (OriginalPath assignment happens there), so the selection is built here.
        phase.Restart();
        int unresolvedSeeds = 0;
        if (seedContainerPaths is not null)
        {
            selection = new ExportSelection(CollectReachable(gameData, seedContainerPaths,
                reachableExcludedClassIds ?? new HashSet<int>(), out unresolvedSeeds));
        }
        long reachMs = phase.ElapsedMilliseconds;
        handler.Selection = selection!;
        clipCapture.Selection = selection;

        phase.Restart();
        if (selection!.NeedsIdentities)
        {
            selection.BindIdentities(gameData);
        }
        (string graphMetaJson, byte[] graphPayload) = buildGraph
            ? ClosureGraphBlob.Build(gameData)
            : (string.Empty, Array.Empty<byte>());
        long graphMs = phase.ElapsedMilliseconds;

        phase.Restart();
        if (selection.HasObjectSet)
        {
            textureExporter.Prewarm(gameData, selection.Admits);
        }
        else if (selection.MightExportEveryAssetOfClass((int)ClassIDType.Texture2D))
        {
            textureExporter.Prewarm(gameData, null);
        }
        long prewarmMs = phase.ElapsedMilliseconds;

        InMemoryFileSystem memoryFileSystem = new();
        phase.Restart();
        handler.Export(gameData, "mem:/out", memoryFileSystem);
        long exportMs = phase.ElapsedMilliseconds;

        phase.Restart();
        ClosureResult result = Partition(memoryFileSystem.Files, map.Table, seedCabNames,
            clipCapture.Captured, meshCapture.Captured, graphMetaJson, graphPayload, seedContainerPaths);
        Logger.Info(LogCategory.Export,
            $"[ImportCabs] closure={closure.ClosureCount} files={closureFiles.Length} " +
            $"resolve={resolveMs}ms load={loadMs}ms process={processMs}ms reach={reachMs}ms graph={graphMs}ms " +
            $"prewarm={prewarmMs}ms export={exportMs}ms partition={phase.ElapsedMilliseconds}ms " +
            $"seeds={(seedContainerPaths is null ? "-" : $"{seedContainerPaths.Length}(unresolved {unresolvedSeeds})")} " +
            $"meshblobs={result.MeshBlobMeta.Count}/{meshCapture.MeshCount} " +
            $"texcache(hit={textureExporter.HitStats.Hits} miss={textureExporter.HitStats.Misses})");
        textureExporter.LogStats();
        LogExportCostByExtension(memoryFileSystem);
        return result;
    }

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

    private sealed class BridgeExportHandler : ExportHandler
    {
        private readonly ClipCaptureExporter _clipCapture;
        private readonly MeshCaptureExporter _meshCapture;
        private readonly PrewarmedTextureExporter _textureExporter;

        // Assigned between Process and Export: reachable mode can only compute its admitted
        // set from the LOADED closure, and this handler has to exist before Load runs.
        public ExportSelection? Selection { get; set; }

        public BridgeExportHandler(FullConfiguration settings, ClipCaptureExporter clipCapture,
            MeshCaptureExporter meshCapture, PrewarmedTextureExporter textureExporter) : base(settings)
        {
            _clipCapture = clipCapture;
            _meshCapture = meshCapture;
            _textureExporter = textureExporter;
        }

        protected override void BeforeExport(ProjectExporter projectExporter)
        {
            ExportSelection selection = Selection
                ?? throw new InvalidOperationException("Selection must be assigned before Export.");
            projectExporter.OverrideExporter<IAnimationClip>(_clipCapture, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh>(
                _meshCapture, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D>(
                _textureExporter, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.Processing.Textures.SpriteInformationObject>(
                _textureExporter, allowInheritance: true);
            if (!selection.IsUnrestricted)
            {
                projectExporter.OverrideExporter<IUnityObjectBase>(
                    new SelectionFilterExporter(selection), allowInheritance: true);
            }
        }
    }

    private sealed class SelectionFilterExporter(ExportSelection selection) : IAssetExporter
    {
        public bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            if (selection.Admits(asset))
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
            _exportSprites = configuration.ExportSettings.SpriteExportMode
                is not AssetRipper.Export.Configuration.SpriteExportMode.Yaml;
        }

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
            if (asset.MainAsset is AssetRipper.Processing.Textures.SpriteInformationObject spriteInformation
                && (_exportSprites || asset is not AssetRipper.SourceGenerated.Classes.ClassID_213.ISprite))
            {
                exportCollection = new NegotiatedTextureExportCollection(this, spriteInformation, _exportSprites);
                return true;
            }
            exportCollection = null;
            return false;
        }

        public void Prewarm(GameData gameData, Func<IUnityObjectBase, bool>? admit)
        {
            List<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D> targets = new();
            foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
            {
                if (asset is AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D texture
                    && asset.MainAsset is AssetRipper.Processing.Textures.SpriteInformationObject
                    && (admit is null || admit(asset))
                    && texture.CheckAssetIntegrity())
                {
                    targets.Add(texture);
                }
            }

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

    private sealed class ClipCaptureExporter : IAssetExporter
    {
        private readonly DefaultYamlExporter _inner = new();

        public ExportSelection? Selection { get; set; }

        public List<(string Cab, string Path, string AssetKey, string MetaJson, byte[] Curves)> Captured { get; } = new();

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
                    Logger.Warning(LogCategory.Export, $"Clip curve blob failed for '{asset.GetBestName()}': {exception.Message} -- the Blender side cannot import this clip's curves (no YAML fallback).");
                }
            }
            Captured.Add((asset.Collection.Name.ToLowerInvariant(), path,
                Selection?.KeyOf(asset) ?? string.Empty, metaJson, curves));
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

    internal sealed class MeshCaptureExporter : IAssetExporter
    {
        private readonly YamlStreamedAssetExporter _inner = new();

        public List<(string Path, string Name, string MetaJson, byte[] Payload)> Captured { get; } = new();

        public int MeshCount { get; private set; }

        public bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            exportCollection = new YamlStreamedAssetExportCollection(this, asset);
            return true;
        }

        public bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
        {
            if (asset is not AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh mesh)
            {
                return _inner.Export(container, asset, path, fileSystem);
            }

            MeshCount++;
            MeshRawBlobResult blob = MeshRawBlob.Build(mesh);
            if (!blob.HasBlob)
            {
                Logger.Warning(LogCategory.Export,
                    $"No mesh raw blob for '{asset.GetBestName()}': {blob.SkipReason} -- host side falls back to YAML parsing.");
                return _inner.Export(container, asset, path, fileSystem);
            }

            Captured.Add((path, mesh.Name.String, blob.MetaJson, blob.Payload));
            return ExportWithoutGeometry(container, mesh, path, fileSystem);
        }

        private bool ExportWithoutGeometry(IExportContainer container,
            AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh mesh, string path, FileSystem fileSystem)
        {
            byte[] vertexData = mesh.VertexData.Data;
            byte[] indexBuffer = mesh.IndexBuffer;
            mesh.VertexData.Data = [];
            mesh.IndexBuffer = [];
            try
            {
                return _inner.Export(container, mesh, path, fileSystem);
            }
            finally
            {
                mesh.VertexData.Data = vertexData;
                mesh.IndexBuffer = indexBuffer;
            }
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



    public static ColumnTableDto GameDataTable(CabMapHandle? map, string datasetId, string[] args, CancellationToken cancellation)
    {
        (string handle, Tables.ColumnTable table) = Data.Datasets.Table(datasetId, args ?? [], cancellation, map?.Table);
        (string name, int rowCount, string[] columns, string[] kinds, byte[][] blobs, byte[][] offsets) =
            Data.ColumnTablePacking.Pack(table);
        return new ColumnTableDto(handle, name, rowCount, columns, kinds, blobs, offsets);
    }

    public static byte[] GameDataBlob(CabMapHandle? map, string datasetId, string[] args, CancellationToken cancellation) =>
        Data.Datasets.Blob(datasetId, args ?? [], cancellation, map?.Table);

    public static byte[] SearchDataTable(string handle, string query, string[]? flatRules)
    {
        int[] rows = TableRegistry.Search(handle, query, ParseFlatRules(flatRules));
        return IntsToBytes(rows, rows.Length);
    }

    public static string OpenHostTable(string handle, string[] columns, string[] flatValues)
        => TableRegistry.OpenHostTable(handle, columns, flatValues);

    private static T VfsFuncOrThrow<T>(T? func) where T : class =>
        func ?? throw new InvalidOperationException(
            "No VFS game hook active -- call Initialize(...) with a VFS-game hook id (e.g. \"Endfield_1.3.3\") first.");

    private static ClosureResult Partition(IReadOnlyDictionary<string, byte[]> files,
        CabTable table, string[] seedCabNames,
        List<(string Cab, string Path, string AssetKey, string MetaJson, byte[] Curves)> capturedClips,
        List<(string Path, string Name, string MetaJson, byte[] Payload)> capturedMeshes,
        string graphMetaJson, byte[] graphPayload, string[]? seedContainerPaths)
    {
        Dictionary<string, byte[]> assets = new(StringComparer.Ordinal);
        Dictionary<string, string> assetPaths = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> other = new(StringComparer.OrdinalIgnoreCase);
        List<string> roots = new();
        List<string> sceneRoots = new();
        Dictionary<string, string> pathToGuid = new(StringComparer.OrdinalIgnoreCase);
        // The same exported paths keyed more weakly, for the seed join below: without
        // their extension, and by bare leaf name. See CollectReachable for why both
        // exist -- the game's container path and the export path disagree on the
        // extension AND, for package assets, on the whole folder.
        Dictionary<string, List<string>> guidsByStem = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> guidsByLeaf = new(StringComparer.OrdinalIgnoreCase);
        UTF8Encoding utf8 = new(false);

        List<(string Guid, string NormalizedPath)> rootPaths = new();

        foreach ((string path, byte[] bytes) in files)
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;            }
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
            string normalizedPath = NormalizeExportPath(path);
            pathToGuid[normalizedPath] = guid;
            string pathStem = StripExtension(normalizedPath);
            if (!guidsByStem.TryGetValue(pathStem, out List<string>? stemGuids))
            {
                guidsByStem[pathStem] = stemGuids = new List<string>();
            }
            stemGuids.Add(guid);
            string leaf = LeafOf(pathStem);
            if (!guidsByLeaf.TryGetValue(leaf, out List<string>? leafGuids))
            {
                guidsByLeaf[leaf] = leafGuids = new List<string>();
            }
            leafGuids.Add(guid);

            assets[guid] = bytes;
            assetPaths[guid] = path;
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(guid);
                rootPaths.Add((guid, NormalizeExportPath(path)));
            }
            else if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
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
            if (pathCount == 0
                && pathToGuid.TryGetValue($"assets/scenes/{seedCab.ToLowerInvariant()}.unity", out string? sceneGuid))
            {
                seedRoots[seedCab] = sceneGuid;
            }
        }

        Dictionary<string, string[]> clipGuidsByCab = capturedClips
            .Select(c => (c.Cab, Guid: pathToGuid.GetValueOrDefault(NormalizeExportPath(c.Path))))
            .Where(c => c.Guid is not null)
            .GroupBy(c => c.Cab, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Guid!).Distinct().ToArray(), StringComparer.Ordinal);

        Dictionary<string, string> clipGuidByAssetKey = new(StringComparer.Ordinal);
        Dictionary<string, string> clipCurveMeta = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> clipCurveData = new(StringComparer.Ordinal);
        foreach ((string _, string path, string assetKey, string metaJson, byte[] curves) in capturedClips)
        {
            string? clipGuid = pathToGuid.GetValueOrDefault(NormalizeExportPath(path));
            if (clipGuid is null)
            {
                continue;
            }
            clipGuidByAssetKey[assetKey] = clipGuid;
            if (metaJson.Length > 0)
            {
                clipCurveMeta[clipGuid] = metaJson;
                clipCurveData[clipGuid] = curves;
            }
        }

        Dictionary<string, string> meshBlobMeta = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> meshBlobData = new(StringComparer.Ordinal);
        Dictionary<string, string> meshGuidByName = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string name, string metaJson, byte[] payload) in capturedMeshes)
        {
            string? guid = pathToGuid.GetValueOrDefault(NormalizeExportPath(path));
            if (guid is not null)
            {
                meshBlobMeta[guid] = metaJson;
                meshBlobData[guid] = payload;
                if (name.Length > 0)
                {
                    meshGuidByName[name] = guid;
                }
            }
        }

        // Every seed container path -> the exported guid it names. "container##sub" names a
        // mesh SUB-ASSET, joined by the mesh's own m_Name (the ranked mesh-name convention the
        // placement data states); a plain path is the container itself. No name-index scan is
        // ever needed on the consuming side, and a seed that resolves to nothing stays absent
        // -- the host counts it as unresolved instead of guessing.
        //
        // Three spellings, tried in order, because the seed states the path the GAME files an
        // asset under and the export states the path AssetRipper wrote:
        //   1. the exact path (the overwhelmingly common case);
        //   2. the path without its extension -- a ".mesh" container exports as ".asset", and
        //      matching only on the full path silently dropped every foliage imposter card;
        //   3. the leaf name as a Mesh's own m_Name, for a container whose export landed
        //      somewhere else entirely.
        // A stem shared by several exported assets is NOT guessed at: placing the wrong asset
        // is worse than reporting the seed, so it falls through to the name match.
        Dictionary<string, string> seedAssetGuids = new(StringComparer.Ordinal);
        foreach (string seed in seedContainerPaths ?? Array.Empty<string>())
        {
            int hashIdx = seed.IndexOf("##", StringComparison.Ordinal);
            string? guid;
            if (hashIdx >= 0)
            {
                guid = meshGuidByName.GetValueOrDefault(seed[(hashIdx + 2)..]);
            }
            else
            {
                string normalized = NormalizeExportPath(seed);
                guid = pathToGuid.GetValueOrDefault(normalized);
                if (guid is null)
                {
                    string stem = StripExtension(normalized);
                    if (guidsByStem.TryGetValue(stem, out List<string>? byStem)
                        && byStem.Count == 1)
                    {
                        guid = byStem[0];
                    }
                    else if (guidsByLeaf.TryGetValue(LeafOf(stem), out List<string>? byLeafName)
                        && byLeafName.Count == 1)
                    {
                        guid = byLeafName[0];
                    }
                    guid ??= meshGuidByName.GetValueOrDefault(LeafOf(stem));
                }
            }
            if (guid is not null)
            {
                seedAssetGuids[seed] = guid;
            }
        }

        return new ClosureResult(assets, other, roots.ToArray(), seedRoots, clipGuidsByCab,
            sceneRoots.ToArray(), clipCurveMeta, clipCurveData, meshBlobMeta, meshBlobData, rootCabs,
            assetPaths, clipGuidByAssetKey, seedAssetGuids, graphMetaJson, graphPayload);
    }

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
                    return null;                }
                extMatch = guid;
            }
            stemMatch = guid;
            stemMatches++;
        }
        return extMatch ?? (stemMatches == 1 ? stemMatch : null);
    }

    private static string StripExtension(string normalizedPath)
    {
        int dot = normalizedPath.LastIndexOf('.');
        return dot > normalizedPath.LastIndexOf('/') ? normalizedPath[..dot] : normalizedPath;
    }

    private static string LeafOf(string normalizedPath) =>
        normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];

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

public sealed class CabMapHandle
{
    private CabTableSearch? _search;

    public string CabMapPath { get; }
    public CabTable Table { get; }
    public string BaseFolder => Table.BaseFolder;

    public CabTableSearch Search => _search ??= CabTableSearch.For(Table);

    internal CabMapHandle(string cabMapPath, CabTable table)
    {
        CabMapPath = cabMapPath;
        Table = table;
    }
}

public sealed record PackedTableDto(
    int Count,
    byte[] CabBlob, byte[] CabOffsets,
    byte[] SourceBlob, byte[] SourceOffsets,
    byte[] PathBlob, byte[] PathOffsets, byte[] PathStarts,
    byte[] ClassFlat, byte[] ClassStarts,
    byte[] DependencyCounts,
    string ClassIdNames);

public sealed record ColumnTableDto(
    string Handle, string Name, int RowCount, string[] Names, string[] Kinds, byte[][] Blobs, byte[][] Offsets);

public sealed record VfsFileDto(string FileName, long FileNameHash, string BlockType, long Length, string ChkPath);

public sealed record SceneChunkSummaryDto(
    int[] SceneStateIds, int AnchoredFiles, long AnchoredBytes, int FloatingFiles, long FloatingBytes);

public sealed record SceneDiscoveryDto(
    int Total, int NoTransform, int LodFiltered, int DistinctAssets, string[] SeedPaths,
    ScenePlacementDto[] Placements);

public sealed record NpcMaterialAssignmentDto(string PartName, string MeshName, string[] Materials);

public sealed record SceneLandmarkDto(
    string LevelId, bool IsSingleLevel, float MinX, float MinZ, float MaxX, float MaxZ);

public sealed record ScenePlacementDto(
    string AssetPath, long AssetHash, string EntityName, string SourceChunk,
    float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw, float Sx, float Sy, float Sz,
    string[] MaterialAssetPaths);

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
    IReadOnlyDictionary<string, string> AssetPaths,
    IReadOnlyDictionary<string, string> ClipGuidByAssetKey,
    IReadOnlyDictionary<string, string> SeedAssetGuids,
    string GraphMetaJson,
    byte[] GraphPayload)
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
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        string.Empty,
        Array.Empty<byte>());
}
