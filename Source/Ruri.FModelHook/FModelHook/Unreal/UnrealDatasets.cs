using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.TextureDecoder.Rgb.Formats;
using CUE4Parse.UE4.Assets.Exports.Material;
using AssetRipper.Numerics;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.Lights;
using CUE4Parse.UE4.Assets.Exports.Component.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using Ruri.FModelHook.ShaderDecompiler.Semantics;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using Ruri.FModelHook.Unreal.Readers;
using Ruri.RipperHook.Conversion;
using System.Numerics;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.FastGeoStreaming;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.VirtualFileSystem;
using Ruri.RipperHook.Bridge;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Tables;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// What the Unreal decoder publishes for a host to draw: the source options it reads (so the
/// form is the schema, never a hand-kept copy), the mounted session, and its archives.
/// </summary>
public static class UnrealDatasets
{
    public const string IdPrefix = "unreal.";
    public const string SettingsSchemaId = "unreal.settings.schema";
    public const string SessionId = "unreal.session";
    public const string ArchivesId = "unreal.archives";
    public const string WorldsId = "unreal.worlds";
    public const string WorldCellsId = "unreal.world.cells";
    public const string ActorsId = "unreal.actors";
    public const string CharactersId = "unreal.characters";
    public const string CharactersAnimationsId = "unreal.characters.animations";
    public const string KeyParam = "key";
    public const string FilesId = "unreal.files";
    public const string FileId = "unreal.file";
    public const string MatchParam = "match";
    public const string PathParam = "path";
    private const string SkeletalMeshClassName = "SkeletalMesh";
    public const string DataTableId = "unreal.datatable";
    public const string MeshGeometryId = "unreal.mesh.geometry";
    public const string MeshSkeletonId = "unreal.mesh.skeleton";
    public const string PlacementsId = "unreal.placements";
    public const string AnimationsId = "unreal.animations";
    public const string MorphTargetsId = "unreal.morphtargets";
    public const string MaterialsId = "unreal.materials";
    public const string TexturesId = "unreal.textures";
    public const string PackageParam = "package";
    public const string PackagesParam = "packages";
    public const string MaterialParam = "material";
    public const string TextureParam = "texture";
    public const string WorldParam = "world";
    public const string MinXParam = "minX";
    public const string MinYParam = "minY";
    public const string MaxXParam = "maxX";
    public const string MaxYParam = "maxY";
    public const string LevelParam = "level";
    private const char ListSeparator = ';';
    private const string MaterialRow = "m";
    private const string KeywordRow = "k";
    private const string TextureRow = "t";
    private const string ScalarRow = "f";
    private const string VectorRow = "c";
    private const string SpotLight = "spot";
    private const string DirectionalLight = "directional";
    private const string PointLight = "point";
    private const string AreaLight = "area";

    public static void Register()
    {
        Datasets.Publish(SettingsSchemaId, DataRole.Introspection, [],
            "Every source option this decoder reads: name, kind (text|flag|choice|path|entries), default, "
            + "choices ('|'-separated for a choice), what it means, whether the mounted build cannot be read without it "
            + "(the reflection schema, for a build that stores its properties unversioned), and what it is READ WITH right "
            + "now -- a recognised title answers the engine, the keys and the schema by itself, so a form draws those as "
            + "already in effect rather than as blank and waiting. A host draws its form from this.",
            SettingsSchema);

        Datasets.Publish(SessionId, DataRole.Session, [],
            "The mounted Unreal session: project, engine, archive and file counts, whether a property "
            + "schema (.usmap) is loaded, how many archives still wait for a key, how many metres "
            + "one of the engine's own units is, so a host can state a world's size without keeping "
            + "its own copy of that scale, and -- for a build a title row recognised -- that title, the "
            + "build it says this install is, and how many archive keys were fetched from where that "
            + "title publishes them.",
            SessionState);

        Datasets.Publish(ArchivesId, DataRole.Diagnostic, [],
            "Every archive the install ships: path, encryption, whether it mounted, its key guid, file count, "
            + "whether it carries a directory index (one without it cannot be mounted by path at all) and whether a "
            + "key was accepted for it -- the three facts that tell an archive with no key apart from one with the "
            + "wrong key and from one that is simply not addressable by name.",
            Archives);
        Datasets.Publish(ActorsId, DataRole.CharacterRoster, [],
            "Every actor the install ships as a Blueprint class: its package, its name, its kind by the engine's own "
            + "ancestry (Character, Pawn or Actor), the class it extends, the first "
            + "engine class in its ancestry, and -- with a cabmap loaded -- how many skeletal and static mesh packages it "
            + "imports directly. Importing the package places the actor with its components, the way a level would.",
            Actors);
        Datasets.Publish(FilesId, DataRole.VfsFiles, [DataParam.Text(MatchParam)],
            "Every file the mounted archives hold whose path contains the given text, with what it "
            + "weighs. The RAW file list, not the package list a cabmap indexes: a build keeps its "
            + "own design tables and its localized text in containers that are not packages at all, "
            + "and nothing could find them before. No file is opened to answer.",
            Files);
        Datasets.PublishBlob(FileId, DataRole.Payload, [DataParam.Text(PathParam)],
            "One file out of the mounted archives, decrypted, exactly as the build stores it. What "
            + "a studio keeps its design tables and its localized text in is that studio's own "
            + "format, so this hands the bytes over and reads nothing into them.",
            FileBytes);
        Datasets.Publish(CharactersId, DataRole.CharacterModels, [],
            "Every character model this build ships: the packages holding a skeletal mesh under the "
            + "content roots this title states its cast lives on, each named by the package's own name "
            + "and placed by the folder it sits in. Read entirely from the cabmap -- no archive is "
            + "opened and no package is loaded until a row is imported -- so it answers for a build "
            + "that publishes no reflection schema, which is the case an actor listing cannot serve.",
            Characters);
        Datasets.Publish(CharactersAnimationsId, DataRole.Selection, [DataParam.Text(KeyParam)],
            "Where the cast row keyed by 'key' plays its animations: its own animation folder, else the "
            + "shared library of the group it belongs to, else nothing -- the family states no answer of "
            + "its own (an engine build's own convention for filing them is a fact only the title that "
            + "shipped it has), so this is empty until the title that claimed the install states one.",
            CharactersAnimations);
        Datasets.Publish(DataTableId, DataRole.Selection, [DataParam.Text(PackageParam)],
            "The designer-authored data one package holds, as rows: a DataTable gives one row per entry of its "
            + "row map, and a DataAsset (or anything a game derives from one) gives a single row of its own "
            + "properties -- each row named, told which struct or class states it, and carrying one column per "
            + "field as the engine wrote it. A title's own roster, wardrobe or item table is READ here, never "
            + "parsed -- the object carries its own schema, so a game that adds one needs no code and a column "
            + "nobody anticipated still arrives.",
            DataTable);
        Datasets.Publish(WorldsId, DataRole.SceneList, [],
            "Every world the install ships outside a World Partition's generated folder, found by the class the "
            + "cabmap lists for a package rather than by the extension a cook happened to write it under: its "
            + "package, whether its persistent level is partitioned, how many streaming cells a partitioned one "
            + "lists, and the ground those cells cover in Unreal units -- the union of their bounds, zero for a "
            + "world with none.",
            Worlds);
        Datasets.Publish(WorldCellsId, DataRole.PlaceList,
            [DataParam.Text(WorldParam), DataParam.Real(MinXParam, required: false), DataParam.Real(MinYParam, required: false),
                DataParam.Real(MaxXParam, required: false), DataParam.Real(MaxYParam, required: false), DataParam.Integer(LevelParam, required: false)],
            "The streaming cells of one partitioned world: the generated level package each cell's actors live in, "
            + "its runtime grid and hierarchical level, the world bounds of its content in Unreal units, its loading "
            + "range and priority, whether it is always loaded, an HLOD or client-only, its data layers, and whether "
            + "the install carries its package. Stating a window (minX, minY, maxX, maxY in Unreal units) keeps only the cells "
            + "whose bounds cross it, an always-loaded cell belonging to every window; stating a level keeps one hierarchical level.",
            WorldCells);
        Datasets.Publish(MeshGeometryId, DataRole.Internal, [DataParam.Text(PackageParam)],
            "Every LOD of every mesh in one package as raw buffers -- positions, normals, tangents, "
            + "every texture coordinate set, colours, triangle indices, the material sections and -- for "
            + "a skeletal mesh -- four influences a vertex beside the bone names they index. Already in "
            + "the host's basis, beside the object path of the material each slot names. "
            + "The geometry without the conversion: no Unity asset, no export, no text.",
            MeshGeometry);
        Datasets.Publish(MeshSkeletonId, DataRole.Internal, [DataParam.Text(PackageParam)],
            "The reference skeleton of every skeletal mesh in one package, bone by bone in the order "
            + "the meshes' weights index them: name, parent, the local transform it rests at in the "
            + "host's basis, and the path a clip addresses it by.",
            MeshSkeleton);
        Datasets.Publish(PlacementsId, DataRole.Internal, [DataParam.Text(PackageParam)],
            "Every scene component one world or Blueprint actor places, parents before children -- and, for "
            + "a package that IS a mesh and nothing composes, that mesh at rest: its name, the row of the "
            + "component it hangs under (-1 at the top), whether it shows, its own transform in the host's "
            + "basis, the object path of the mesh it renders and of the material each slot draws with, and "
            + "-- for a light -- its kind, colour, intensity and shape. An instanced component places "
            + "nothing itself and states one child row per instance.",
            Placements);
        Datasets.Publish(AnimationsId, DataRole.Internal, [DataParam.Text(PackageParam)],
            "Every animation sequence in one package as curves a host plays: a JSON index beside a "
            + "float32 payload (times, values and both tangents per key), keys reduced to what the "
            + "sequence's own compression tolerance justifies, bone paths in the reference skeleton's "
            + "own naming, coordinates in the host's basis. No AnimationClip asset is created.",
            Animations);
        Datasets.Publish(MorphTargetsId, DataRole.ExpressionCatalog, [DataParam.List(PackagesParam)],
            "Every named morph target the skeletal meshes of the given packages carry -- the "
            + "expression vocabulary a model was built with, as the MESH itself states it. One row "
            + "per (mesh, shape), carrying the shape's index in that mesh's own order.",
            MorphTargets);

        Datasets.Publish(MaterialsId, DataRole.Internal, [DataParam.List(MaterialParam)],
            "The parameter set each named material interface resolves to, the way the engine resolves it "
            + "(the base material's cached defaults, each instance overriding by name, then what the "
            + "compiled base pass proves about the slots). One row per entry, told apart by 'kind': "
            + "'m' the material itself (name, and the base material of its chain under 'texture'), "
            + "'k' an input its graph connects, 't' a texture parameter (the texture's object path under "
            + "'texture'), 'f' a scalar (x), 'c' a vector (x y z w). No Unity Shader, no Material, no text.",
            Materials);
        Datasets.Publish(TexturesId, DataRole.Internal, [DataParam.List(TextureParam)],
            "Each named texture decoded to pixels and handed over in a container a host loads directly: "
            + "its object path, its own name, its size, and whether the asset itself declares sRGB encoding "
            + "or a normal map -- the two facts a host must not guess from a slot or a file name.",
            Textures);
    }

