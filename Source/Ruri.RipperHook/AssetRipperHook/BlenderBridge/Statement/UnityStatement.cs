using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_108;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_20;
using AssetRipper.SourceGenerated.Classes.ClassID_205;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_23;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Ruri.RipperHook.BlenderBridge;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Data;
using Ruri.RipperHook.Humanoid;
using System.Globalization;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// The engine's own flattening of a plan: the closure is loaded and processed as an import
/// would load it, and then -- on the objects, never through text -- every root's transform
/// tree, the renderers it draws, their meshes baked to the pose they display at rest, the
/// materials they wear resolved to surface roles, the pixels those materials read, and the
/// clips the seeds carry. What a game states beyond the engine (which archives, which subset,
/// which recipe) arrived in the plan; what is stated here is true of every Unity build.
/// </summary>
public sealed class UnityStatement
{
    private readonly CabTable _map;
    private readonly StatementPlan _plan;
    private readonly StatementOptions _options;
    private readonly Statement _statement = new();
    private readonly TextureRoles _roles;
    private readonly Dictionary<AssetCollection, string> _identities;
    private readonly GameData _gameData;
    private readonly HashSet<string> _seedCabs;
    private readonly Dictionary<IMesh, DecodedMesh> _decodedMeshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ITexture2D, string> _textureKeys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _writtenNames = new(StringComparer.Ordinal);
    private readonly List<ITexture2D> _texturesToEncode = [];

    public Statement Result => _statement;

    public GameData GameData => _gameData;

    public IReadOnlyDictionary<AssetCollection, string> Identities => _identities;

