using System.Runtime.CompilerServices;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Data;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// The one thing the kernel publishes about any selection: what it places, draws, wears and
/// plays, already in the requester's basis. A seed is the payload of any published row -- an
/// archive or container path of the loaded map is the engine's own flattening, anything else
/// is explained by the hook that published it. Every table of one statement is cut from one
/// flattening, kept only until the next different request.
/// </summary>
public static class StatementDatasets
{
    public const string IdPrefix = "core.statement.";
    public const string RootsId = "core.statement.roots";
    public const string NodesId = "core.statement.nodes";
    public const string MeshesId = "core.statement.meshes";
    public const string MorphsId = "core.statement.morphs";
    public const string CollidersId = "core.statement.colliders";
    public const string SkeletonsId = "core.statement.skeletons";
    public const string AvatarsId = "core.statement.avatars";
    public const string MaterialsId = "core.statement.materials";
    public const string TexturesId = "core.statement.textures";
    public const string TextureId = "core.statement.texture";
    public const string ClipsId = "core.statement.clips";
    public const string AudioClipsId = "core.statement.audioclips";
    public const string AudioClipId = "core.statement.audioclip";
    public const string ControllersId = "core.statement.controllers";
    public const string AnimatorsId = "core.statement.animators";
    public const string TimelinesId = "core.statement.timelines";
    public const string CinemachineId = "core.statement.cinemachine";
    public const string ReportId = "core.statement.report";
    public const string ArchivesId = "core.statement.archives";
    public const string BasesId = "core.bases";

    public const string Seed = "seed";
    public const string BasisParam = "basis";
    public const string Detail = "detail";
    public const string Inactive = "inactive";
    public const string ShadowProxies = "shadow_proxies";
    public const string CollidersFlag = "colliders";
    public const string Scripts = "scripts";
    public const string Roles = "roles";
    public const string Containers = "containers";
    public const string Texture = "texture";
    public const string Paths = "paths";
    public const string Avatar = "avatar";
    public const string AudioClip = "audioclip";

    private static readonly object Gate = new();
    private static string _cachedRequest = string.Empty;
    private static string _cachedSignature = string.Empty;
    private static Statement? _cached;

    private static DataParam[] Common(params DataParam[] extra) =>
    [
        DataParam.List(Seed), DataParam.Text(BasisParam, required: false), DataParam.Integer(Detail, required: false),
        DataParam.Flag(Inactive, required: false), DataParam.Flag(ShadowProxies, required: false), DataParam.Flag(CollidersFlag, required: false),
        DataParam.Flag(Scripts, required: false),
        DataParam.List(Roles),
        DataParam.List(Containers), .. extra,
    ];

    public static DataParam[] CommonParameters() => Common();

    public const string CommonText =
        " seed... are payloads of published rows (archives, container paths, or what a hook states); basis is unity, "
        + "blender or gltf (geometry and transforms both come out in it); detail is the level to keep, -1 every level; "
        + "inactive keeps disabled renderers (on unless stated 0); shadow_proxies keeps shadow-only renderers; colliders "
        + "states the collision volumes of the objects (core.statement.colliders; off states none); scripts reads the layouts of the "
        + "game's own scripts, which loads the install's managed assemblies (a MonoBehaviour has no fields to state without them); roles are "
        + "texture-role layer files, later overriding earlier; containers are the image containers accepted.";