    /// <summary>
    /// Every LOD of every mesh in one package, as the buffers a host writes into its own mesh:
    /// every vertex stream the engine stores (positions, normals, tangents, colours, every
    /// texture coordinate set), the triangle indices, the material sections as int triples
    /// (first index, index count, material slot), and -- for a skeletal mesh -- four influences
    /// a vertex beside the bone names its indices address. Coordinates are already in the
    /// host's basis.
    ///
    /// This is the geometry WITHOUT the detour: the package is read, the LOD decoded and the
    /// buffers handed over. Nothing here creates a Unity asset, runs an export or writes text.
    /// </summary>
    private static ColumnTable MeshGeometry(DataRequest request)
    {
        TableBuilder table = new(MeshGeometryId, "name", "lod#", "vertices#", "positions@", "normals@",
            "tangents@", "colors@", "uv@", "uvSets", "indices@", "sections@", "skin@", "bones", "materials");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string package = PackageKey(provider, request.Text(PackageParam));
        if (!provider.Files.TryGetValue(package, out GameFile? file))
        {
            return table.Build();
        }
        foreach (UObject export in provider.LoadUncached(file).GetExports())
        {
            switch (export)
            {
                case UStaticMesh staticMesh:
                {
                    using StaticMeshDto dto = new(staticMesh, EMeshQuality.All, ENaniteMeshFormat.NoNanite);
                    Rows(table, export.Name, dto, null, null);
                    break;
                }
                case USkeletalMesh skeletalMesh:
                {
                    using SkeletalMeshDto dto = new(skeletalMesh, EMeshQuality.All, ENaniteMeshFormat.NoNanite);
                    Rows(table, export.Name, dto, static vertex => vertex.Influences,
                        UnrealRig.From(skeletalMesh, UnrealBasis.Basis));
                    break;
                }
            }
        }
        return table.Build();
    }