    private UnityStatement(CabTable map, StatementPlan plan, StatementOptions options, GameData gameData)
    {
        _map = map;
        _plan = plan;
        _options = options;
        _gameData = gameData;
        _roles = TextureRoles.Load(options.RoleLayers);
        _identities = ClosureGraphBlob.CollectionIdentities(gameData);
        _seedCabs = new HashSet<string>(plan.Cabs, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Load the plan's closure the way an import loads it: gated to the closure,
    /// scripts at level zero, static batching separated, then processed.</summary>
    public static GameData LoadClosure(CabTable map, IEnumerable<string> cabs)
    {
        CabClosure closure = ClosureReader.Resolve(map, cabs, reachThroughDependents: true);
        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ExportSettings.ShaderExportMode = ShaderExportMode.Dummy;
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
        settings.ProcessingSettings.EnableStaticMeshSeparation = true;
        ExportHandler handler = new(settings);
        GameData gameData = closure.Files.Length == 0
            ? handler.Load([], AssetRipper.IO.Files.LocalFileSystem.Instance)
            : ClosureReader.Load(closure, handler);
        if (gameData.GameBundle.HasAnyAssetCollections())
        {
            handler.Process(gameData);
        }
        return gameData;
    }

    public static Statement Flatten(CabTable map, StatementPlan plan, StatementOptions options)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        GameData gameData = LoadClosure(map, plan.Cabs);
        long read = System.Diagnostics.Stopwatch.GetTimestamp();
        UnityStatement flattening = new(map, plan, options, gameData);
        flattening.Run();
        TimeSpan total = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        TimeSpan reading = System.Diagnostics.Stopwatch.GetElapsedTime(started, read);
        Statement result = flattening.Result;
        AssetRipper.Import.Logging.Logger.Info(AssetRipper.Import.Logging.LogCategory.Import,
            $"[statement] {plan.Label}: read {plan.Cabs.Count} archive(s) in {reading.TotalMilliseconds:F0} ms, "
            + $"flattened {result.Nodes.Count} node(s) and {result.Meshes.Count} mesh(es) in "
            + $"{(total - reading - flattening._encoding).TotalMilliseconds:F0} ms, encoded "
            + $"{result.Textures.Count} texture(s) in {flattening._encoding.TotalMilliseconds:F0} ms");
        return result;
    }

    public string KeyOf(IUnityObjectBase asset) =>
        string.Create(CultureInfo.InvariantCulture, $"{_identities[asset.Collection]}|{asset.PathID}");

    private bool IsSeedCollection(AssetCollection collection) => _seedCabs.Contains(collection.Name);

    private void Run()
    {
        switch (_plan.Kind)
        {
            case StatementKind.Prefab:
            case StatementKind.Scene:
                foreach (IGameObject root in Roots())
                {
                    FlattenRoot(root);
                }
                break;
            case StatementKind.Loose:
                FlattenLoose();
                break;
            case StatementKind.Parts:
                FlattenParts();
                break;
            case StatementKind.Window:
                FlattenWindow();
                break;
            case StatementKind.Assembly:
                FlattenAssembly();
                break;
            default:
                throw new NotSupportedException($"statement kind {_plan.Kind} is not flattened by the engine path.");
        }
        foreach (string missing in _plan.Missing)
        {
            _statement.Note(_plan.Seed, "named by the plan but resolved to nothing", 1, missing);
        }
        Clips();
        EncodeTextures();
    }

    /// <summary>A recipe: named meshes out of the closure, each bind-baked onto ONE rig grown
    /// from the template avatar's standing rest, dressed in the materials the recipe names.
    /// A part's own avatar, filed beside its meshes, extends the rig by what it alone adds.</summary>
    private void FlattenParts()
    {
        PartsSkeleton template = _plan.Skeleton
            ?? throw new InvalidOperationException($"recipe '{_plan.Label}' names no skeleton to bind its meshes to.");
        HashSet<string> templateClosure = new(CabMap.ResolveClosureCabNames(_map, [template.Cab]), StringComparer.OrdinalIgnoreCase);
        IAvatar? avatar = null;
        List<string> leaves = [];
        foreach (IUnityObjectBase asset in _gameData.GameBundle.FetchAssets()
                     .Where(asset => templateClosure.Contains(asset.Collection.Name))
                     .OrderBy(asset => _identities[asset.Collection], StringComparer.Ordinal).ThenBy(asset => asset.PathID))
        {
            if (asset is IAvatar found && avatar is null)
            {
                avatar = found;
            }
            else if (asset is IMonoBehaviour behaviour && leaves.Count == 0
                && string.Equals(behaviour.Name.String, template.AvatarNameStem, StringComparison.OrdinalIgnoreCase))
            {
                leaves.AddRange(BonePathLeaves(behaviour));
            }
        }
        if (avatar is null)
        {
            throw new InvalidOperationException(
                $"recipe '{_plan.Label}' names avatar template '{template.AvatarNameStem}', and the closure of archive '{template.Cab}' carries no avatar.");
        }
        AvatarSkeleton standing = AvatarSkeleton.Read(avatar);
        if (standing.WorldRests.Count == 0)
        {
            throw new InvalidOperationException(
                $"avatar template '{template.AvatarNameStem}' carries no default pose, so its meshes have no rest to bind to.");
        }
        SharedSkeleton rig = new(leaves);
        rig.AddSkeleton(standing);

        Dictionary<string, List<IMesh>> meshesByName = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<IMaterial>> materialsByName = new(StringComparer.OrdinalIgnoreCase);
        List<IAvatar> avatars = [];
        foreach (IUnityObjectBase asset in _gameData.GameBundle.FetchAssets())
        {
            switch (asset)
            {
                case IMesh mesh:
                    Index(meshesByName, mesh.Name.String, mesh);
                    break;
                case IMaterial material:
                    Index(materialsByName, material.Name.String, material);
                    break;
                case IAvatar partAvatar when !ReferenceEquals(partAvatar, avatar):
                    avatars.Add(partAvatar);
                    break;
            }
        }

        int rootIndex = _statement.Nodes.Count;
        StatementNode rootRow = new()
        {
            Index = rootIndex,
            Parent = -1,
            Name = _plan.Label,
            Path = UnityNode.AnimatorRootPath,
            Kind = "empty",
            Active = true,
            Position = System.Numerics.Vector3.Zero,
            Rotation = System.Numerics.Quaternion.Identity,
            Scale = System.Numerics.Vector3.One,
            Shadows = null,
        };
        _statement.Nodes.Add(rootRow);
        _statement.Roots.Add(new StatementRoot(_plan.Seed, rootIndex, _plan.Label, "parts"));
        string skeletonKey = _plan.Seed;
        rootRow.Skeleton = skeletonKey;

        HashSet<IAvatar> merged = new(ReferenceEqualityComparer.Instance);
        foreach (PartMesh part in _plan.Meshes)
        {
            if (!meshesByName.TryGetValue(part.Name, out List<IMesh>? candidates) || candidates.Count == 0)
            {
                _statement.Note(_plan.Seed, "recipe mesh not in the closure", 1, part.Name);
                continue;
            }
            string folder = FolderOf(part.ContainerPath);
            if (folder.Length > 0)
            {
                foreach (IAvatar partAvatar in avatars)
                {
                    if (!merged.Contains(partAvatar)
                        && string.Equals(FolderOf(partAvatar.OriginalPath ?? string.Empty), folder, StringComparison.OrdinalIgnoreCase))
                    {
                        rig.AddSkeleton(AvatarSkeleton.Read(partAvatar));
                        merged.Add(partAvatar);
                        break;
                    }
                }
            }
            IMesh mesh = candidates[0];
            DecodedMesh decoded;
            try
            {
                decoded = UnityMeshDecoder.Decode(mesh);
            }
            catch (Exception exception)
            {
                _statement.Note(_plan.Seed, "mesh could not be decoded", 1, $"{part.Name}: {exception.Message}");
                continue;
            }
            if (!decoded.HasGeometry)
            {
                _statement.Note(_plan.Seed, "mesh decoded to zero vertices", 1, part.Name);
                continue;
            }
            bool baked = rig.Bind(decoded, out List<string> bonePaths);
            List<string> materialKeys = [];
            if (_plan.Dressing.TryGetValue(part.Name, out IReadOnlyList<string>? dressing))
            {
                foreach (string materialName in dressing)
                {
                    if (materialsByName.TryGetValue(materialName, out List<IMaterial>? materials) && materials.Count > 0)
                    {
                        materialKeys.Add(Material(materials[0]));
                    }
                    else
                    {
                        _statement.Note(_plan.Seed, "recipe material not in the closure", 1, $"{part.Name}: {materialName}");
                    }
                }
            }
            string key = KeyOf(mesh);
            StatementMesh row = new()
            {
                Key = key,
                Name = decoded.Name,
                Geometry = decoded,
                BonePaths = bonePaths,
                Skeleton = baked ? skeletonKey : string.Empty,
                Baked = baked,
            };
            _statement.Add(row);
            Morphs(row);
            _statement.Nodes.Add(new StatementNode
            {
                Index = _statement.Nodes.Count,
                Parent = rootIndex,
                Name = decoded.Name,
                Path = decoded.Name,
                Kind = baked ? "skinned" : "mesh",
                Active = true,
                Mesh = key,
                Skeleton = skeletonKey,
                Materials = materialKeys,
                Position = System.Numerics.Vector3.Zero,
                Rotation = System.Numerics.Quaternion.Identity,
                Scale = System.Numerics.Vector3.One,
                Shadows = null,
            });
            if (!baked)
            {
                _statement.Note(_plan.Seed, "recipe mesh carries no bind data and is placed unskinned", 1, part.Name);
            }
        }
        StatementSkeleton skeleton = new() { Key = skeletonKey, AvatarJson = AvatarStatement.ToJson(AvatarRigInput.FromAvatar(avatar)) };
        Dictionary<string, int> indexOfBone = new(StringComparer.Ordinal);
        foreach (SharedSkeleton.Bone bone in rig.Bones)
        {
            int index = skeleton.Bones.Count;
            indexOfBone[bone.Name] = index;
            (System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale) = rig.LocalRest(bone);
            skeleton.Bones.Add(new StatementBone(index, bone.Parent.Length > 0 && indexOfBone.TryGetValue(bone.Parent, out int parent) ? parent : -1,
                bone.Name, bone.Path, bone.Path, position, rotation, scale, string.Empty));
        }
        _statement.Skeletons.Add(skeleton);
    }

    private const string BuiltinMeshMarker = "renderpipelineresources/mesh/";

    /// <summary>A window of a scene: every placement the title reduced it to, each drawing an
    /// asset the closure holds under the path the game files it -- a loose mesh placed as one
    /// node, a prefab as its surviving pieces under one anchor -- with one mesh per distinct
    /// asset shared by every placement of it.</summary>
    private void FlattenWindow()
    {
        Dictionary<string, List<IUnityObjectBase>> byPath = new(StringComparer.Ordinal);
        Dictionary<string, List<IUnityObjectBase>> byLeaf = new(StringComparer.Ordinal);
        foreach (AssetCollection collection in _gameData.GameBundle.FetchAssetCollections())
        {
            foreach (IUnityObjectBase asset in collection)
            {
                string? original = asset.OriginalPath;
                if (string.IsNullOrEmpty(original))
                {
                    continue;
                }
                string normalized = NormalizePath(original);
                Index(byPath, normalized, asset);
                string stem = StripExtension(normalized);
                if (!string.Equals(stem, normalized, StringComparison.Ordinal))
                {
                    Index(byPath, stem, asset);
                }
                Index(byLeaf, LeafOf(stem), asset);
            }
        }

        List<IUnityObjectBase> Resolve(string path)
        {
            string normalized = NormalizePath(path);
            if (byPath.TryGetValue(normalized, out List<IUnityObjectBase>? assets)
                || byPath.TryGetValue(StripExtension(normalized), out assets))
            {
                return assets;
            }
            return byLeaf.TryGetValue(LeafOf(StripExtension(normalized)), out List<IUnityObjectBase>? byName) && byName.Count == 1
                ? byName
                : [];
        }

        int firstNode = _statement.Nodes.Count;
        _statement.Roots.Add(new StatementRoot(_plan.Seed, firstNode, _plan.Label, "window"));

        static System.Numerics.Quaternion Aimed(System.Numerics.Vector3 forward)
        {
            System.Numerics.Vector3 target = System.Numerics.Vector3.Normalize(forward);
            if (!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            {
                return System.Numerics.Quaternion.Identity;
            }
            System.Numerics.Vector3 source = System.Numerics.Vector3.UnitZ;
            float dot = System.Numerics.Vector3.Dot(source, target);
            if (dot > 0.999999f)
            {
                return System.Numerics.Quaternion.Identity;
            }
            if (dot < -0.999999f)
            {
                return new System.Numerics.Quaternion(0f, 1f, 0f, 0f);
            }
            System.Numerics.Vector3 axis = System.Numerics.Vector3.Cross(source, target);
            return System.Numerics.Quaternion.Normalize(
                new System.Numerics.Quaternion(axis.X, axis.Y, axis.Z, 1f + dot));
        }
        Dictionary<string, WindowSource> sources = new(StringComparer.Ordinal);
        HashSet<string> unresolved = new(StringComparer.Ordinal);
        HashSet<string> empty = new(StringComparer.Ordinal);
        foreach (PlanLight light in _plan.Lights)
        {
            _statement.Nodes.Add(new StatementNode
            {
                Index = _statement.Nodes.Count,
                Parent = -1,
                Name = light.Name,
                Path = light.Name,
                Kind = "light",
                Active = true,
                Position = light.Position,
                Rotation = Aimed(light.Forward),
                Scale = System.Numerics.Vector3.One,
                Shadows = null,
                Light = new UnityLightInfo
                {
                    Node = null!,
                    Name = light.Name,
                    Type = light.Type,
                    Red = light.Red,
                    Green = light.Green,
                    Blue = light.Blue,
                    Intensity = light.Intensity,
                    Range = light.Range,
                    SpotAngle = light.SpotAngle,
                    InnerSpotAngle = light.InnerSpotAngle,
                    AreaWidth = 0f,
                    AreaHeight = 0f,
                    Shadows = light.Shadows,
                    VolumeFactor = light.VolumeFactor,
                    Disabled = false,
                },
            });
        }
        foreach (PlanNote note in _plan.Notes)
        {
            _statement.Note(_plan.Seed, note.What, note.Count, note.Detail);
        }
        int placed = 0;
        int builtins = 0;
        int proxies = 0;
        foreach (WindowPlacement placement in _plan.Placements)
        {
            if (placement.IsPrefab && placement.Shadows is not null)
            {
                throw new InvalidDataException(
                    $"prefab placement '{placement.AssetPath}' states how it casts shadows; its own renderers do.");
            }
            if (placement.Shadows == ShadowCastingMode.ShadowsOnly && !_options.ShadowProxies)
            {
                proxies++;
                continue;
            }
            string key = placement.IsPrefab
                ? placement.AssetPath
                : placement.AssetPath + "\n" + string.Join("\n", placement.MaterialPaths);
            if (!sources.TryGetValue(key, out WindowSource? source))
            {
                source = BuildWindowSource(placement, Resolve, ref builtins);
                sources[key] = source;
            }
            if (source.Problem == "missing")
            {
                unresolved.Add(placement.AssetPath);
                continue;
            }
            if (source.Problem == "empty")
            {
                empty.Add(placement.AssetPath);
                continue;
            }
            placed++;
            if (source.Pieces is null)
            {
                _statement.Nodes.Add(new StatementNode
                {
                    Index = _statement.Nodes.Count,
                    Parent = -1,
                    Name = source.Name,
                    Path = placement.Name,
                    Kind = "mesh",
                    Active = true,
                    Mesh = source.MeshKey,
                    Materials = source.MaterialKeys,
                    Position = placement.Position,
                    Rotation = placement.Rotation,
                    Scale = placement.Scale,
                    Shadows = placement.Shadows,
                    MainLightShadows = placement.MainLightShadows,
                });
                continue;
            }
            int anchor = _statement.Nodes.Count;
            _statement.Nodes.Add(new StatementNode
            {
                Index = anchor,
                Parent = -1,
                Name = source.Name,
                Path = placement.Name,
                Kind = "empty",
                Active = true,
                Position = placement.Position,
                Rotation = placement.Rotation,
                Scale = placement.Scale,
                Shadows = null,
            });
            foreach (WindowPiece piece in source.Pieces)
            {
                (System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale) =
                    Mat4.Decompose(piece.Local);
                _statement.Nodes.Add(new StatementNode
                {
                    Index = _statement.Nodes.Count,
                    Parent = anchor,
                    Name = piece.Name,
                    Path = piece.Path,
                    Kind = piece.Kind,
                    Active = !piece.Hidden,
                    Mesh = piece.MeshKey,
                    Skeleton = piece.Skeleton,
                    Materials = piece.MaterialKeys,
                    Position = position,
                    Rotation = rotation,
                    Scale = scale,
                    Light = piece.Light,
                    Camera = piece.Camera,
                    Shadows = piece.Shadows,
                });
            }
        }
        _statement.Note(_plan.Seed, "placements stated", _plan.Placements.Count, _plan.Label);
        if (proxies > 0)
        {
            _statement.Note(_plan.Seed, "shadow proxies left out", proxies, _plan.Label);
        }
        _statement.Note(_plan.Seed, "placements placed", placed, _plan.Label);
        if (unresolved.Count > 0)
        {
            _statement.Note(_plan.Seed, "placement paths not in the closure", unresolved.Count, string.Join(Separator, unresolved.Take(8)));
        }
        if (empty.Count > 0)
        {
            _statement.Note(_plan.Seed, "placement assets without geometry", empty.Count, string.Join(Separator, empty.Take(8)));
        }
        if (builtins > 0)
        {
            _statement.Note(_plan.Seed, "engine primitives rebuilt from their definition", builtins, _plan.Label);
        }
    }

    private const string Separator = ";";

    private sealed class WindowPiece
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string Kind { get; init; }
        public required double[] Local { get; init; }
        public required bool Hidden { get; init; }
        public string MeshKey { get; init; } = string.Empty;
        public string Skeleton { get; init; } = string.Empty;
        public IReadOnlyList<string> MaterialKeys { get; init; } = [];
        public UnityLightInfo? Light { get; init; }
        public UnityCameraInfo? Camera { get; init; }
        public ShadowCastingMode? Shadows { get; init; }
    }

    private sealed class WindowSource
    {
        public string Problem { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string MeshKey { get; init; } = string.Empty;
        public IReadOnlyList<string> MaterialKeys { get; init; } = [];
        public List<WindowPiece>? Pieces { get; init; }
    }

    private WindowSource BuildWindowSource(WindowPlacement placement, Func<string, List<IUnityObjectBase>> resolve, ref int builtins)
    {
        List<IUnityObjectBase> assets = resolve(placement.AssetPath);
        string lowered = placement.AssetPath.ToLowerInvariant();
        if (assets.Count == 0 && lowered.Contains(BuiltinMeshMarker, StringComparison.Ordinal))
        {
            string stem = LeafOf(StripExtension(NormalizePath(placement.AssetPath)));
            if (stem == "quad")
            {
                DecodedMesh built = BuiltinMeshes.Build("Quad")!;
                string builtinKey = "builtin:Quad";
                if (!_statement.HasMesh(builtinKey))
                {
                    _statement.Add(new StatementMesh { Key = builtinKey, Name = "Quad", Geometry = built });
                }
                builtins++;
                int hash = placement.AssetPath.IndexOf("##", StringComparison.Ordinal);
                string builtinName = hash >= 0 && hash + 2 < placement.AssetPath.Length ? placement.AssetPath[(hash + 2)..] : "Quad";
                return new WindowSource { Name = builtinName, MeshKey = builtinKey, MaterialKeys = PlacementMaterials(placement, resolve) };
            }
        }
        if (assets.Count == 0)
        {
            return new WindowSource { Problem = "missing" };
        }
        if (placement.IsPrefab)
        {
            IGameObject? root = assets.OfType<IGameObject>().FirstOrDefault(gameObject => gameObject.IsRoot());
            if (root is null)
            {
                return new WindowSource { Problem = "missing" };
            }
            string name = placement.Stem.Length > 0 ? placement.Stem : LeafOf(NormalizePath(placement.AssetPath));
            return new WindowSource { Name = name, Pieces = PrefabPieces(root) };
        }
        int subMark = placement.AssetPath.IndexOf("##", StringComparison.Ordinal);
        string wanted = subMark >= 0 ? placement.AssetPath[(subMark + 2)..] : string.Empty;
        List<IMesh> meshes = assets.OfType<IMesh>().ToList();
        IMesh? mesh = wanted.Length > 0
            ? meshes.FirstOrDefault(candidate => string.Equals(candidate.Name.String, wanted, StringComparison.OrdinalIgnoreCase))
            : meshes.Count == 1 ? meshes[0]
            : meshes.FirstOrDefault(candidate => string.Equals(candidate.Name.String, placement.MeshName, StringComparison.OrdinalIgnoreCase))
              ?? meshes.FirstOrDefault();
        if (mesh is null)
        {
            return new WindowSource { Problem = "missing" };
        }
        if (!_decodedMeshes.TryGetValue(mesh, out DecodedMesh? decoded))
        {
            try
            {
                decoded = UnityMeshDecoder.Decode(mesh);
            }
            catch (Exception exception)
            {
                _statement.Note(_plan.Seed, "mesh could not be decoded", 1, $"{placement.AssetPath}: {exception.Message}");
                return new WindowSource { Problem = "missing" };
            }
            _decodedMeshes[mesh] = decoded;
        }
        if (!decoded.HasGeometry)
        {
            return new WindowSource { Problem = "empty" };
        }
        string key = KeyOf(mesh);
        if (!_statement.HasMesh(key))
        {
            StatementMesh row = new() { Key = key, Name = decoded.Name, Geometry = decoded };
            _statement.Add(row);
            Morphs(row);
        }
        string display = decoded.Name.Length > 0 ? decoded.Name
            : placement.MeshName.Length > 0 ? placement.MeshName : LeafOf(NormalizePath(placement.AssetPath));
        return new WindowSource { Name = display, MeshKey = key, MaterialKeys = PlacementMaterials(placement, resolve) };
    }

    /// <summary>The materials a placement row states, positionally: a slot whose material is
    /// not in the closure stays empty rather than shifting every later slot along.</summary>
    private List<string> PlacementMaterials(WindowPlacement placement, Func<string, List<IUnityObjectBase>> resolve)
    {
        List<string> keys = new(placement.MaterialPaths.Count);
        foreach (string path in placement.MaterialPaths)
        {
            IMaterial? material = path.Length == 0 ? null : resolve(path).OfType<IMaterial>().FirstOrDefault();
            if (material is null)
            {
                if (path.Length > 0)
                {
                    _statement.Note(_plan.Seed, "placement material not in the closure", 1, path);
                }
                keys.Add(string.Empty);
                continue;
            }
            keys.Add(Material(material));
        }
        return keys;
    }

    /// <summary>One prefab as flat pieces: a piece per surviving renderer, at its world
    /// transform within the prefab (identity for a skinned mesh already baked there), plus
    /// its cameras and lights; a piece that drew nothing is carried hidden.</summary>
    private List<WindowPiece> PrefabPieces(IGameObject root)
    {
        List<WindowPiece> pieces = [];
        if (!root.TryGetComponent(out ITransform? rootTransform))
        {
            return pieces;
        }
        UnityHierarchy hierarchy = UnityHierarchy.Build([rootTransform]);
        List<ILODGroup> lodGroups = [];
        List<ISkinnedMeshRenderer> skinned = [];
        List<IMeshRenderer> meshRenderers = [];
        List<ICamera> cameras = [];
        List<ILight> lights = [];
        List<IMonoBehaviour> behaviours = [];
        foreach (UnityNode node in hierarchy.DocumentOrder())
        {
            if (node.GameObject is null)
            {
                continue;
            }
            foreach (IComponent? component in node.GameObject.GetComponentAccessList())
            {
                switch (component)
                {
                    case ILODGroup group: lodGroups.Add(group); break;
                    case ISkinnedMeshRenderer renderer: skinned.Add(renderer); break;
                    case IMeshRenderer renderer: meshRenderers.Add(renderer); break;
                    case ICamera camera: cameras.Add(camera); break;
                    case ILight light: lights.Add(light); break;
                    case IMonoBehaviour behaviour: behaviours.Add(behaviour); break;
                }
            }
        }
        UnityRendererFilters filters = new()
        {
            DetailLevel = _options.Detail,
            ShadowProxies = _options.ShadowProxies,
            Inactive = _options.Inactive,
        };
        UnityRendererSkips skips = new();
        HashSet<IRenderer> discard = UnityRenderers.LodDiscardSet(lodGroups, _options.Detail);
        Dictionary<IRenderer, int> lodOf = LodLevels(lodGroups);
        foreach (UnityRendererInfo info in Filled(UnityRenderers.Renderers(skinned, meshRenderers, hierarchy, filters, skips,
                     (renderer, _) => !discard.Contains(renderer))))
        {
            (DecodedMesh? decoded, string meshKey, string builtin) = ResolveMesh(info);
            if (decoded is null)
            {
                continue;
            }
            if (info.SubMeshWindow is { } window)
            {
                decoded = Windowed(decoded, window.First, window.Count);
                meshKey = string.Create(CultureInfo.InvariantCulture, $"{meshKey}#{window.First}-{window.Count}");
            }
            List<string> materialKeys = info.Materials.Select(material => material is null ? string.Empty : Material(material)).ToList();
            bool baked = false;
            if (info.Skinned)
            {
                List<double[]?> worlds = info.Bones.Select(bone => hierarchy.Of(bone)?.World).ToList();
                DecodedMesh copy = Clone(decoded);
                baked = UnitySkinning.BakeBindPose(copy, worlds);
                if (baked)
                {
                    decoded = copy;
                    meshKey = KeyOf(info.Renderer);
                }
            }
            if (!_statement.HasMesh(meshKey))
            {
                StatementMesh row = new()
                {
                    Key = meshKey,
                    Name = decoded.Name,
                    Geometry = decoded,
                    Lod = lodOf.GetValueOrDefault(info.Renderer, -1),
                    Baked = baked,
                };
                _statement.Add(row);
                Morphs(row);
            }
            pieces.Add(new WindowPiece
            {
                Name = info.Name,
                Path = info.Node?.Path ?? string.Empty,
                Kind = baked ? "skinned" : "mesh",
                Local = baked || info.Node is null ? Mat4.Identity() : info.Node.World,
                Hidden = info.Disabled,
                MeshKey = meshKey,
                MaterialKeys = materialKeys,
                Shadows = info.Renderer.GetShadowCastingMode(),
            });
        }
        foreach (UnityCameraInfo camera in UnityRenderers.Cameras(cameras, behaviours, hierarchy, filters))
        {
            pieces.Add(new WindowPiece
            {
                Name = camera.Name, Path = camera.Node.Path, Kind = "camera", Local = camera.Node.World,
                Hidden = camera.Disabled, Camera = camera,
            });
        }
        foreach (UnityLightInfo light in UnityRenderers.Lights(lights, hierarchy, filters))
        {
            pieces.Add(new WindowPiece
            {
                Name = light.Name, Path = light.Node.Path, Kind = "light", Local = light.Node.World,
                Hidden = light.Disabled, Light = light,
            });
        }
        return pieces;
    }

    private static string NormalizePath(string path)
    {
        string slashed = path.Replace('\\', '/');
        int hashIndex = slashed.IndexOf("##", StringComparison.Ordinal);
        string trimmed = hashIndex >= 0 ? slashed[..hashIndex] : slashed;
        int assetsIndex = trimmed.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
        if (assetsIndex >= 0)
        {
            trimmed = trimmed[assetsIndex..];
        }
        return trimmed.ToLowerInvariant();
    }

    private static string StripExtension(string normalizedPath)
    {
        int dot = normalizedPath.LastIndexOf('.');
        return dot > normalizedPath.LastIndexOf('/') ? normalizedPath[..dot] : normalizedPath;
    }

    private static string LeafOf(string normalizedPath) => normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];

    private static IEnumerable<string> BonePathLeaves(IMonoBehaviour behaviour)
    {
        AssetRipper.Import.Structure.Assembly.Serializable.SerializableStructure? structure =
            behaviour.Structure is AssetRipper.Import.Structure.Assembly.Serializable.UnloadedStructure unloaded
                ? unloaded.LoadStructure()
                : behaviour.Structure as AssetRipper.Import.Structure.Assembly.Serializable.SerializableStructure;
        if (structure is null || !structure.TryGetField("bonePathsStr", out AssetRipper.Import.Structure.Assembly.Serializable.SerializableValue value))
        {
            return [];
        }
        return value.AsStringArray;
    }

    private static void Index<T>(Dictionary<string, List<T>> index, string name, T asset)
    {
        if (!index.TryGetValue(name, out List<T>? list))
        {
            index[name] = list = [];
        }
        list.Add(asset);
    }

    private static string FolderOf(string path)
    {
        string normalized = path.Replace('\\', '/');
        int cut = normalized.LastIndexOf('/');
        return cut < 0 ? string.Empty : normalized[..cut];
    }

    private IEnumerable<IGameObject> Roots()
    {
        HashSet<string> named = new(_plan.NamedRoots, StringComparer.OrdinalIgnoreCase);
        List<IGameObject> roots = [];
        foreach (IUnityObjectBase asset in _gameData.GameBundle.FetchAssets())
        {
            if (asset is not IGameObject gameObject || !gameObject.IsRoot())
            {
                continue;
            }
            if (_plan.SeededOnly && !IsSeedCollection(gameObject.Collection))
            {
                continue;
            }
            if (named.Count > 0 && !named.Contains(gameObject.Name))
            {
                continue;
            }
            roots.Add(gameObject);
        }
        return roots.OrderBy(root => _identities[root.Collection], StringComparer.Ordinal).ThenBy(root => root.PathID);
    }

    private sealed record RootFlattening(UnityHierarchy Hierarchy, StatementSkeleton Skeleton, int RootIndex,
        Dictionary<UnityNode, StatementNode> Rows);

    private RootFlattening? FlattenRoot(IGameObject rootObject)
    {
        if (!rootObject.TryGetComponent(out ITransform? rootTransform))
        {
            _statement.Note(_plan.Seed, "root without transform", 1, rootObject.Name);
            return null;
        }
        UnityHierarchy hierarchy = UnityHierarchy.Build([rootTransform]);
        string rootKey = KeyOf(rootObject);
        List<ILODGroup> lodGroups = [];
        List<ISkinnedMeshRenderer> skinned = [];
        List<IMeshRenderer> meshRenderers = [];
        List<ICamera> cameras = [];
        List<ILight> lights = [];
        List<IMonoBehaviour> behaviours = [];
        IAvatar? avatar = null;
        foreach (UnityNode node in hierarchy.DocumentOrder())
        {
            if (node.GameObject is null)
            {
                continue;
            }
            foreach (IComponent? component in node.GameObject.GetComponentAccessList())
            {
                switch (component)
                {
                    case ILODGroup group: lodGroups.Add(group); break;
                    case ISkinnedMeshRenderer renderer: skinned.Add(renderer); break;
                    case IMeshRenderer renderer: meshRenderers.Add(renderer); break;
                    case ICamera camera: cameras.Add(camera); break;
                    case ILight light: lights.Add(light); break;
                    case IMonoBehaviour behaviour: behaviours.Add(behaviour); break;
                    case IAnimator animator when avatar is null && animator.AvatarP is { } found: avatar = found; break;
                }
            }
        }

        int firstNode = _statement.Nodes.Count;
        Dictionary<UnityNode, StatementNode> rows = new(ReferenceEqualityComparer.Instance);
        foreach (UnityNode node in hierarchy.DocumentOrder())
        {
            StatementNode row = new()
            {
                Index = _statement.Nodes.Count,
                Parent = node.Parent is null ? -1 : rows[node.Parent].Index,
                Name = node.Name,
                Path = node.Path,
                Kind = "empty",
                Active = node.ActiveInHierarchy,
                Position = node.LocalPosition,
                Rotation = node.LocalRotation,
                Scale = node.LocalScale,
                Shadows = null,
            };
            rows[node] = row;
            _statement.Nodes.Add(row);
        }
        _statement.Roots.Add(new StatementRoot(_plan.Seed, firstNode, rootObject.Name,
            _plan.Kind.ToString().ToLowerInvariant()));

        StatementSkeleton skeleton = Skeleton(rootKey, hierarchy, avatar);
        _statement.Skeletons.Add(skeleton);
        foreach (StatementNode row in rows.Values)
        {
            row.Skeleton = skeleton.Key;
        }

        UnityRendererFilters filters = new()
        {
            DetailLevel = _options.Detail,
            ShadowProxies = _options.ShadowProxies,
            Inactive = _options.Inactive,
        };
        UnityRendererSkips skips = new();
        HashSet<IRenderer> discard = UnityRenderers.LodDiscardSet(lodGroups, _options.Detail);
        Dictionary<IRenderer, int> lodOf = LodLevels(lodGroups);
        foreach (UnityRendererInfo info in Filled(UnityRenderers.Renderers(skinned, meshRenderers, hierarchy, filters, skips,
                     AtLevel(discard))))
        {
            FlattenRenderer(info, hierarchy, rows, skeleton, lodOf);
        }
        foreach (UnityCameraInfo camera in UnityRenderers.Cameras(cameras, behaviours, hierarchy, filters))
        {
            StatementNode row = rows[camera.Node];
            if (row.Kind == "empty")
            {
                row.Kind = "camera";
                row.Active = !camera.Disabled;
                row.Camera = camera;
            }
        }
        foreach (UnityLightInfo light in UnityRenderers.Lights(lights, hierarchy, filters))
        {
            StatementNode row = rows[light.Node];
            if (row.Kind == "empty")
            {
                row.Kind = "light";
                row.Active = !light.Disabled;
                row.Light = light;
            }
        }
        if (skips.Lod > 0)
        {
            _statement.Note(_plan.Seed, "renderers below the wanted detail level", skips.Lod, rootObject.Name);
        }
        if (skips.Shadow > 0)
        {
            _statement.Note(_plan.Seed, "shadow-only renderers", skips.Shadow, rootObject.Name);
        }
        if (skips.Inactive > 0)
        {
            _statement.Note(_plan.Seed, "inactive renderers left out", skips.Inactive, rootObject.Name);
        }
        if (skips.InactiveIncluded > 0)
        {
            _statement.Note(_plan.Seed, "inactive renderers included", skips.InactiveIncluded, rootObject.Name);
        }
        return new RootFlattening(hierarchy, skeleton, firstNode, rows);
    }

    /// <summary>Whether a renderer is at the wanted detail level. A title that fills its
    /// renderers at run time states the level of each fill, and a level no fill authors
    /// contributes its nearest one; a renderer no fill mentions is at no level and always kept.
    /// Everyone else is judged by the engine's own LODGroups.</summary>
    private Func<IRenderer, string, bool> AtLevel(HashSet<IRenderer> discard)
    {
        IReadOnlyDictionary<string, RendererFill> fills = Fills;
        HashSet<int> stated = fills.Values.Where(fill => fill.Lod >= 0).Select(fill => fill.Lod).ToHashSet();
        if (stated.Count == 0)
        {
            return (renderer, _) => !discard.Contains(renderer);
        }
        int wanted = stated.OrderBy(level => Math.Abs(level - _options.Detail)).ThenBy(level => level).First();
        return (_, name) => !fills.TryGetValue(name, out RendererFill? fill) || fill.Lod < 0 || fill.Lod == wanted;
    }

    private IReadOnlyDictionary<string, RendererFill>? _fills;

    /// <summary>What the title fills its empty renderers with, asked once of the loaded closure.</summary>
    private IReadOnlyDictionary<string, RendererFill> Fills =>
        _fills ??= _plan.Fills?.Invoke(_gameData) ?? new Dictionary<string, RendererFill>(StringComparer.Ordinal);

    /// <summary>Every renderer as it draws at run time: a fill's mesh replaces the renderer's own,
    /// a fill that states materials or bones replaces the renderer's -- what the title assigns wins --
    /// and what the title writes onto those materials travels with them.</summary>
    private IEnumerable<UnityRendererInfo> Filled(IEnumerable<UnityRendererInfo> renderers)
    {
        IReadOnlyDictionary<string, RendererFill> fills = Fills;
        foreach (UnityRendererInfo info in renderers)
        {
            yield return fills.TryGetValue(info.Name, out RendererFill? fill)
                ? info with
                {
                    Mesh = fill.Mesh,
                    Materials = fill.Materials.Count > 0 ? fill.Materials : info.Materials,
                    Writes = fill.Writes,
                    Bones = fill.Bones ?? info.Bones,
                }
                : info;
        }
    }

    /// <summary>Several prefabs joined into ONE rigged character: the piece marked as the rig
    /// is the skeleton; every other piece is placed at the bone the title names for it, moved
    /// by the title's own correction, its skinning re-pointed onto the rig by bone NAME, the
    /// bones it alone adds grafted at their rest, and its unskinned pieces hung off that bone.</summary>
    private void FlattenAssembly()
    {
        if (_plan.Parts.Count == 0)
        {
            throw new InvalidOperationException($"assembly '{_plan.Label}' states no pieces.");
        }
        Dictionary<string, IGameObject> rootsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (IUnityObjectBase asset in _gameData.GameBundle.FetchAssets()
                     .OrderBy(asset => _identities[asset.Collection], StringComparer.Ordinal).ThenBy(asset => asset.PathID))
        {
            if (asset is IGameObject gameObject && gameObject.IsRoot())
            {
                rootsByName.TryAdd(gameObject.Name, gameObject);
            }
        }
        AssemblyPart rigPart = _plan.Parts.FirstOrDefault(part => part.Rig) ?? _plan.Parts[0];
        if (!rootsByName.TryGetValue(rigPart.Asset, out IGameObject? rigRoot))
        {
            throw new InvalidOperationException(
                $"'{rigPart.Asset}' is not in the resolved closure, so there is no skeleton to build on.");
        }
        RootFlattening rig = FlattenRoot(rigRoot)
            ?? throw new InvalidOperationException($"'{rigPart.Asset}' carries no transform hierarchy to build a skeleton from.");
        _statement.Nodes[rig.RootIndex].Kind = "empty";
        StatementNode rigRow = _statement.Nodes[rig.RootIndex];
        _statement.Nodes[rig.RootIndex] = new StatementNode
        {
            Index = rigRow.Index, Parent = rigRow.Parent, Name = _plan.Label, Path = rigRow.Path, Kind = rigRow.Kind,
            Active = rigRow.Active, Mesh = rigRow.Mesh, Skeleton = rigRow.Skeleton, Materials = rigRow.Materials,
            Position = rigRow.Position, Rotation = rigRow.Rotation, Scale = rigRow.Scale, Light = rigRow.Light, Camera = rigRow.Camera,
            Shadows = rigRow.Shadows, MainLightShadows = rigRow.MainLightShadows,
        };
        _statement.Roots[^1] = _statement.Roots[^1] with { Label = _plan.Label, Kind = "assembly" };

        Dictionary<string, double[]> boneWorld = new(StringComparer.Ordinal);
        Dictionary<string, string> boneIdentity = new(StringComparer.Ordinal);
        Dictionary<string, int> boneIndex = new(StringComparer.Ordinal);
        foreach (UnityNode node in rig.Hierarchy.DocumentOrder())
        {
            boneWorld.TryAdd(node.Name, node.World);
            boneIdentity.TryAdd(node.Name, node.Path);
        }
        foreach (StatementBone bone in rig.Skeleton.Bones)
        {
            boneIndex.TryAdd(bone.Name, bone.Index);
        }
        string firstBone = rig.Skeleton.Bones.Count > 0 ? rig.Skeleton.Bones[0].Name : string.Empty;

        foreach (AssemblyPart part in _plan.Parts)
        {
            if (ReferenceEquals(part, rigPart))
            {
                continue;
            }
            if (!rootsByName.TryGetValue(part.Asset, out IGameObject? partRoot)
                || !partRoot.TryGetComponent(out ITransform? partTransform))
            {
                _statement.Note(_plan.Seed, "assembly piece not in the closure", 1, part.Asset);
                continue;
            }
            string anchor = boneWorld.ContainsKey(part.Anchor) ? part.Anchor : firstBone;
            double[] anchorWorld = anchor.Length > 0 && boneWorld.TryGetValue(anchor, out double[]? found) ? found : Mat4.Identity();
            double[] offset = Mat4.Multiply(anchorWorld, PartCorrection(part));
            FlattenPiece(part, partTransform, offset, anchor, rig, boneWorld, boneIdentity, boneIndex);
        }
    }

    /// <summary>One piece's own correction as a Unity-space matrix: position in metres,
    /// rotation as ZXY euler degrees -- Unity's inspector convention, q = Y·X·Z.</summary>
    private static double[] PartCorrection(AssemblyPart part)
    {
        if (part.Position == System.Numerics.Vector3.Zero && part.RotationDegrees == System.Numerics.Vector3.Zero
            && part.Scale == System.Numerics.Vector3.One)
        {
            return Mat4.Identity();
        }
        System.Numerics.Quaternion x = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, part.RotationDegrees.X * MathF.PI / 180f);
        System.Numerics.Quaternion y = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, part.RotationDegrees.Y * MathF.PI / 180f);
        System.Numerics.Quaternion z = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, part.RotationDegrees.Z * MathF.PI / 180f);
        System.Numerics.Quaternion combined = Hamilton(Hamilton(y, x), z);
        return Mat4.UnityTrs(part.Position, combined, part.Scale);
    }

    private static System.Numerics.Quaternion Hamilton(System.Numerics.Quaternion left, System.Numerics.Quaternion right) => new(
        left.W * right.X + left.X * right.W + left.Y * right.Z - left.Z * right.Y,
        left.W * right.Y - left.X * right.Z + left.Y * right.W + left.Z * right.X,
        left.W * right.Z + left.X * right.Y - left.Y * right.X + left.Z * right.W,
        left.W * right.W - left.X * right.X - left.Y * right.Y - left.Z * right.Z);

    private void FlattenPiece(AssemblyPart part, ITransform partTransform, double[] offset, string anchor,
        RootFlattening rig, Dictionary<string, double[]> boneWorld, Dictionary<string, string> boneIdentity,
        Dictionary<string, int> boneIndex)
    {
        UnityHierarchy hierarchy = UnityHierarchy.Build([partTransform]);
        List<ILODGroup> lodGroups = [];
        List<ISkinnedMeshRenderer> skinned = [];
        List<IMeshRenderer> meshRenderers = [];
        foreach (UnityNode node in hierarchy.DocumentOrder())
        {
            if (node.GameObject is null)
            {
                continue;
            }
            foreach (IComponent? component in node.GameObject.GetComponentAccessList())
            {
                switch (component)
                {
                    case ILODGroup group: lodGroups.Add(group); break;
                    case ISkinnedMeshRenderer renderer: skinned.Add(renderer); break;
                    case IMeshRenderer renderer: meshRenderers.Add(renderer); break;
                }
            }
        }
        UnityRendererFilters filters = new() { DetailLevel = _options.Detail, ShadowProxies = _options.ShadowProxies, Inactive = _options.Inactive };
        UnityRendererSkips skips = new();
        HashSet<IRenderer> discard = UnityRenderers.LodDiscardSet(lodGroups, _options.Detail);
        List<UnityRendererInfo> renderers = Filled(UnityRenderers.Renderers(skinned, meshRenderers, hierarchy, filters, skips, AtLevel(discard))).ToList();

        HashSet<string> weighted = new(StringComparer.Ordinal);
        foreach (UnityRendererInfo info in renderers.Where(info => info.Skinned))
        {
            foreach (ITransform? bone in info.Bones)
            {
                if (hierarchy.Of(bone) is { } node)
                {
                    weighted.Add(node.Name);
                }
            }
        }
        bool SubtreeHasWeight(UnityNode node)
        {
            Stack<UnityNode> stack = new();
            stack.Push(node);
            while (stack.Count > 0)
            {
                UnityNode current = stack.Pop();
                if (weighted.Contains(current.Name))
                {
                    return true;
                }
                foreach (UnityNode child in current.Children)
                {
                    stack.Push(child);
                }
            }
            return false;
        }
        foreach (UnityNode node in hierarchy.StackOrder())
        {
            if (boneWorld.ContainsKey(node.Name) || !SubtreeHasWeight(node))
            {
                continue;
            }
            double[] world = Mat4.Multiply(offset, node.World);
            string parentName = string.Empty;
            for (UnityNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (boneWorld.ContainsKey(ancestor.Name))
                {
                    parentName = ancestor.Name;
                    break;
                }
            }
            double[] local = world;
            if (parentName.Length > 0 && Mat4.Invert(boneWorld[parentName]) is { } inverse)
            {
                local = Mat4.Multiply(inverse, world);
            }
            (System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale) = Mat4.Decompose(local);
            int index = rig.Skeleton.Bones.Count;
            rig.Skeleton.Bones.Add(new StatementBone(index, parentName.Length > 0 ? boneIndex[parentName] : -1, node.Name, node.Path,
                node.Path, position, rotation, scale, string.Empty));
            boneWorld[node.Name] = world;
            boneIdentity[node.Name] = node.Path;
            boneIndex[node.Name] = index;
        }

        double[] offsetNormal = Mat4.NormalMatrix(offset) ?? Mat4.Linear3(offset);
        foreach (UnityRendererInfo info in renderers)
        {
            (DecodedMesh? decoded, string meshKey, string builtin) = ResolveMesh(info);
            if (decoded is null)
            {
                continue;
            }
            if (info.SubMeshWindow is { } window)
            {
                decoded = Windowed(decoded, window.First, window.Count);
                meshKey = string.Create(CultureInfo.InvariantCulture, $"{meshKey}#{window.First}-{window.Count}");
            }
            List<string> materialKeys = info.Materials.Select(material => material is null ? string.Empty : Material(material)).ToList();
            if (info.Skinned)
            {
                DecodedMesh baked = Clone(decoded);
                List<double[]?> worlds = info.Bones.Select(bone => hierarchy.Of(bone)?.World).ToList();
                bool bakedNow = UnitySkinning.BakeBindPose(baked, worlds);
                if (bakedNow)
                {
                    TransformInPlace(baked, offset, offsetNormal);
                }
                List<string> bonePaths = info.Bones.Select(bone =>
                    hierarchy.Of(bone) is { } node && boneIdentity.TryGetValue(node.Name, out string? identity) ? identity : string.Empty).ToList();
                string key = KeyOf(info.Renderer);
                StatementMesh mesh = new()
                {
                    Key = key, Name = decoded.Name, Geometry = baked, BonePaths = bonePaths, Skeleton = rig.Skeleton.Key, Baked = bakedNow,
                };
                _statement.Add(mesh);
                Morphs(mesh);
                _statement.Nodes.Add(new StatementNode
                {
                    Index = _statement.Nodes.Count, Parent = rig.RootIndex, Name = info.Name, Path = info.Node?.Path ?? string.Empty,
                    Kind = bakedNow ? "skinned" : "mesh", Active = !info.Disabled, Mesh = key, Skeleton = rig.Skeleton.Key,
                    Materials = materialKeys, Anchor = bakedNow ? string.Empty : anchor,
                    Position = System.Numerics.Vector3.Zero, Rotation = System.Numerics.Quaternion.Identity, Scale = System.Numerics.Vector3.One,
                    Shadows = info.Renderer.GetShadowCastingMode(),
                });
                continue;
            }
            if (!_statement.HasMesh(meshKey))
            {
                StatementMesh mesh = new() { Key = meshKey, Name = decoded.Name, Geometry = decoded };
                _statement.Add(mesh);
                Morphs(mesh);
            }
            double[] world = info.Node is null ? offset : Mat4.Multiply(offset, info.Node.World);
            (System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale) = Mat4.Decompose(world);
            _statement.Nodes.Add(new StatementNode
            {
                Index = _statement.Nodes.Count, Parent = rig.RootIndex, Name = info.Name, Path = info.Node?.Path ?? string.Empty,
                Kind = "mesh", Active = !info.Disabled, Mesh = meshKey, Materials = materialKeys, Anchor = anchor,
                Position = position, Rotation = rotation, Scale = scale,
                Shadows = info.Renderer.GetShadowCastingMode(),
            });
            if (builtin.Length > 0)
            {
                _statement.Note(_plan.Seed, "engine primitive rebuilt from its definition", 1, $"{info.Name}: {builtin}");
            }
        }
        if (skips.Lod > 0)
        {
            _statement.Note(_plan.Seed, "renderers below the wanted detail level", skips.Lod, part.Asset);
        }
    }

    private static void TransformInPlace(DecodedMesh mesh, double[] matrix, double[] normalMatrix)
    {
        if (mesh.Positions is { } positions)
        {
            for (int index = 0; index + 2 < positions.Length; index += 3)
            {
                Mat4.TransformPoint(matrix, positions[index], positions[index + 1], positions[index + 2], out double x, out double y, out double z);
                positions[index] = (float)x;
                positions[index + 1] = (float)y;
                positions[index + 2] = (float)z;
            }
        }
        if (mesh.Normals is { } normals)
        {
            for (int index = 0; index + 2 < normals.Length; index += 3)
            {
                Mat4.TransformDirection3(normalMatrix, normals[index], normals[index + 1], normals[index + 2], out double x, out double y, out double z);
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (length < 1e-6)
                {
                    length = 1.0;
                }
                normals[index] = (float)(x / length);
                normals[index + 1] = (float)(y / length);
                normals[index + 2] = (float)(z / length);
            }
        }
        if (mesh.Tangents is { } tangents)
        {
            double[] linear = Mat4.Linear3(matrix);
            for (int index = 0; index + 3 < tangents.Length; index += 4)
            {
                Mat4.TransformDirection3(linear, tangents[index], tangents[index + 1], tangents[index + 2], out double x, out double y, out double z);
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (length < 1e-6)
                {
                    length = 1.0;
                }
                tangents[index] = (float)(x / length);
                tangents[index + 1] = (float)(y / length);
                tangents[index + 2] = (float)(z / length);
            }
        }
    }

    private static Dictionary<IRenderer, int> LodLevels(List<ILODGroup> groups)
    {
        Dictionary<IRenderer, int> levels = new(ReferenceEqualityComparer.Instance);
        foreach (ILODGroup group in groups)
        {
            for (int index = 0; index < group.LODs.Count; index++)
            {
                foreach (var entry in group.LODs[index].Renderers)
                {
                    if (entry.Renderer.TryGetAsset(group.Collection) is IRenderer renderer)
                    {
                        levels.TryAdd(renderer, index);
                    }
                }
            }
        }
        return levels;
    }

    private StatementSkeleton Skeleton(string rootKey, UnityHierarchy hierarchy, IAvatar? avatar)
    {
        StatementSkeleton skeleton = new() { Key = rootKey };
        Dictionary<UnityNode, int> indexOf = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, string> humanoid = avatar is null ? [] : HumanoidSlots(avatar, hierarchy);
        foreach (UnityNode node in hierarchy.StackOrder())
        {
            int index = skeleton.Bones.Count;
            indexOf[node] = index;
            skeleton.Bones.Add(new StatementBone(index, node.Parent is null ? -1 : indexOf[node.Parent], node.Name,
                node.Path, node.Path, node.LocalPosition, node.LocalRotation, node.LocalScale,
                humanoid.GetValueOrDefault(node.Path, string.Empty)));
        }
        if (avatar is not null)
        {
            skeleton.AvatarJson = AvatarStatement.ToJson(AvatarRigInput.FromAvatar(avatar));
        }
        return skeleton;
    }

    /// <summary>Node path -> human slot name, off the avatar's human rig: HumanBoneIndex ->
    /// skeleton node -> id -> TOS path, joined to the hierarchy by that path (whole, or as
    /// the suffix a deeper-nested rig leaves it as).</summary>
    private static Dictionary<string, string> HumanoidSlots(IAvatar avatar, UnityHierarchy hierarchy)
    {
        Dictionary<string, string> slots = new(StringComparer.Ordinal);
        AvatarRigInput input = AvatarRigInput.FromAvatar(avatar);
        if (input.HumanBoneIndex.Length == 0)
        {
            return slots;
        }
        Dictionary<string, string> byPath = new(StringComparer.Ordinal);
        Dictionary<uint, string> bySuffix = UnitySkinning.SuffixTable(hierarchy.DocumentOrder().Select(node => node.Path));
        foreach (UnityNode node in hierarchy.DocumentOrder())
        {
            byPath.TryAdd(node.Path, node.Path);
        }

        void Add(int nodeIndex, string slot)
        {
            if ((uint)nodeIndex >= (uint)input.NodeId.Length
                || !input.Tos.TryGetValue(input.NodeId[nodeIndex], out string? tosPath) || tosPath.Length == 0)
            {
                return;
            }
            string? path = byPath.GetValueOrDefault(tosPath) ?? bySuffix.GetValueOrDefault(UnitySkinning.Crc(tosPath));
            if (path is not null)
            {
                slots.TryAdd(path, slot);
            }
        }

        for (int slot = 0; slot < input.HumanBoneIndex.Length; slot++)
        {
            Add(input.HumanBoneIndex[slot], Enum.IsDefined(typeof(AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.Bones.BoneType), slot)
                ? ((AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.Bones.BoneType)slot).ToString()
                : "Body" + slot.ToString(CultureInfo.InvariantCulture));
        }
        foreach ((int[] hand, string side) in new[] { (input.LeftHandBoneIndex, "Left"), (input.RightHandBoneIndex, "Right") })
        {
            foreach (int node in hand)
            {
                Add(node, side + "Finger");
            }
        }
        return slots;
    }

    private void FlattenRenderer(UnityRendererInfo info, UnityHierarchy hierarchy, Dictionary<UnityNode, StatementNode> rows,
        StatementSkeleton skeleton, Dictionary<IRenderer, int> lodOf)
    {
        StatementNode? row = info.Node is null ? null : rows[info.Node];
        string rendererName = info.Name;
        (DecodedMesh? decoded, string meshKey, string builtin) = ResolveMesh(info);
        if (decoded is null)
        {
            return;
        }
        if (info.SubMeshWindow is { } window)
        {
            decoded = Windowed(decoded, window.First, window.Count);
            meshKey = string.Create(CultureInfo.InvariantCulture, $"{meshKey}#{window.First}-{window.Count}");
        }
        List<string> materialKeys = [];
        foreach (IMaterial? material in info.Materials)
        {
            materialKeys.Add(material is null ? string.Empty
                : info.Writes.Count == 0 ? Material(material) : WrittenMaterial(material, info));
        }
        int lod = lodOf.GetValueOrDefault(info.Renderer, -1);
        ShadowCastingMode shadows = info.Renderer.GetShadowCastingMode();

        if (info.Skinned)
        {
            List<double[]?> worlds = new(info.Bones.Count);
            List<string> bonePaths = new(info.Bones.Count);
            foreach (ITransform? bone in info.Bones)
            {
                UnityNode? boneNode = hierarchy.Of(bone);
                worlds.Add(boneNode?.World);
                bonePaths.Add(boneNode?.Path ?? (bone?.GetRootPath() ?? string.Empty));
            }
            DecodedMesh baked = Clone(decoded);
            bool bakedNow = UnitySkinning.BakeBindPose(baked, worlds);
            string key = KeyOf(info.Renderer);
            StatementMesh mesh = new()
            {
                Key = key,
                Name = decoded.Name,
                Geometry = baked,
                BonePaths = bonePaths,
                Skeleton = skeleton.Key,
                Lod = lod,
                Baked = bakedNow,
            };
            _statement.Add(mesh);
            Morphs(mesh);
            if (row is not null)
            {
                row.Kind = bakedNow ? "skinned" : "mesh";
                row.Active = !info.Disabled;
                row.Mesh = key;
                row.Materials = materialKeys;
                row.Shadows = shadows;
            }
            if (!bakedNow)
            {
                _statement.Note(_plan.Seed, "skinned renderer without skin data placed by its node", 1, rendererName);
            }
            if (decoded.VariableBoneCountWeights > 0)
            {
                _statement.Note(_plan.Seed, "influences past the fixed four not decoded", 1, rendererName);
            }
            return;
        }

        if (!_statement.HasMesh(meshKey))
        {
            StatementMesh mesh = new()
            {
                Key = meshKey,
                Name = decoded.Name,
                Geometry = decoded,
                Lod = lod,
            };
            _statement.Add(mesh);
            Morphs(mesh);
        }
        if (builtin.Length > 0)
        {
            _statement.Note(_plan.Seed, "engine primitive rebuilt from its definition", 1, $"{rendererName}: {builtin}");
        }
        if (row is not null)
        {
            row.Kind = "mesh";
            row.Active = !info.Disabled;
            row.Mesh = meshKey;
            row.Materials = materialKeys;
            row.Shadows = shadows;
        }
    }

    private (DecodedMesh? Decoded, string Key, string Builtin) ResolveMesh(UnityRendererInfo info)
    {
        IMesh? mesh = info.Mesh;
        if (mesh is null)
        {
            string? primitive = BuiltinPrimitive(info);
            if (primitive is not null)
            {
                DecodedMesh? built = BuiltinMeshes.Build(primitive);
                if (built is null)
                {
                    _statement.Note(_plan.Seed, "engine primitive not rebuilt here", 1, $"{info.Name}: {primitive}");
                    return (null, string.Empty, string.Empty);
                }
                return (built, "builtin:" + primitive, built.Builtin);
            }
            _statement.Note(_plan.Seed, "renderer without a mesh", 1, info.Name);
            return (null, string.Empty, string.Empty);
        }
        if (!_decodedMeshes.TryGetValue(mesh, out DecodedMesh? decoded))
        {
            try
            {
                decoded = UnityMeshDecoder.Decode(mesh);
            }
            catch (Exception exception)
            {
                _statement.Note(_plan.Seed, "mesh could not be decoded", 1, $"{info.Name}: {exception.Message}");
                return (null, string.Empty, string.Empty);
            }
            _decodedMeshes[mesh] = decoded;
        }
        if (!decoded.HasGeometry)
        {
            _statement.Note(_plan.Seed, "mesh decoded to zero vertices", 1, $"{info.Name}: {decoded.Name}");
            return (null, string.Empty, string.Empty);
        }
        List<int> dropped = decoded.SubMeshes.Where(subMesh => subMesh.Topology != 0).Select(subMesh => subMesh.Topology).Distinct().ToList();
        if (dropped.Count > 0)
        {
            _statement.Note(_plan.Seed, "non-triangle submeshes dropped", dropped.Count, $"{info.Name}: {string.Join(',', dropped)}");
        }
        return (decoded, KeyOf(mesh), string.Empty);
    }

    /// <summary>A renderer whose mesh reference points into the engine's own resources file,
    /// which no extraction contains -- so the reference is read raw and the primitive named.</summary>
    private static string? BuiltinPrimitive(UnityRendererInfo info)
    {
        AssetRipper.Assets.Metadata.PPtr pointer;
        AssetCollection collection = info.Renderer.Collection;
        if (info.Renderer is ISkinnedMeshRenderer skinned)
        {
            pointer = new AssetRipper.Assets.Metadata.PPtr(skinned.Mesh.FileID, skinned.Mesh.PathID);
        }
        else
        {
            IMeshFilter? filter = info.Node?.GameObject?.TryGetComponent<IMeshFilter>();
            if (filter is null)
            {
                return null;
            }
            pointer = new AssetRipper.Assets.Metadata.PPtr(filter.Mesh.FileID, filter.Mesh.PathID);
        }
        if (pointer.FileID <= 0 || pointer.FileID > collection.Dependencies.Count)
        {
            return null;
        }
        AssetCollection? dependency = collection.Dependencies[pointer.FileID - 1];
        if (dependency is not null && !string.Equals(dependency.Name, BuiltinMeshes.ResourcesCollectionName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return BuiltinMeshes.NameOf(pointer.PathID);
    }

    private static DecodedMesh Clone(DecodedMesh source)
    {
        DecodedMesh copy = new(source.Name)
        {
            VertexCount = source.VertexCount,
            Positions = source.Positions?.ToArray(),
            Normals = source.Normals?.ToArray(),
            Tangents = source.Tangents?.ToArray(),
            Colors = source.Colors,
            InfluenceCount = source.InfluenceCount,
            BoneWeights = source.BoneWeights,
            BoneIndices = source.BoneIndices,
            Triangles = source.Triangles,
            TriangleMaterial = source.TriangleMaterial,
            BindPoses = source.BindPoses,
            BoneNameHashes = source.BoneNameHashes,
            VariableBoneCountWeights = source.VariableBoneCountWeights,
            Builtin = source.Builtin,
        };
        foreach ((int layer, float[] uv) in source.Uvs)
        {
            copy.Uvs[layer] = uv;
        }
        copy.SubMeshes.AddRange(source.SubMeshes);
        copy.BlendShapes.AddRange(source.BlendShapes);
        return copy;
    }

    /// <summary>The window a static-batched renderer draws: only the triangles of its own
    /// sub-meshes, with the material slots re-based onto the window's first one.</summary>
    private static DecodedMesh Windowed(DecodedMesh source, int first, int count)
    {
        DecodedMesh copy = Clone(source);
        List<uint> triangles = [];
        List<int> materials = [];
        for (int triangle = 0; triangle < source.TriangleMaterial.Length; triangle++)
        {
            int slot = source.TriangleMaterial[triangle];
            if (slot < first || slot >= first + count)
            {
                continue;
            }
            triangles.Add(source.Triangles[triangle * 3]);
            triangles.Add(source.Triangles[triangle * 3 + 1]);
            triangles.Add(source.Triangles[triangle * 3 + 2]);
            materials.Add(slot - first);
        }
        copy.Triangles = triangles.ToArray();
        copy.TriangleMaterial = materials.ToArray();
        return copy;
    }

    private void Morphs(StatementMesh mesh)
    {
        foreach (DecodedBlendShape shape in mesh.Geometry.BlendShapes)
        {
            if (shape.Frames.Count == 0)
            {
                continue;
            }
            DecodedBlendShapeFrame frame = shape.Frames[0];
            foreach (DecodedBlendShapeFrame candidate in shape.Frames)
            {
                if (candidate.Weight > frame.Weight)
                {
                    frame = candidate;
                }
            }
            _statement.Morphs.Add(new StatementMorph(mesh.Key, shape.Name, frame.VertexIndices, frame.PositionDeltas,
                frame.HasNormals ? frame.NormalDeltas : [], frame.HasTangents ? frame.TangentDeltas : [],
                frame.Weight, shape.Frames.Count));
        }
    }

    private string Material(IMaterial material)
    {
        string key = KeyOf(material);
        return _statement.HasMaterial(key) ? key : Register(key, UnityMaterials.Read(material, TextureKey));
    }

    /// <summary>The material a renderer draws with once the title has written onto it. The engine
    /// gives each renderer its own instance of a material it writes onto, so the written material is
    /// a record of its own, named after the material and the renderer it belongs to; renderers the
    /// title writes the same values onto share one.</summary>
    private string WrittenMaterial(IMaterial material, UnityRendererInfo info)
    {
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{KeyOf(material)}|{UnityMaterials.WriteSignature(info.Writes, TextureKey)}");
        if (_statement.HasMaterial(key))
        {
            return key;
        }
        string stem = string.Create(CultureInfo.InvariantCulture, $"{material.Name.String}@{info.Name}");
        string name = stem;
        for (int ordinal = 2; _writtenNames.TryGetValue(name, out string? taken) && taken != key; ordinal++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{stem}#{ordinal}");
        }
        _writtenNames[name] = key;
        return Register(key, UnityMaterials.Written(UnityMaterials.Read(material, TextureKey), name, info.Writes, TextureKey));
    }

    private string Register(string key, UnityMaterialProperties properties)
    {
        TextureRoles.Resolution roles = _roles.Resolve(properties);
        _statement.Add(new StatementMaterial { Key = key, Properties = properties, Roles = roles });
        foreach (string unmapped in roles.Unmapped)
        {
            _statement.Note(_plan.Seed, "texture property no role layer names", 1, $"{properties.Name}: {unmapped}");
        }
        if (properties.ShaderName.Length == 0)
        {
            _statement.Note(_plan.Seed, "material without a shader", 1, properties.Name);
        }
        return key;
    }

    private string TextureKey(IUnityObjectBase asset)
    {
        ITexture2D texture = (ITexture2D)asset;
        if (!_textureKeys.TryGetValue(texture, out string? key))
        {
            key = KeyOf(texture);
            _textureKeys[texture] = key;
            _texturesToEncode.Add(texture);
        }
        return key;
    }

    private void FlattenLoose()
    {
        foreach (IUnityObjectBase asset in _gameData.GameBundle.FetchAssets())
        {
            if (!IsSeedCollection(asset.Collection))
            {
                continue;
            }
            switch (asset)
            {
                case IMesh mesh:
                {
                    DecodedMesh decoded;
                    try
                    {
                        decoded = UnityMeshDecoder.Decode(mesh);
                    }
                    catch (Exception exception)
                    {
                        _statement.Note(_plan.Seed, "mesh could not be decoded", 1, $"{mesh.Name}: {exception.Message}");
                        continue;
                    }
                    if (!decoded.HasGeometry)
                    {
                        _statement.Note(_plan.Seed, "mesh decoded to zero vertices", 1, decoded.Name);
                        continue;
                    }
                    StatementMesh row = new() { Key = KeyOf(mesh), Name = decoded.Name, Geometry = decoded };
                    _statement.Add(row);
                    Morphs(row);
                    break;
                }
                case IMaterial material:
                    Material(material);
                    break;
                case ITexture2D texture:
                    TextureKey(texture);
                    break;
            }
        }
        _statement.Roots.Add(new StatementRoot(_plan.Seed, -1, _plan.Label, "loose"));
    }

    private void Clips()
    {
        if (_plan.Clips is { } stated)
        {
            foreach ((IUnityObjectBase asset, string label) in stated(_gameData))
            {
                if (asset is not IAnimationClip named)
                {
                    continue;
                }
                (string statedMeta, byte[] statedCurves) = ClipCurveBlob.Build(named);
                _statement.Clips.Add(new StatementClip(KeyOf(named), label, statedMeta, statedCurves,
                    named.Collection.Name));
            }
            return;
        }
        foreach (IUnityObjectBase asset in _gameData.GameBundle.FetchAssets())
        {
            if (asset is not IAnimationClip clip || !IsSeedCollection(asset.Collection))
            {
                continue;
            }
            (string meta, byte[] curves) = ClipCurveBlob.Build(clip);
            _statement.Clips.Add(new StatementClip(KeyOf(clip), clip.Name.String, meta, curves,
                clip.Collection.Name));
        }
    }

    private TimeSpan _encoding;

    private void EncodeTextures()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ImageExportFormat[] accepted = _options.Containers
            .Select(name => Enum.TryParse(name, ignoreCase: true, out ImageExportFormat format) ? format : ImageExportFormat.Png)
            .ToArray();
        StatementTexture?[] encoded = new StatementTexture?[_texturesToEncode.Count];
        _ = TextureEncoding.Ready;
        Parallel.For(0, _texturesToEncode.Count, index =>
        {
            ITexture2D texture = _texturesToEncode[index];
            encoded[index] = TextureEncoding.Encode(texture, _textureKeys[texture], accepted);
        });
        for (int index = 0; index < encoded.Length; index++)
        {
            StatementTexture? row = encoded[index];
            if (row is null)
            {
                _statement.Note(_plan.Seed, "texture could not be decoded", 1, _texturesToEncode[index].Name.String);
                continue;
            }
            _statement.Add(row);
        }
        _encoding = System.Diagnostics.Stopwatch.GetElapsedTime(started);
    }
}