    public static void Register()
    {
        Datasets.Publish(RootsId, DataRole.Statement, Common(),
            "What each seed resolved to: the node its tree starts at, its label and kind, and the transform the "
            + "statement's own frame sits at in the basis -- where a host places the whole of it." + CommonText, Roots);
        Datasets.Publish(NodesId, DataRole.Statement, Common(),
            "Every transform of every seed, parents before children: name, the engine's own transform path, what "
            + "sits on it (empty, mesh, skinned, light, camera), the local transform in the basis, and for a renderer "
            + "the mesh, skeleton and material keys it draws with, its layer, and for the root of a rig the avatar it is "
            + "built with. A skinned node's mesh is already baked to its rest "
            + "pose in the statement's own frame, so a host places it at the skeleton and not at the node." + CommonText, Nodes);
        Datasets.Publish(MeshesId, DataRole.Statement, Common(),
            "Every mesh the seeds draw as raw buffers in the basis: positions, normals, tangents, colours, every "
            + "texture-coordinate set, triangle indices, material sections, four influences a vertex beside the bone "
            + "paths they index, and bind poses." + CommonText, Meshes);
        Datasets.Publish(MorphsId, DataRole.Statement, Common(),
            "Every blend shape of every mesh as sparse deltas in the basis, one row per shape." + CommonText, Morphs);
        Datasets.Publish(CollidersId, DataRole.Statement, Common(),
            "Every collision volume the seeds put on their nodes, one row per component, in the node's own space and "
            + "the requester's basis: a box (centre, size), a sphere (centre, radius), a capsule (centre, radius, height, "
            + "axis) or a mesh (a key of core.statement.meshes), with whether it is a trigger, whether it is enabled, the "
            + "layer of its object and the physics material it names. Empty unless colliders is asked." + CommonText, Colliders);
        Datasets.Publish(SkeletonsId, DataRole.Statement, Common(),
            "Every seed's transform tree as a skeleton, parents before children: name, path, the identity a rig "
            + "carries for it, the rest local transform in the basis, and the humanoid slot it fills." + CommonText, Skeletons);
        Datasets.Publish(AvatarsId, DataRole.Statement, Common(),
            "The avatar each skeleton was built with, in the form the humanoid solver reads back -- what a host "
            + "stamps on the rig and hands to core.statement.clips as its avatar argument." + CommonText, Avatars);
        Datasets.Publish(MaterialsId, DataRole.Statement, Common(),
            "Every material the seeds wear, one row per entry told apart by kind: m the material (name, shader), "
            + "k a keyword, p a disabled pass, s a pass its shader draws with (its name; texture holds its LightMode "
            + "tag, empty when untagged), t a texture property (texture key, tiling and offset), f a number, c a "
            + "vector, n the decode a role layer states for one texture, r a resolved surface role (a colour role "
            + "names its texture; a channel role names the texture and the channel in x; a value role carries the "
            + "value), u a texture property no role layer names." + CommonText, Materials);
        Datasets.Publish(TexturesId, DataRole.Statement, Common(),
            "Every texture those materials read: the container it was decoded into, its name and whether the asset "
            + "itself declares sRGB encoding. The PIXELS are asked for one texture at a time from "
            + "core.statement.texture, because a selection's images run to gigabytes and a host loads them one by "
            + "one anyway." + CommonText, Textures);
        Datasets.PublishBlob(TextureId, DataRole.Payload, Common(DataParam.Text(Texture)),
            "The bytes of ONE texture of a selection, in the container core.statement.textures states for it. Asked "
            + "per texture rather than as a column beside the rest: a scene's images are gigabytes, which is more "
            + "than one array can hold and far more than a host wants resident to load them one at a time."
            + CommonText, TextureBytes);
        Datasets.Publish(ClipsId, DataRole.Statement,
            Common(DataParam.List(Paths, required: true), DataParam.Text(Avatar)),
            "Every animation clip the seeds themselves carry, as the target it plays on reads it: a JSON index "
            + "beside a float32 payload. There is no reading a clip on its own -- read the target's skeleton FIRST: "
            + "a clip stores its bindings as CRC32 hashes, and without the target's paths every bone curve would be "
            + "a path_0x<crc>_ placeholder that matches no bone. paths+ are the target's object hierarchy -- the "
            + "reversible strings Unity itself hashes once to bind a clip -- onto which every curve is re-anchored; "
            + "avatar is the avatar that hierarchy was built with, against which a muscle-encoded clip is solved into "
            + "bone curves." + CommonText, Clips);
        Datasets.Publish(AudioClipsId, DataRole.Statement, Common(),
            "Every audio clip the seeds themselves carry, one row each as the engine plays it: the channels, frequency and whole "
            + "number of samples per channel the clip's sound bank states, the duration those make (the sample count over the "
            + "frequency, which is how the game's own timeline takes a clip's length), the length the clip itself serializes in "
            + "single precision, its compression format and the size of its Ogg Vorbis file. The file itself is asked for one clip "
            + "at a time from core.statement.audioclip. A clip that cannot be restated as Ogg Vorbis states no row and is named in "
            + "the report." + CommonText, AudioClips);
        Datasets.PublishBlob(AudioClipId, DataRole.Payload, Common(DataParam.Text(AudioClip)),
            "The bytes of ONE audio clip of a selection as a standard Ogg Vorbis file, rebuilt from the clip's sound bank. Asked "
            + "per clip rather than as a column beside the rest: a selection's sounds run to many megabytes."
            + CommonText, AudioClipBytes);
        Datasets.Publish(ControllersId, DataRole.Statement, Common(),
            "Every animator controller the seeds themselves carry, one row each with the whole controller as a JSON "
            + "record: parameters, layers, state machines, states, transitions, conditions and blend trees, every clip it "
            + "plays named by the key core.statement.clips states for it, and beside the fields a host models the fields "
            + "it does not (mirror, cycle offset, tags, behaviours, solo and mute, interruption sources the engine "
            + "lacks), so that a host can refuse what it cannot play rather than lose it." + CommonText, Controllers);
        Datasets.Publish(TimelinesId, DataRole.Statement, Common(),
            "Every timeline asset the directors of the seeds play, or that the seeds themselves carry, one row each with the "
            + "whole timeline as a JSON record: its tracks in a depth-first walk, each track's clips with the playable asset each "
            + "plays, its markers, and for a timeline a director plays that director's scene path and the object each track is "
            + "bound to; every serialized field is stated under the name the script of the game gives it and a pointer as the "
            + "key of what it points at, so that a host refuses what it cannot play rather than lose it. scripts reads the "
            + "layouts of the game's own scripts, which a timeline cannot be stated without: the table is empty unless it is asked."
            + CommonText, Timelines);
        Datasets.Publish(CinemachineId, DataRole.Statement, Common(),
            "Every component of the camera package's own scripts (the assembly Cinemachine; its post-processing component, which "
            + "names a render pipeline profile and nothing of the camera, is left out) the seeds place, one row each: the node it "
            + "sits on (its source path, the path the scene layer states the node by), the class of its script, and the component "
            + "as a JSON record -- whether it is enabled and every field its script serializes, each under the name the script gives "
            + "it, a vector as an object of x, y and z, a pointer to an object of the scene as the source path of that object, a "
            + "pointer to a scriptable object (a noise profile, a table of blends) as the asset itself with its name. A pointer to "
            + "a node the selection does not hold, or to anything a layer of nodes cannot state, refuses this table, never leaves a "
            + "component out of it (the report names each component that could not be stated; the other tables are unaffected). "
            + "scripts reads the layouts of the game's own scripts, which these components cannot be stated without: the table is "
            + "empty unless it is asked." + CommonText, Cinemachine);
        Datasets.Publish(AnimatorsId, DataRole.Statement, Common(),
            "Every Animator component the seeds place: the node it sits on (its path as core.statement.nodes states it), "
            + "the controller it plays and the avatar it was built with, whether it is enabled, whether it applies root "
            + "motion, and its update and culling modes." + CommonText, Animators);
        Datasets.Publish(ArchivesId, DataRole.Internal, [DataParam.List(Seed, required: true)],
            "Where each seed lives: every archive loading it would read, with the container path "
            + "that archive files first -- answered by the same resolution a load makes.", Archives);
        Datasets.Publish(BasesId, DataRole.Introspection, [],
            "Every basis a statement can be asked in: its name, whether it reverses triangle "
            + "winding and flips texture coordinates, the conversion itself as a row-major 4x4, "
            + "the top-level frame a whole statement sits in, and the local turn that lands a "
            + "camera or a light on this basis' own aim convention. What a host reads when it "
            + "needs the conversion rather than converted numbers.", Bases);
        Datasets.Publish(ReportId, DataRole.Statement, Common(),
            "What the flattening left out or rebuilt, and why: levels of detail dropped, shadow proxies, inactive "
            + "renderers, meshes without geometry, primitives rebuilt, texture properties no role layer names." + CommonText, Report);
    }