    /// <summary>Every LOD of one mesh, decoded through the one reading both lanes share.</summary>
    private static void Rows<TVertex>(TableBuilder table, string name, MeshDto<TVertex> dto,
        Func<TVertex, MeshBoneInfluenceDto[]>? influences, UnrealRig? rig)
        where TVertex : struct, IMeshVertex
    {
        string[]? boneNames = rig?.Names;
        string? rootBone = boneNames is { Length: > 0 } ? boneNames[0] : null;
        string materials = string.Join(ListSeparator, dto.Materials.Select(static slot =>
            slot.Material is { IsNull: false } pointer ? pointer.ResolvedObject?.GetPathName() ?? string.Empty : string.Empty));
        foreach (MeshLodDto<TVertex> lod in dto.LODs)
        {
            MeshGeometry geometry = UnrealMeshGeometry.FromLod(name, dto, lod, UnrealBasis.Basis,
                influences, rig?.BindPoses, boneNames, rootBone);
            int[] sections = new int[geometry.Sections.Length * 3];
            for (int index = 0; index < geometry.Sections.Length; index++)
            {
                MeshSection section = geometry.Sections[index];
                sections[index * 3] = section.FirstIndex;
                sections[index * 3 + 1] = section.IndexCount;
                sections[index * 3 + 2] = section.MaterialIndex;
            }
            // Texture coordinate sets are sparse -- a set whose length disagreed with the vertex
            // count is left out -- so the sets present are named beside the buffer that holds
            // them, and nothing has to infer a set index from a position in the buffer.
            List<int> uvSets = new();
            List<Vector2> uvValues = new();
            for (int set = 0; set < geometry.TexCoords.Length; set++)
            {
                if (geometry.TexCoords[set] is { } values)
                {
                    uvSets.Add(set);
                    uvValues.AddRange(values);
                }
            }
            table.Row(name, (int)lod.SourceLodIndex, geometry.Positions.Length,
                Bytes<Vector3>(geometry.Positions),
                Bytes<Vector3>(geometry.Normals),
                Bytes<Vector4>(geometry.Tangents),
                Bytes<Vector4>(geometry.Colors),
                Bytes<Vector2>(uvValues.ToArray()),
                string.Join(ListSeparator, uvSets),
                Bytes<uint>(geometry.Indices),
                Bytes<int>(sections),
                Bytes<BoneWeight4>(geometry.Skin),
                boneNames is null ? string.Empty : string.Join(ListSeparator, boneNames),
                materials);
        }
    }

    /// <summary>
    /// The reference skeleton of every skeletal mesh in one package, bone by bone in the order
    /// the meshes' weights index them: the bone's own name, its parent, the local transform it
    /// rests at in the host's basis, and the path a clip addresses it by. An armature is built
    /// from this alone -- no Unity rig prefab is created and none is needed.
    /// </summary>
    private static ColumnTable MeshSkeleton(DataRequest request)
    {
        TableBuilder table = new(MeshSkeletonId, "mesh", "bone", "parent#", "px#", "py#", "pz#",
            "qx#", "qy#", "qz#", "qw#", "sx#", "sy#", "sz#", "path");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string package = PackageKey(provider, request.Text(PackageParam));
        if (!provider.Files.TryGetValue(package, out GameFile? file))
        {
            return table.Build();
        }
        foreach (UObject export in provider.LoadUncached(file).GetExports())
        {
            if (export is not USkeletalMesh skeletalMesh)
            {
                continue;
            }
            foreach (UnrealRig.Bone bone in UnrealRig.From(skeletalMesh, UnrealBasis.Basis).Bones)
            {
                table.Row(export.Name, bone.Name, bone.ParentIndex,
                    bone.Position.X, bone.Position.Y, bone.Position.Z,
                    bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W,
                    bone.Scale.X, bone.Scale.Y, bone.Scale.Z, bone.Path);
            }
        }
        return table.Build();
    }

    /// <summary>
    /// Every animation sequence in one package as the curves a host plays: the sequence sampled
    /// (<see cref="UnrealClip"/>), reduced to the keys its own compression tolerance justifies
    /// (<see cref="ClipBuilder.Reduce"/>), and packed as the clip blob -- one JSON index beside
    /// one float32 payload, which the host reads with the reader every other clip goes through.
    /// No AnimationClip asset is created; the sequences are read and the curves handed over.
    /// </summary>
    private static ColumnTable Animations(DataRequest request)
    {
        TableBuilder table = new(AnimationsId, "name", "meta", "curves@", "frames#", "rate#");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string package = PackageKey(provider, request.Text(PackageParam));
        if (!provider.Files.TryGetValue(package, out GameFile? file))
        {
            return table.Build();
        }
        foreach (UObject export in provider.LoadPackage(file).GetExports())
        {
            if (export is not UAnimSequence source
                || UnrealClip.Read(source, UnrealBasis.Basis, package) is not { } sampled)
            {
                continue;
            }
            List<ClipBuilder.ReducedChannel> channels =
                ClipBuilder.Reduce(sampled.SampleRate, sampled.FrameCount, sampled.Tracks, [], sampled.Tolerance);
            (string meta, byte[] curves) = ClipCurveBlob.Build(export.Name, sampled.SampleRate, sampled.FrameCount, channels);
            table.Row(export.Name, meta, curves, sampled.FrameCount, sampled.SampleRate);
        }
        return table.Build();
    }

    /// <summary>
    /// Every scene component a world places, parents before children: what it is called, the
    /// component it hangs under, whether it shows, its own transform in the host's basis, the
    /// mesh it renders with the material each slot draws with, and -- for a light -- its kind,
    /// colour and shape. An instanced component places nothing itself and states one child row
    /// per instance, exactly as the engine draws it.
    ///
    /// A package that IS a mesh places that mesh, at rest, with its own slot materials: nothing
    /// composes it, and a title whose characters are assembled at runtime out of a body, a face
    /// and a wardrobe ships those meshes and no actor that carries them.
    /// </summary>
    private static ColumnTable Placements(DataRequest request)
    {
        TableBuilder table = new(PlacementsId, "name", "parent#", "active",
            "px#", "py#", "pz#", "qx#", "qy#", "qz#", "qw#", "sx#", "sy#", "sz#",
            "mesh", "skinned", "materials",
            "light", "lr#", "lg#", "lb#", "intensity#", "range#", "outer#", "inner#", "width#", "height#");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string package = PackageKey(provider, request.Text(PackageParam));
        if (!provider.Files.TryGetValue(package, out GameFile? file))
        {
            return table.Build();
        }
        SourceBasis basis = UnrealBasis.Basis;
        List<UObject> exports = provider.LoadPackage(file).GetExports().ToList();
        bool composed = false;
        foreach (UObject export in exports)
        {
            UnrealSceneGraph.Collector collector = new();
            switch (export)
            {
                case UWorld world:
                    UnrealSceneGraph.Collect(collector, world, package);
                    break;
                case UBlueprintGeneratedClass actorClass
                    when UnrealBlueprint.IsActorClass(actorClass, provider.MappingsForGame):
                    UnrealBlueprint.Collect(collector, actorClass);
                    break;
                case USkeletalMesh or UStaticMesh:
                    Mesh(table, basis, export);
                    composed = true;
                    continue;
                case UFastGeoContainer container:
                    Geometry(table, basis, container);
                    composed = true;
                    continue;
                default:
                    continue;
            }
            composed = true;
            List<UnrealSceneGraph.Placed> ordered = collector.Ordered();
            List<(int Parent, string Name, FTransform Transform, string Mesh, string Materials)> instances = new();
            for (int index = 0; index < ordered.Count; index++)
            {
                UnrealSceneGraph.Placed placed = ordered[index];
                Placement(table, basis, placed.Name, placed.Parent, placed.Active,
                    placed.Component.GetRelativeTransform(), placed.Component, index, instances);
            }
            foreach ((int parent, string name, FTransform transform, string mesh, string materials) in instances)
            {
                Row(table, basis, name, parent, true, transform, mesh, false, materials,
                    string.Empty, default, 0f, 0f, 0f, 0f, 0f, 0f);
            }
        }
        if (!composed)
        {
            Named(table, basis, exports);
        }
        return table.Build();
    }

