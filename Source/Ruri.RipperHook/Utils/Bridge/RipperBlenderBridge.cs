using AssetRipper.Assets;
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
using Ruri.Hook.Config;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.Bridge;

public static class RipperBlenderBridge
{
    private static bool _loggingConfigured;

    public static string[] ListAvailableHooks() =>
        Ruri.Hook.RuriHook.GetAvailableHooks()
            .SelectMany(h => Ruri.Hook.RuriHook.BuildHookIds(h.Attribute))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string[] ListGameHooks() =>
        Ruri.Hook.RuriHook.GetAvailableHooks()
            .Where(h => h.Attribute.IsGameSpecific)
            .SelectMany(h => Ruri.Hook.RuriHook.BuildHookIds(h.Attribute))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    public static void Initialize(IEnumerable<string> enabledHookIds, string gameRoot)
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
        foreach (string id in enabledHookIds)
        {
            config.EnabledHooks.Add(NormalizeHookId(id));
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

    public static ClosureResult ImportCabs(CabMapHandle map, string[] seedCabNames, int[]? exportClassIds,
        string[]? exportAssetKeys, bool buildGraph, string[] acceptedTextureFormats)
    {
        return ImportCabsCore(map, seedCabNames, new ExportSelection(exportClassIds, exportAssetKeys),
            buildGraph, acceptedTextureFormats);
    }

    private sealed class ExportSelection
    {
        private readonly HashSet<int>? _classIds;
        private readonly HashSet<string>? _assetKeys;
        private Dictionary<AssetRipper.Assets.Collections.AssetCollection, string>? _identities;

        public ExportSelection(int[]? classIds, string[]? assetKeys)
        {
            _classIds = classIds is null ? null : new HashSet<int>(classIds);
            _assetKeys = assetKeys is null ? null : new HashSet<string>(assetKeys, StringComparer.Ordinal);
        }

        public bool IsUnrestricted => _classIds is null && _assetKeys is null;

        public bool MightExportEveryAssetOfClass(int classId) =>
            _assetKeys is null && (_classIds is null || _classIds.Contains(classId));

        public bool NeedsIdentities => _assetKeys is not null;

        public void BindIdentities(GameData gameData) =>
            _identities = ClosureGraphBlob.CollectionIdentities(gameData);

        public string KeyOf(IUnityObjectBase asset) =>
            _identities is null
                ? string.Empty
                : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{_identities[asset.Collection]}|{asset.PathID}");

        public bool Admits(IUnityObjectBase asset) =>
            (_classIds is null || _classIds.Contains(asset.ClassID))
            && (_assetKeys is null || _assetKeys.Contains(KeyOf(asset)));
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

    private static ClosureResult ImportCabsCore(CabMapHandle map, string[] seedCabNames, ExportSelection selection,
        bool buildGraph, string[] acceptedTextureFormats)
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
        settings.ImportSettings.ScriptContentLevel = AssetRipper.Import.Configuration.ScriptContentLevel.Level0;
        settings.ExportSettings.PreferOriginalTextureExtension = true;
        settings.ExportSettings.ImageExportFormat = AssetRipper.Export.Configuration.ImageExportFormat.Png;
        ClipCaptureExporter clipCapture = new(selection);
        MeshCaptureExporter meshCapture = new();
        PrewarmedTextureExporter textureExporter = new(settings, ParseTextureFormats(acceptedTextureFormats));
        BridgeExportHandler handler = new(settings, clipCapture, meshCapture, textureExporter, selection);

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

        phase.Restart();
        if (selection.NeedsIdentities)
        {
            selection.BindIdentities(gameData);
        }
        (string graphMetaJson, byte[] graphPayload) = buildGraph
            ? ClosureGraphBlob.Build(gameData)
            : (string.Empty, Array.Empty<byte>());
        long graphMs = phase.ElapsedMilliseconds;

        phase.Restart();
        if (selection.MightExportEveryAssetOfClass((int)ClassIDType.Texture2D))
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
            clipCapture.Captured, meshCapture.Captured, graphMetaJson, graphPayload);
        Logger.Info(LogCategory.Export,
            $"[ImportCabs] closure={closure.ClosureCount} files={closureFiles.Length} " +
            $"resolve={resolveMs}ms load={loadMs}ms process={processMs}ms graph={graphMs}ms " +
            $"prewarm={prewarmMs}ms export={exportMs}ms partition={phase.ElapsedMilliseconds}ms " +
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
        private readonly ExportSelection _selection;

        public BridgeExportHandler(FullConfiguration settings, ClipCaptureExporter clipCapture,
            MeshCaptureExporter meshCapture, PrewarmedTextureExporter textureExporter,
            ExportSelection selection) : base(settings)
        {
            _clipCapture = clipCapture;
            _meshCapture = meshCapture;
            _textureExporter = textureExporter;
            _selection = selection;
        }

        protected override void BeforeExport(ProjectExporter projectExporter)
        {
            projectExporter.OverrideExporter<IAnimationClip>(_clipCapture, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.SourceGenerated.Classes.ClassID_43.IMesh>(
                _meshCapture, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.SourceGenerated.Classes.ClassID_28.ITexture2D>(
                _textureExporter, allowInheritance: true);
            projectExporter.OverrideExporter<AssetRipper.Processing.Textures.SpriteInformationObject>(
                _textureExporter, allowInheritance: true);
            if (!_selection.IsUnrestricted)
            {
                projectExporter.OverrideExporter<IUnityObjectBase>(
                    new SelectionFilterExporter(_selection), allowInheritance: true);
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

        public void Prewarm(GameData gameData)
        {
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

    private sealed class ClipCaptureExporter(ExportSelection selection) : IAssetExporter
    {
        private readonly DefaultYamlExporter _inner = new();

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
            Captured.Add((asset.Collection.Name.ToLowerInvariant(), path, selection.KeyOf(asset), metaJson, curves));
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
            "No VFS game hook active -- call Initialize(...) with a VFS-game hook id (e.g. \"EndField_1.3.3\") first.");

    private static ClosureResult Partition(IReadOnlyDictionary<string, byte[]> files,
        CabTable table, string[] seedCabNames,
        List<(string Cab, string Path, string AssetKey, string MetaJson, byte[] Curves)> capturedClips,
        List<(string Path, string MetaJson, byte[] Payload)> capturedMeshes,
        string graphMetaJson, byte[] graphPayload)
    {
        Dictionary<string, byte[]> assets = new(StringComparer.Ordinal);
        Dictionary<string, string> assetPaths = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> other = new(StringComparer.OrdinalIgnoreCase);
        List<string> roots = new();
        List<string> sceneRoots = new();
        Dictionary<string, string> pathToGuid = new(StringComparer.OrdinalIgnoreCase);
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
            pathToGuid[NormalizeExportPath(path)] = guid;

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
            assetPaths, clipGuidByAssetKey, graphMetaJson, graphPayload);
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
        string.Empty,
        Array.Empty<byte>());
}