    private static StatementOptions Options(DataRequest request) => new()
    {
        Basis = request.Given(BasisParam) ? Basis.Parse(request.Text(BasisParam)) : Basis.Unity,
        Detail = request.Given(Detail) ? request.Integer(Detail) : 0,
        Inactive = !request.Given(Inactive) || request.Flag(Inactive),
        ShadowProxies = request.Flag(ShadowProxies),
        Colliders = request.Flag(CollidersFlag),
        Scripts = request.Given(Scripts) && request.Flag(Scripts),
        RoleLayers = request.List(Roles),
        Containers = request.List(Containers),
    };

    /// <summary>The flattening every table of one request is cut from. One is kept: the tables
    /// of a request are asked one after another, and a different request replaces it.
    ///
    /// Found by the REQUEST first -- its seeds, options and map -- because resolving a seed can
    /// read archives, and every table and every texture of one request asks again. Only a request
    /// not seen last resolves its seeds, and then the plans' own signature is what decides whether
    /// the flattening it would make is the one already kept.</summary>
    public static Statement Flatten(DataRequest request)
    {
        CabTable map = request.Map;
        string[] seeds = request.List(Seed);
        if (seeds.Length == 0)
        {
            throw new ArgumentException("a statement needs at least one seed.");
        }
        StatementOptions options = Options(request);
        string mapIdentity = MapIdentity(map);
        string asked = Asked(seeds, options, map);
        lock (Gate)
        {
            if (_cached is not null && _cachedRequest == asked)
            {
                return _cached;
            }
        }
        List<StatementPlan> plans = new(seeds.Length);
        foreach (string seed in seeds)
        {
            plans.Add(StatementSources.Resolve(seed, map, options));
        }
        string signature = string.Join("\n", plans.Select(plan => plan.Signature)) + "\n" + options.Signature + "\n"
            + mapIdentity;
        lock (Gate)
        {
            if (_cached is not null && _cachedSignature == signature)
            {
                _cachedRequest = asked;
                return _cached;
            }
        }
        Statement statement = StatementFlattener.Flatten(map, plans, options, request.Cancellation);
        lock (Gate)
        {
            _cached = statement;
            _cachedSignature = signature;
            _cachedRequest = asked;
        }
        return statement;
    }