    /// <summary>One mesh at rest: the package IS the model and nothing composes it.</summary>
    private static void Mesh(TableBuilder table, SourceBasis basis, UObject export) =>
        Row(table, basis, export.Name, -1, true, FTransform.Identity,
            export.GetPathName(), export is USkeletalMesh,
            string.Join(ListSeparator, UnrealComponents.MaterialPaths(export, [])),
            string.Empty, default, 0f, 0f, 0f, 0f, 0f, 0f);

    /// <summary>
    /// The meshes a package of pure DATA names, for a package that composes nothing.
    ///
    /// A studio that assembles a character out of parts at runtime ships neither a world nor a
    /// blueprint for it: it ships a data asset whose properties name the skeletal meshes the
    /// parts are, and the game puts them on one skeleton. Nothing here knows those properties'
    /// names -- every property that POINTS AT a mesh is one, which is the only thing that has to
    /// be true for the model to come out whole. Reached only when the package placed nothing by
    /// itself, so a world or an actor that merely references a mesh is untouched.
    /// </summary>
    private static void Named(TableBuilder table, SourceBasis basis, List<UObject> exports)
    {
        HashSet<string> placed = new(StringComparer.Ordinal);
        foreach (UObject export in exports)
        {
            foreach (FPropertyTag property in export.Properties)
            {
                if (property.Tag?.GenericValue is not FPackageIndex pointer || pointer.IsNull)
                {
                    continue;
                }
                UObject? named = pointer.Load();
                if (named is not (USkeletalMesh or UStaticMesh) || !placed.Add(named.GetPathName()))
                {
                    continue;
                }
                Mesh(table, basis, named);
            }
        }
    }

    /// <summary>One component's row: what it renders, decided by what kind of component it is.</summary>
    private static void Placement(TableBuilder table, SourceBasis basis, string name, int parent, bool active,
        FTransform transform, USceneComponent component, int index,
        List<(int Parent, string Name, FTransform Transform, string Mesh, string Materials)> instances)
    {
        switch (component)
        {
            case UInstancedStaticMeshComponent instanced:
            {
                (string mesh, string materials) = Mesh(instanced.GetStaticMesh(), instanced.OverrideMaterials);
                FInstancedStaticMeshInstanceData[] placed = instanced.GetInstances();
                for (int slot = 0; slot < placed.Length; slot++)
                {
                    instances.Add((index, $"{name}_{slot}", placed[slot].TransformData, mesh, materials));
                }
                Row(table, basis, name, parent, active, transform, string.Empty, false, string.Empty,
                    string.Empty, default, 0f, 0f, 0f, 0f, 0f, 0f);
                break;
            }
            case UStaticMeshComponent staticMesh:
            {
                (string mesh, string materials) = Mesh(staticMesh.GetStaticMesh(), staticMesh.OverrideMaterials);
                Row(table, basis, name, parent, active, transform, mesh, false, materials,
                    string.Empty, default, 0f, 0f, 0f, 0f, 0f, 0f);
                break;
            }
            case USkinnedMeshComponent skinned:
            {
                (string mesh, string materials) = Mesh(skinned.GetSkeletalMesh(), skinned.OverrideMaterials);
                Row(table, basis, name, parent, active, transform, mesh, true, materials,
                    string.Empty, default, 0f, 0f, 0f, 0f, 0f, 0f);
                break;
            }
            case ULightComponentBase light:
            {
                float unitScale = basis.UnitScale;
                (string kind, float range, float outer, float inner, float width, float height) = light switch
                {
                    USpotLightComponent spot => (SpotLight, spot.AttenuationRadius * unitScale,
                        spot.OuterConeAngle * 2f, spot.InnerConeAngle * 2f, 0f, 0f),
                    URectLightComponent rect => (AreaLight, rect.AttenuationRadius * unitScale, 0f, 0f,
                        rect.SourceWidth * unitScale, rect.SourceHeight * unitScale),
                    UPointLightComponent point => (PointLight, point.AttenuationRadius * unitScale, 0f, 0f, 0f, 0f),
                    UDirectionalLightComponent => (DirectionalLight, 0f, 0f, 0f, 0f, 0f),
                    _ => (string.Empty, 0f, 0f, 0f, 0f, 0f),
                };
                Row(table, basis, name, parent, active, transform, string.Empty, false, string.Empty,
                    kind, light.GetLightColor(), light.Intensity, range, outer, inner, width, height);
                break;
            }
            default:
                Row(table, basis, name, parent, active, transform, string.Empty, false, string.Empty,
                    string.Empty, default, 0f, 0f, 0f, 0f, 0f, 0f);
                break;
        }
    }

    /// <summary>The mesh a component points at and the material every slot of it draws with.</summary>
    /// <summary>
    /// Every mesh a geometry container places.
    ///
    /// A cook that streams its static geometry as scene proxies rather than as actors writes a
    /// cell's meshes into a container instead of a level: there is no actor, no component tree
    /// and no attachment, only primitives already in world space. So each one is a row that
    /// stands on its own, and a level whose content is a container places exactly what the
    /// container holds rather than nothing at all.
    /// </summary>
    private static void Geometry(TableBuilder table, SourceBasis basis, UFastGeoContainer container)
    {
        foreach (FFastGeoComponentCluster cluster in container.ComponentClusters)
        {
            foreach (FFastGeoStaticMeshComponent component in cluster.StaticMeshComponents)
            {
                if (component.SceneProxyDesc.StaticMeshSceneProxyDesc is not { } described)
                {
                    continue;
                }
                (string mesh, string materials) = Mesh(described.StaticMesh, component.OverrideMaterials);
                Row(table, basis, $"{cluster.Name}_{component.ComponentIndex}", -1, component.bVisible,
                    component.WorldTransform, mesh, false, materials,
                    string.Empty, default, 0f, 0f, 0f, 0f, 0f, 0f);
            }
        }
    }

    private static (string Mesh, string Materials) Mesh(FPackageIndex pointer, FPackageIndex?[] overrides)
    {
        if (pointer.IsNull)
        {
            return (string.Empty, string.Empty);
        }
        string path = pointer.ResolvedObject?.GetPathName() ?? string.Empty;
        return pointer.Load() is { } source
            ? (path, string.Join(ListSeparator, UnrealComponents.MaterialPaths(source, overrides)))
            : (path, string.Empty);
    }

    private static void Row(TableBuilder table, SourceBasis basis, string name, int parent, bool active,
        FTransform transform, string mesh, bool skinned, string materials,
        string light, FLinearColor color, float intensity, float range, float outer, float inner,
        float width, float height)
    {
        (Vector3 position, Quaternion rotation, Vector3 scale) = UnrealComponents.Transform(basis, transform);
        table.Row(name, parent, active ? "1" : "0",
            position.X, position.Y, position.Z,
            rotation.X, rotation.Y, rotation.Z, rotation.W,
            scale.X, scale.Y, scale.Z,
            mesh, skinned ? "1" : "0", materials,
            light, color.R, color.G, color.B, intensity, range, outer, inner, width, height);
    }