    public static void Forget()
    {
        lock (Gate)
        {
            _cached = null;
            _cachedSignature = string.Empty;
            _cachedRequest = string.Empty;
        }
    }

    /// <summary>
    /// The host is done with the selection this request asked about: the flattening its tables
    /// were cut from is let go now, instead of when the next selection is asked about. Kept until
    /// then, the last load's meshes and every one of its images stayed resident for as long as the
    /// session sat idle after it -- gigabytes after a scene. A flattening a later request has
    /// already taken over is that request's, and is not touched.
    /// </summary>
    public static void Release(DataRequest request)
    {
        string asked = Asked(request.List(Seed), Options(request), request.Map);
        lock (Gate)
        {
            if (_cached is not null && _cachedRequest == asked)
            {
                _cached = null;
                _cachedSignature = string.Empty;
                _cachedRequest = string.Empty;
            }
        }
    }

    /// <summary>What a request asked, before its seeds are resolved: the seeds, every option a
    /// flattening depends on, and which map it reads.</summary>
    private static string Asked(string[] seeds, StatementOptions options, CabTable map)
        => string.Join("\n", seeds) + "\n" + options.Signature + "\n" + MapIdentity(map);

    private static string MapIdentity(CabTable map)
        => RuntimeHelpers.GetHashCode(map).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Basis BasisOf(DataRequest request) => Options(request).Basis;

    private static ColumnTable Roots(DataRequest request) => StatementTables.Roots(RootsId, Flatten(request), BasisOf(request));

    private static ColumnTable Nodes(DataRequest request) => StatementTables.Nodes(NodesId, Flatten(request), BasisOf(request));

    private static ColumnTable Meshes(DataRequest request) => StatementTables.Meshes(MeshesId, Flatten(request), BasisOf(request));

    private static ColumnTable Morphs(DataRequest request) => StatementTables.Morphs(MorphsId, Flatten(request), BasisOf(request));

    private static ColumnTable Colliders(DataRequest request) =>
        StatementTables.Colliders(CollidersId, Flatten(request), BasisOf(request));

    private static ColumnTable Skeletons(DataRequest request) => StatementTables.Skeletons(SkeletonsId, Flatten(request), BasisOf(request));

    private static ColumnTable Avatars(DataRequest request) => StatementTables.Avatars(AvatarsId, Flatten(request));

    private static ColumnTable Materials(DataRequest request) => StatementTables.Materials(MaterialsId, Flatten(request));

    private static ColumnTable Textures(DataRequest request) => StatementTables.Textures(TexturesId, Flatten(request));

    private static byte[] TextureBytes(DataRequest request)
    {
        string wanted = request.Text(Texture);
        foreach (StatementTexture texture in Flatten(request).Textures)
        {
            if (string.Equals(texture.Key, wanted, StringComparison.Ordinal))
            {
                return texture.Image;
            }
        }
        throw new ArgumentException(
            $"this selection states no texture '{wanted}' -- ask {TexturesId} which it carries.");
    }

    private static ColumnTable AudioClips(DataRequest request) =>
        StatementTables.AudioClips(AudioClipsId, Flatten(request).AudioClips);

    private static byte[] AudioClipBytes(DataRequest request)
    {
        string wanted = request.Text(AudioClip);
        foreach (StatementAudioClip clip in Flatten(request).AudioClips)
        {
            if (string.Equals(clip.Key, wanted, StringComparison.Ordinal))
            {
                return clip.Ogg;
            }
        }
        throw new ArgumentException(
            $"this selection states no audio clip '{wanted}' -- ask {AudioClipsId} which it carries.");
    }

    private static ColumnTable Controllers(DataRequest request) =>
        StatementTables.Controllers(ControllersId, Flatten(request).Controllers);

    private static ColumnTable Timelines(DataRequest request) =>
        StatementTables.Timelines(TimelinesId, Flatten(request).Timelines);

    private static ColumnTable Cinemachine(DataRequest request)
    {
        Statement statement = Flatten(request);
        if (statement.CinemachineRefused.Count > 0)
        {
            throw new InvalidOperationException(
                $"{statement.CinemachineRefused.Count} of {statement.Cinemachine.Count + statement.CinemachineRefused.Count} "
                + $"component(s) of the camera package cannot be stated: {string.Join("; ", statement.CinemachineRefused)}");
        }
        return StatementTables.Cinemachine(CinemachineId, statement.Cinemachine);
    }

    private static ColumnTable Animators(DataRequest request) =>
        StatementTables.Animators(AnimatorsId, Flatten(request).Animators);

    private static ColumnTable Report(DataRequest request) => StatementTables.Report(ReportId, Flatten(request));

    private static ColumnTable Bases(DataRequest request) => StatementTables.Bases(BasesId);

    private static ColumnTable Archives(DataRequest request)
    {
        CabTable map = request.Map;
        TableBuilder table = new(ArchivesId, "seed", "cab", "container");
        foreach (string seed in request.List(Seed))
        {
            foreach (string cab in StatementSources.Archives([seed], map))
            {
                string container = map.TryGetId(cab, out int id) && map.ContainerPathCount(id) > 0
                    ? map.ContainerPath(id, 0)
                    : string.Empty;
                table.Row(seed, cab, container);
            }
        }
        return table.Build();
    }

    private static ColumnTable Clips(DataRequest request)
    {
        Statement statement = Flatten(request);
        string[] paths = request.List(Paths);
        string avatar = request.Text(Avatar);
        List<StatementClip> restated = new(statement.Clips.Count);
        List<string> notes = [];
        foreach (StatementClip clip in statement.Clips)
        {
            restated.Add(ClipStatement.Restate(clip, paths, avatar, notes.Add));
        }
        ColumnTable table = StatementTables.Clips(ClipsId, restated);
        foreach (string entry in notes)
        {
            AssetRipper.Import.Logging.Logger.Warning(AssetRipper.Import.Logging.LogCategory.Export, "[statement] " + entry);
        }
        return table;
    }
}