    /// <summary>
    /// Every named material interface's resolved parameters, flattened to one row per entry.
    /// The resolution is <see cref="UnrealMaterial.Resolve"/> -- the same reading the Unity
    /// conversion runs -- so a host reading this and a project exported from the same mount
    /// cannot disagree about a material.
    /// </summary>
    private static ColumnTable Materials(DataRequest request)
    {
        TableBuilder table = new(MaterialsId, "material", "kind", "name", "texture", "x#", "y#", "z#", "w#");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        MaterialSemanticsResolver? resolver =
            UnrealSourceOptions.Flag(UnrealSourceOptions.MaterialSemantics) ? provider.Semantics : null;
        foreach (string path in Named(request.List(MaterialParam)))
        {
            if (Load<UMaterialInterface>(provider, path) is not { } source)
            {
                continue;
            }
            List<UMaterialInterface> chain = UnrealMaterial.Chain(source);
            UnrealMaterialParameters parameters = UnrealMaterial.Resolve(provider, resolver, source, chain);
            parameters.StateSurfaceMode();
            table.Row(path, MaterialRow, source.Name, chain[0].GetPathName(), 0d, 0d, 0d, 0d);
            foreach (string keyword in parameters.Keywords)
            {
                table.Row(path, KeywordRow, keyword, string.Empty, 0d, 0d, 0d, 0d);
            }
            foreach ((string name, string? texture) in parameters.Textures)
            {
                table.Row(path, TextureRow, name, texture ?? string.Empty, 0d, 0d, 0d, 0d);
            }
            foreach ((string name, float value) in parameters.Floats)
            {
                table.Row(path, ScalarRow, name, string.Empty, value, 0d, 0d, 0d);
            }
            foreach ((string name, Vector4 color) in parameters.Colors)
            {
                table.Row(path, VectorRow, name, string.Empty, color.X, color.Y, color.Z, color.W);
            }
        }
        return table.Build();
    }

    /// <summary>
    /// Every named texture as pixels in a container the host loads: the package is read on this
    /// thread (one archive, one stream -- reading it from several gains nothing and interleaves),
    /// and the decode and encode, which are computation over a buffer, run on every core.
    /// </summary>
    private static ColumnTable Textures(DataRequest request)
    {
        TableBuilder table = new(TexturesId, "texture", "name", "width#", "height#", "srgb", "normal", "image@");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        ETexturePlatform platform = UnrealSourceOptions.TexturePlatformChoice();
        List<(string Path, UTexture Source)> loaded = new();
        foreach (string path in Named(request.List(TextureParam)))
        {
            if (Load<UTexture>(provider, path) is { } source)
            {
                loaded.Add((path, source));
            }
        }
        (int Width, int Height, byte[] Image)[] images = new (int, int, byte[])[loaded.Count];
        WarmEncoder();
        Parallel.For(0, loaded.Count, index => images[index] = Image(loaded[index].Path, loaded[index].Source, platform));
        for (int index = 0; index < loaded.Count; index++)
        {
            (string path, UTexture source) = loaded[index];
            (int width, int height, byte[] image) = images[index];
            table.Row(path, source.Name, width, height, source.SRGB ? "1" : "0", source.IsNormalMap ? "1" : "0", image);
        }
        return table.Build();
    }

    /// <summary>
    /// One texture's pixels in a PNG, through the same encoder every exported texture goes
    /// through. A layout the decoder answers with that has no matching colour type is reported
    /// and yields no image, never a silently reinterpreted one.
    /// </summary>
    private static (int Width, int Height, byte[] Image) Image(string path, UTexture source, ETexturePlatform platform)
    {
        CTexture? decoded;
        try
        {
            decoded = source.Decode(platform);
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {path} did not decode: {exception.GetType().Name}: {exception.Message}");
            return (0, 0, []);
        }
        if (decoded is null)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {path} has no decodable mip.");
            return (0, 0, []);
        }
        DirectBitmap? bitmap = decoded.PixelFormat switch
        {
            EPixelFormat.PF_B8G8R8A8 => Bitmap<ColorBGRA<byte>, byte>(decoded),
            EPixelFormat.PF_R8G8B8A8 => Bitmap<ColorRGBA<byte>, byte>(decoded),
            EPixelFormat.PF_A8R8G8B8 => Bitmap<ColorARGB<byte>, byte>(decoded),
            EPixelFormat.PF_G8 => Bitmap<ColorR<byte>, byte>(decoded),
            EPixelFormat.PF_R8G8 => Bitmap<ColorRG<byte>, byte>(decoded),
            EPixelFormat.PF_G16 => Bitmap<ColorR<ushort>, ushort>(decoded),
            EPixelFormat.PF_R16F => Bitmap<ColorR<Half>, Half>(decoded),
            EPixelFormat.PF_G16R16F => Bitmap<ColorRG<Half>, Half>(decoded),
            EPixelFormat.PF_FloatRGBA => Bitmap<ColorRGBA<Half>, Half>(decoded),
            EPixelFormat.PF_R32_FLOAT => Bitmap<ColorR<float>, float>(decoded),
            EPixelFormat.PF_G32R32F => Bitmap<ColorRG<float>, float>(decoded),
            EPixelFormat.PF_A32B32G32R32F => Bitmap<ColorRGBA<float>, float>(decoded),
            _ => null,
        };
        if (bitmap is null)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {path} decodes to {decoded.PixelFormat}, which no colour type here states.");
            return (0, 0, []);
        }
        using MemoryStream stream = new();
        bitmap.SaveAsPng(stream);
        return (decoded.Width, decoded.Height, stream.ToArray());
    }

    /// <summary>
    /// Run the encoder's static initialisers on this thread, once for the process.
    /// They are not safe to enter from several threads at once: fpng registers each of its
    /// globals in a plain dictionary keyed by a plain counter, and two initialisers racing it
    /// throw out of a type initializer -- which the runtime caches and rethrows for every
    /// later encode, so the whole process loses PNG encoding over a first-use race.
    /// </summary>
    private static void WarmEncoder() => Warmed.Value.GetType();

    private static readonly Lazy<object> Warmed = new(static () =>
    {
        using MemoryStream stream = new();
        new DirectBitmap<ColorBGRA<byte>, byte>(1, 1, 1).SaveAsPng(stream);
        return stream;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static DirectBitmap? Bitmap<TColor, TChannel>(CTexture decoded)
        where TChannel : unmanaged
        where TColor : unmanaged, AssetRipper.TextureDecoder.Rgb.IColor<TChannel>
    {
        int size = decoded.Width * decoded.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TColor>();
        byte[] data = decoded.Data;
        if (data.Length < size)
        {
            return null;
        }
        return new DirectBitmap<TColor, TChannel>(decoded.Width, decoded.Height, 1, data.Length == size ? data : data[..size]);
    }

    /// <summary>The named objects, each asked for once, in the order they were named.</summary>
    private static IEnumerable<string> Named(string[] paths)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (path.Length > 0 && seen.Add(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>The object one path names, or null with a line saying why -- a path a mount does not carry is data, not a fault.</summary>
    private static T? Load<T>(UnrealFileProvider provider, string path) where T : UObject
    {
        try
        {
            return provider.LoadPackageObject<T>(path);
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {path} did not load: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// The mount's key for a package, from either spelling: the path the mount indexes it by
    /// ("Project/Content/A/B.uasset") or the path the engine's own references use, whose leaf
    /// names the object inside it ("/Game/A/B.B"). The object name is dropped -- a suffix that
    /// is not a package extension is one -- and FixPath does the root and the extension.
    ///
    /// FixPath alone is not enough: it reads the leaf BEFORE trimming an object name off, so it
    /// never appends the extension it just removed and answers a key no mount holds.
    /// </summary>
    /// <summary>
    /// Every named morph target the skeletal meshes of a selection carry.
    ///
    /// The engine's own answer to "what expressions was this built with", asked of the same
    /// packages an import of that row would read. A name is read off the mesh's own morph list
    /// without loading the target: the deltas are a separate question, asked when a face is
    /// actually driven.
    /// </summary>
    private static ColumnTable MorphTargets(DataRequest request)
    {
        TableBuilder table = new(MorphTargetsId,
            "name|Expression", "mesh|Mesh", "index#|Index", "cab|Package", "key|Id");
        table.Role(ColumnRole.Label, "name")
            .Role(ColumnRole.Facet | ColumnRole.Group, "mesh")
            .Role(ColumnRole.Key | ColumnRole.Payload, "key");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        foreach (string stated in request.List(PackagesParam))
        {
            string package = PackageKey(provider, stated);
            if (!provider.Files.TryGetValue(package, out GameFile? file))
            {
                continue;
            }
            foreach (UObject export in provider.LoadUncached(file).GetExports())
            {
                if (export is not USkeletalMesh skeletalMesh)
                {
                    continue;
                }
                for (int index = 0; index < skeletalMesh.MorphTargets.Length; index++)
                {
                    FPackageIndex shape = skeletalMesh.MorphTargets[index];
                    if (shape.IsNull)
                    {
                        continue;
                    }
                    table.Row(shape.Name, export.Name, index, package,
                        package + "|" + export.Name + "|" + index.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        return table.Build();
    }

    private static string PackageKey(UnrealFileProvider provider, string path) =>
        UnrealDataTables.Key(provider, path);

    private static byte[] Bytes<T>(T[]? values) where T : unmanaged =>
        values is null || values.Length == 0 ? [] : System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    /// <summary>
    /// The designer-authored data one package holds, as rows. A DataTable contributes one row per
    /// entry of its row map, named by the row struct the engine wrote it under; every other object
    /// no asset family claims -- a DataAsset and whatever a game derives from one -- contributes a
    /// single row of its own properties, named by its class. The columns are the union of the
    /// fields the rows actually carry, in the order the engine wrote them, so nothing here knows
    /// what any particular table is about.
    /// </summary>
    private static ColumnTable DataTable(DataRequest request)
    {
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string package = PackageKey(provider, request.Text(PackageParam));
        List<string> columns = new();
        HashSet<string> declared = new(StringComparer.Ordinal);
        List<UnrealDataTables.Row> rows = UnrealDataTables.Rows(provider, package);
        foreach (UnrealDataTables.Row row in rows)
        {
            foreach (string field in row.Values.Keys)
            {
                if (declared.Add(field))
                {
                    columns.Add(field);
                }
            }
        }
        string[] names = new string[columns.Count + 3];
        names[0] = "table";
        names[1] = "row";
        names[2] = "struct";
        columns.CopyTo(names, 3);
        TableBuilder builder = new(DataTableId, names);
        object[] cells = new object[names.Length];
        foreach (UnrealDataTables.Row row in rows)
        {
            cells[0] = row.Table;
            cells[1] = row.Name;
            cells[2] = row.Struct;
            for (int index = 0; index < columns.Count; index++)
            {
                cells[index + 3] = row.Values.TryGetValue(columns[index], out string? value) ? value : string.Empty;
            }
            builder.Row(cells);
        }
        return builder.Build();
    }

    /// <summary>
    /// The raw files the mount holds, narrowed by the text a caller is looking for. Reading the
    /// provider's own index only -- nothing is opened, nothing is decrypted -- so it costs the
    /// same whether a build ships a thousand files or two million.
    /// </summary>
    private static ColumnTable Files(DataRequest request)
    {
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string match = request.Text(MatchParam);
        TableBuilder table = new(FilesId, "path", "name", "extension", "size#");
        foreach (GameFile file in provider.Files.Values)
        {
            if (match.Length > 0 && !file.Path.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            table.Row(file.Path, file.NameWithoutExtension, file.Extension, file.Size);
        }
        return table.Build();
    }

    /// <summary>One mounted file's bytes, as stored -- decryption is the mount's, nothing else is done to them.</summary>
    private static byte[] FileBytes(DataRequest request)
    {
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string path = request.Text(PathParam);
        if (!provider.TrySaveAsset(path, out byte[]? payload))
        {
            throw new FileNotFoundException($"[Unreal] the mount holds no file '{path}'.", path);
        }
        return payload;
    }

    /// <summary>
    /// What a cabmap row says a skeletal mesh package lists as, asked of the one families table
    /// that decides it. A Blueprint class lists as everything it MIGHT compose, skinned renderer
    /// included, so "carries a skinned renderer" alone answers yes for every ability graph a
    /// build ships; the whole set a skeletal mesh produces does not.
    /// </summary>
    private static readonly int[] SkeletalMeshClassIds =
        UnrealClasses.Of(SkeletalMeshClassName, null).Select(static id => (int)id).ToArray();

    /// <summary>
    /// The build's cast, read off the cabmap alone: every package under a root the title states
    /// that the map lists a skeletal mesh in. Nothing here knows a character's name -- a row IS
    /// a package the map already carries, so a title that ships a new cast member ships a row.
    /// </summary>
    private static ColumnTable Characters(DataRequest request)
    {
        TableBuilder table = new(CharactersId, "name", "package", "folder");
        table.Role(ColumnRole.Label, "name")
            .Role(ColumnRole.Key, "name")
            .Role(ColumnRole.Shipped, "package")
            .Role(ColumnRole.Payload, "package");
        if (!request.HasMap)
        {
            throw new InvalidOperationException(
                $"dataset '{CharactersId}' reads the cabmap; build or load one for this install first.");
        }
        UnrealTitle? title = UnrealTitles.For(request.GameRoot);
        string[] roots = title?.CharacterRoots ?? [];
        if (roots.Length == 0)
        {
            Logger.Warning(LogCategory.Import,
                $"[Unreal] {(title is null ? "This install" : title.Product)} states no character roots, "
                + "so there is no shelf to list a cast from.");
            return table.Build();
        }
        CabTable map = request.Map;
        for (int id = 0; id < map.Count; id++)
        {
            if (!Holds(map, id, SkeletalMeshClassIds))
            {
                continue;
            }
            for (int path = 0; path < map.ContainerPathCount(id); path++)
            {
                string container = map.ContainerPath(id, path);
                if (!Rooted(container, roots))
                {
                    continue;
                }
                table.Row(UnrealPaths.Stem(container), container, UnrealPaths.Folder(container));
                break;
            }
        }
        return table.Build();
    }

    /// <summary>
    /// The family's own answer for where a cast row's animations live: none. A build organises its
    /// animation content however its studio chose to, which the cabmap's shared class vocabulary says
    /// nothing about -- a title that states its own convention replaces this (see
    /// <see cref="UnrealTitleDatasets"/>), and one that has not yet answers with an empty table, which
    /// <c>animation_rules</c> reads the same way it reads a row with none of its own: nothing found,
    /// said so rather than guessed at.
    /// </summary>
    private static ColumnTable CharactersAnimations(DataRequest request) =>
        new TableBuilder(CharactersAnimationsId, "anchor", "hits#", "group").Build();

    /// <summary>Whether the cabmap lists everything an Unreal class produces for this package -- the whole set, because any one of those ids alone is produced by half a dozen other classes.</summary>
    private static bool Holds(CabTable map, int id, int[] produced)
    {
        ReadOnlySpan<int> listed = map.ClassIds(id);
        foreach (int wanted in produced)
        {
            if (!listed.Contains(wanted))
            {
                return false;
            }
        }
        return produced.Length > 0;
    }

    private static bool Rooted(string container, string[] roots)
    {
        foreach (string root in roots)
        {
            if (container.Contains(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static ColumnTable Actors(DataRequest request)
    {
        TableBuilder table = new(ActorsId, "package", "name", "kind", "parent", "native", "skeletal#", "static#");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        Dictionary<string, int> cabIds = request.HasMap ? CabIds(request.Map) : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (UnrealActorScan.Actor actor in UnrealActorScan.Scan(provider))
        {
            (int skeletal, int statics) = request.HasMap && cabIds.TryGetValue(actor.Package, out int id) ? MeshDependencies(request.Map, id) : (0, 0);
            table.Row(actor.Package, actor.Name, actor.Kind, actor.Parent, actor.Native, skeletal, statics);
        }
        return table.Build();
    }

    private static Dictionary<string, int> CabIds(CabTable map)
    {
        Dictionary<string, int> ids = new(map.Count, StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id < map.Count; id++)
        {
            ids[map.CabName(id)] = id;
        }
        return ids;
    }

    /// <summary>How many of a package's direct dependencies carry a skeletal mesh, and how many a static one, by the classes the cabmap lists for them.</summary>
    private static (int Skeletal, int Static) MeshDependencies(CabTable map, int id)
    {
        int skeletal = 0;
        int statics = 0;
        foreach (int dependency in map.Dependencies(id))
        {
            ReadOnlySpan<int> classIds = map.ClassIds(dependency);
            if (classIds.IndexOf((int)ClassIDType.Mesh) < 0)
            {
                continue;
            }
            if (classIds.IndexOf((int)ClassIDType.SkinnedMeshRenderer) >= 0)
            {
                skeletal++;
            }
            else if (classIds.IndexOf((int)ClassIDType.MeshRenderer) >= 0)
            {
                statics++;
            }
        }
        return (skeletal, statics);
    }

    /// <summary>The ground a world's cells cover, or none when the build states no bounds for them.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Ground(IReadOnlyList<UnrealWorldCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (UnrealWorldCell cell in cells)
        {
            minX = Math.Min(minX, cell.Bounds.Min.X);
            minY = Math.Min(minY, cell.Bounds.Min.Y);
            maxX = Math.Max(maxX, cell.Bounds.Max.X);
            maxY = Math.Max(maxY, cell.Bounds.Max.Y);
        }
        return minX <= maxX && minY <= maxY ? (minX, minY, maxX, maxY) : (0, 0, 0, 0);
    }

    /// <summary>Every world the install ships, as <see cref="UnrealWorlds"/> reads them.</summary>
    private static ColumnTable Worlds(DataRequest request)
    {
        TableBuilder table = new(WorldsId, "world", "name", "partitioned", "cells#", "minX#", "minY#", "maxX#", "maxY#");
        if (!request.HasMap)
        {
            throw new InvalidOperationException(
                $"dataset '{WorldsId}' reads the cabmap; build or load one for this install first.");
        }
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        foreach (UnrealWorld world in UnrealWorlds.All(provider, request.Map))
        {
            (double minX, double minY, double maxX, double maxY) = Ground(world.Cells);
            table.Row(world.Package, world.Name, world.Partitioned ? "1" : "0", world.Cells.Count,
                minX, minY, maxX, maxY);
        }
        return table.Build();
    }

    private static ColumnTable WorldCells(DataRequest request)
    {
        TableBuilder table = new(WorldCellsId, "cell", "level", "grid", "hlevel#", "loadingRange#", "priority#",
            "minX#", "minY#", "minZ#", "maxX#", "maxY#", "maxZ#", "alwaysLoaded", "hlod", "clientOnly", "dataLayers", "present");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        bool windowed = request.Given(MinXParam) || request.Given(MaxXParam) || request.Given(MinYParam) || request.Given(MaxYParam);
        double minX = request.Real(MinXParam);
        double minY = request.Real(MinYParam);
        double maxX = request.Real(MaxXParam);
        double maxY = request.Real(MaxYParam);
        bool leveled = request.Given(LevelParam);
        int level = request.Integer(LevelParam);
        foreach (UnrealWorldCell cell in UnrealWorldPartition.Cells(provider, request.Text(WorldParam)))
        {
            if (leveled && !cell.AlwaysLoaded && cell.Level != level)
            {
                continue;
            }
            if (windowed && !cell.AlwaysLoaded
                && (cell.Bounds.Max.X < minX || cell.Bounds.Min.X > maxX || cell.Bounds.Max.Y < minY || cell.Bounds.Min.Y > maxY))
            {
                continue;
            }
            table.Row(cell.Name, cell.LevelPackage, cell.Grid, cell.Level, cell.LoadingRange, cell.Priority,
                cell.Bounds.Min.X, cell.Bounds.Min.Y, cell.Bounds.Min.Z, cell.Bounds.Max.X, cell.Bounds.Max.Y, cell.Bounds.Max.Z,
                cell.AlwaysLoaded ? "1" : "0", cell.Hlod ? "1" : "0", cell.ClientOnlyVisible ? "1" : "0",
                string.Join(ListSeparator, cell.DataLayers), provider.Files.ContainsKey(cell.LevelPackage) ? "1" : "0");
        }
        return table.Build();
    }

    private static ColumnTable SettingsSchema(DataRequest request)
    {
        TableBuilder table = new(SettingsSchemaId, "name", "kind", "default", "choices", "description", "required", "effective");
        UnrealTitle? title = UnrealTitles.For(request.GameRoot);
        bool engineUnstated = EngineUnstated(request.GameRoot);
        bool engineKnown = !engineUnstated || UnrealSourceOptions.EngineChoice(title) is not null;
        bool unversioned = engineKnown && StoresPropertiesUnversioned(request.GameRoot);
        UnrealKeyring.Schema schema = UnrealKeyring.Mappings(title);
        bool schemaKnown = UnrealSourceOptions.Text(UnrealSourceOptions.Mappings, title).Length > 0
            || schema.Provider is not null;
        UnrealKeyring.Keyset published = UnrealKeyring.Keys(title);
        foreach (UnrealSourceOptions.Option option in UnrealSourceOptions.Schema)
        {
            bool required = string.Equals(option.Name, UnrealSourceOptions.Engine, StringComparison.Ordinal) ? !engineKnown
                : unversioned && !schemaKnown && string.Equals(option.Name, UnrealSourceOptions.Mappings, StringComparison.Ordinal);
            table.Row(option.Name, option.Kind, option.Default, option.Choices, option.Description, required ? "1" : "0",
                Effective(option.Name, title, schema, published));
        }
        return table.Build();
    }

    /// <summary>
    /// Whether the open install's executable states no engine version -- true only for a root that
    /// holds archive folders and whose executable carries no build version literal; false while
    /// no install is open, when the question cannot be asked.
    /// </summary>
    private static bool EngineUnstated(string gameRoot)
    {
        if (gameRoot.Length == 0)
        {
            return false;
        }
        string[] pakFolders = UnrealInstall.PakFolders(gameRoot);
        return pakFolders.Length > 0 && UnrealInstall.EngineFromVersion(UnrealInstall.EngineVersion(pakFolders[0])) is null;
    }

    /// <summary>
    /// Whether the mounted build stores its objects' properties unversioned -- the layout only
    /// the build's own reflection schema can read -- judged by the first package the mount
    /// holds, every package of one cook sharing the flag. False while nothing mounts (an
    /// archive still waiting for its key), when the question cannot be answered yet.
    /// </summary>
    private static bool StoresPropertiesUnversioned(string gameRoot)
    {
        if (gameRoot.Length == 0)
        {
            return false;
        }
        try
        {
            UnrealFileProvider provider = UnrealProviderSession.Open(gameRoot);
            foreach (GameFile file in provider.Files.Values)
            {
                if (file.IsUePackage)
                {
                    return provider.LoadUncached(file) is AbstractUePackage package && package.HasFlags(EPackageFlags.PKG_UnversionedProperties);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] Could not tell whether the build stores its properties unversioned: {exception.GetType().Name}: {exception.Message}");
        }
        return false;
    }

    private static ColumnTable SessionState(DataRequest request)
    {
        TableBuilder table = new(SessionId, "project", "displayName", "engine", "engineVersion", "files#", "archives#",
            "mounted#", "missingKeys#", "mappings", "structs#", "unitScale#", "title", "build", "publishedKeys#");
        DefaultFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string[] pakFolders = UnrealInstall.PakFolders(request.GameRoot);
        UnrealTitles.Claim? claim = UnrealTitles.Of(request.GameRoot);
        table.Row(
            provider.ProjectName,
            provider.GameDisplayName ?? string.Empty,
            provider.Versions.Game.ToString(),
            pakFolders.Length > 0 ? UnrealInstall.EngineVersion(pakFolders[0]) : string.Empty,
            provider.Files.Count,
            provider.MountedVfs.Count + provider.UnloadedVfs.Count,
            provider.MountedVfs.Count,
            provider.RequiredKeys.Count,
            Stated(UnrealSourceOptions.Text(UnrealSourceOptions.Mappings, claim?.Title), UnrealKeyring.Mappings(claim?.Title).Name),
            provider.MappingsForGame?.Types.Count ?? 0,
            UnrealBasis.Basis.UnitScale,
            claim?.Title.Product ?? string.Empty,
            claim?.Version ?? string.Empty,
            UnrealKeyring.Keys(claim?.Title).Keys.Count);
        return table.Build();
    }

    /// <summary>The schema a mount reads through: the file a user named, else the name the title's endpoint gave the bytes it published.</summary>
    private static string Stated(string path, string published) => path.Length > 0 ? path : published;

    /// <summary>
    /// What this option is READ WITH right now, so a form shows an install that needs nothing
    /// typed as already answered rather than as blank and waiting. A recognised title answers the
    /// engine, the keys and the schema by itself; the two that are documents rather than text say
    /// what arrived and from where, because a quarter of a megabyte of keys is not a field value.
    /// </summary>
    /// <summary>
    /// What an option is ALREADY answered with, in the option's own syntax -- a value a host can
    /// put straight into the field that holds it.
    ///
    /// It has to be the value and never a description of one. Answering "3348 published by
    /// &lt;url&gt;" left a host with something it could only render BESIDE the field, so the field
    /// itself stayed blank and read as "still to be found" while the install was open on exactly
    /// those keys -- two places saying one thing, and the empty one the more prominent.
    /// </summary>
    private static string Effective(string name, UnrealTitle? title, UnrealKeyring.Schema schema, UnrealKeyring.Keyset published)
    {
        if (string.Equals(name, UnrealSourceOptions.MainKey, StringComparison.Ordinal))
        {
            string stated = UnrealSourceOptions.Text(UnrealSourceOptions.MainKey, title);
            return stated.Length > 0 ? stated
                : published.Keys.FirstOrDefault(entry => entry.Key == default).Value?.ToString() ?? string.Empty;
        }
        if (string.Equals(name, UnrealSourceOptions.DynamicKeys, StringComparison.Ordinal))
        {
            string stated = UnrealSourceOptions.Text(UnrealSourceOptions.DynamicKeys, title);
            if (stated.Length > 0)
            {
                return stated;
            }
            // The same guid=key;... the option is READ back as, written by the one place that
            // states that syntax, so what the field shows is what a host could have typed.
            return string.Join(UnrealSourceOptions.EntrySeparator, published.Keys
                .Where(static entry => entry.Key != default)
                .Select(static entry => $"{entry.Key}{UnrealSourceOptions.ValueSeparator}{entry.Value}"));
        }
        if (string.Equals(name, UnrealSourceOptions.Mappings, StringComparison.Ordinal))
        {
            string stated = UnrealSourceOptions.Text(UnrealSourceOptions.Mappings, title);
            if (stated.Length > 0)
            {
                return stated;
            }
            // A published schema is kept beside this title's other published facts, so it has a
            // path like any stated one -- the field takes that, not the file's bare name.
            return schema.Name.Length > 0 && title is not null
                ? Path.Combine(UnrealKeyring.KeptRoot, title.Product, schema.Name)
                : string.Empty;
        }
        return UnrealSourceOptions.Text(name, title);
    }

    private static ColumnTable Archives(DataRequest request)
    {
        TableBuilder table = new(ArchivesId, "name", "path", "encrypted", "mounted", "keyGuid", "files#", "index", "keyed");
        DefaultFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        foreach (IAesVfsReader reader in provider.MountedVfs)
        {
            table.Row(reader.Name, reader.Path, reader.IsEncrypted ? "1" : "0", "1", reader.EncryptionKeyGuid.ToString(),
                reader.FileCount, reader.HasDirectoryIndex ? "1" : "0", reader.AesKey is null ? "0" : "1");
        }
        foreach (IAesVfsReader reader in provider.UnloadedVfs)
        {
            table.Row(reader.Name, reader.Path, reader.IsEncrypted ? "1" : "0", "0", reader.EncryptionKeyGuid.ToString(),
                0, reader.HasDirectoryIndex ? "1" : "0", reader.AesKey is null ? "0" : "1");
        }
        return table.Build();
    }
}
