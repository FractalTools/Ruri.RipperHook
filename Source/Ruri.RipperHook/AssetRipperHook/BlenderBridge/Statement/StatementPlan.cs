using AssetRipper.Assets;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using System.Numerics;
using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.BlenderBridge.Statements;

public enum StatementKind
{
    Prefab,
    Scene,
    Loose,
    Window,
    Assembly,
    Parts,
    Placements,
}

/// <summary>What the requester asked the statement to be stated in and at.</summary>
public sealed class StatementOptions
{
    public Basis Basis { get; init; } = Basis.Unity;

    public int Detail { get; init; }

    public bool Inactive { get; init; } = true;

    public bool ShadowProxies { get; init; }

    public IReadOnlyList<string> RoleLayers { get; init; } = [];

    public IReadOnlyList<string> Containers { get; init; } = [];

    /// <summary>What makes two requests the SAME flattening. The basis is not in it: a
    /// basis converts the tables a flattening is read out of and nothing that is flattened,
    /// so asking one selection in two bases -- geometry in the host's own, the rig's rest in
    /// the one its animation data is written in -- must not decode the selection twice.</summary>
    public string Signature => string.Join('|', Detail, Inactive ? 1 : 0, ShadowProxies ? 1 : 0,
        string.Join(';', RoleLayers), string.Join(';', Containers));
}

/// <summary>One piece of an assembled character: the prefab's own name in the closure, the
/// bone of the rig it hangs on, and the game's own correction stated in the GAME's space
/// (metres, ZXY euler degrees). <c>Rig</c> marks the one piece that IS the skeleton.</summary>
public sealed record AssemblyPart(string Asset, string Label, string Anchor, Vector3 Position,
    Vector3 RotationDegrees, Vector3 Scale, bool Rig);

/// <summary>One mesh a recipe names: the mesh's own name and the container path the game
/// files it under, which is what tells it apart from a namesake in a pooled archive.</summary>
public sealed record PartMesh(string Name, string ContainerPath);

/// <summary>The shared skeleton a recipe's loose meshes bind to: the archive holding the avatar
/// template and the leaf names its template lists beside it.</summary>
public sealed record PartsSkeleton(string Cab, string AvatarNameStem);

/// <summary>One placement of a scene window: what it draws, where, and with which materials --
/// and what the path IS under the title's own addressable convention: a prefab (placed as its
/// pieces under one anchor) or a loose mesh, and the name it is shown under. A loose mesh states
/// how its renderer draws into shadow maps and whether that shadow falls in the directional
/// light's cascades; a prefab states neither, because its own renderers do.</summary>
public sealed record WindowPlacement(string AssetPath, string Name, Vector3 Position, Quaternion Rotation,
    Vector3 Scale, IReadOnlyList<string> MaterialPaths, bool IsPrefab, string Stem, string MeshName,
    ShadowCastingMode? Shadows, bool MainLightShadows);

/// <summary>What a material write sets through the engine's own material setters.</summary>
public enum MaterialWriteKind
{
    Color,
    Float,
    Texture,
    Keyword,
}

/// <summary>One value a title writes onto a renderer's materials at run time: the property or
/// keyword it names and what it writes -- four components for a colour, one for a float, one for a
/// keyword (non-zero enables it), and for a texture slot the texture, where null clears the slot.</summary>
public sealed record MaterialWrite(string Property, MaterialWriteKind Kind, float[] Value, ITexture2D? Texture);

/// <summary>What a renderer the prefab ships empty draws at run time: the mesh and the materials
/// the title puts on it, the bones that mesh's weights index when the title rebinds them, the
/// detail level that fill is (-1 unstated), and what the title writes onto every material the
/// renderer draws with once they are on, in the order it writes. Materials left empty and bones
/// left null keep whatever the renderer carries of its own.</summary>
public sealed record RendererFill(IMesh Mesh, IReadOnlyList<IMaterial?> Materials, IReadOnlyList<ITransform?>? Bones, int Lod,
    IReadOnlyList<MaterialWrite> Writes);

/// <summary>One light a plan states: what it is, where it stands and how its transform turns it, and how
/// bright, in the engine's own frame -- the colour linear as the engine emits it, the intensity the
/// component's own, the range and cone angles (full angles, degrees) as it states them, and the multiplier
/// on what it scatters into a participating medium (zero keeps it out). The rotation is the transform's own,
/// roll included: a cookie is projected along the light's axes, not only its forward. <see cref="Fade"/>
/// and <see cref="Parameters"/> are what the engine's light list carries beyond that
/// (<see cref="UnityLightInfo.Fade"/>, <see cref="UnityLightInfo.Parameters"/>).</summary>
public sealed record PlanLight(string Name, int Type, System.Numerics.Vector3 Position,
    System.Numerics.Quaternion Rotation, float Red, float Green, float Blue, float Intensity, float Range,
    float SpotAngle, float InnerSpotAngle, bool Shadows, float VolumeFactor, System.Numerics.Vector4 Fade,
    float[] Parameters);

/// <summary>One thing a plan states short of its source: what, how many, and which.</summary>
public sealed record PlanNote(string What, int Count, string Detail);

/// <summary>What one seed resolves to: which archives to read and how to read what is in
/// them. Inert on purpose -- resolving touches no closure of its own.</summary>
public sealed class StatementPlan
{
    public required string Seed { get; init; }

    public required string Label { get; init; }

    public required StatementKind Kind { get; init; }

    public required IReadOnlyList<string> Cabs { get; init; }

    public bool SeededOnly { get; init; }

    public IReadOnlyList<string> NamedRoots { get; init; } = [];

    public IReadOnlyList<PartMesh> Meshes { get; init; } = [];

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Dressing { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public PartsSkeleton? Skeleton { get; init; }

    public IReadOnlyList<AssemblyPart> Parts { get; init; } = [];

    public IReadOnlyList<string> WindowPaths { get; init; } = [];

    public IReadOnlyList<WindowPlacement> Placements { get; init; } = [];

    /// <summary>The lights this selection states of its own, for a selection whose lighting does
    /// not sit on any of its placements. A streaming level is the case: its placement data states
    /// renderers only, and the level's light lives beside them in its environment volumes.</summary>
    public IReadOnlyList<PlanLight> Lights { get; init; } = [];

    /// <summary>What the plan knows it states short of the source, reported with the statement rather
    /// than dropped: something the source carries that the plan could not reproduce exactly.</summary>
    public IReadOnlyList<PlanNote> Notes { get; init; } = [];

    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>A prefab whose renderers the title fills at run time: what each renderer, by
    /// name, draws. Asked once the closure is loaded, because what a renderer draws is an object
    /// of the closure rather than a name -- two parts may name their meshes alike.</summary>
    public Func<GameData, IReadOnlyDictionary<string, RendererFill>>? Fills { get; init; }

    /// <summary>A flattening of the plan's own, for an engine the Unity path does not read.</summary>
    public Func<StatementOptions, Statement>? Flatten { get; init; }

    /// <summary>Which clips of the loaded closure this seed states, and what each is called.
    /// Asked once the closure is loaded because the answer is topology rather than a name: one
    /// position of one controller is a handful of clips out of an archive holding thousands.
    /// Absent means every clip the seed's own archives carry.</summary>
    public Func<GameData, IReadOnlyDictionary<IUnityObjectBase, string>>? Clips { get; init; }

    public string Signature => string.Join('|', Seed, Kind, string.Join(';', Cabs), SeededOnly ? 1 : 0,
        string.Join(';', NamedRoots), Meshes.Count, Parts.Count, Placements.Count, Fills is null ? 0 : 1);
}

/// <summary>Where a seed that is not an archive of the loaded map is explained: a hook
/// registers what its own seeds mean. Asked in registration order; the first answer wins.</summary>
public static class StatementSources
{
    public delegate StatementPlan? Source(string seed, CabTable map, StatementOptions options);

    private static readonly List<Source> Sources = [];

    public static void Register(Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Sources.Add(source);
    }

    public static void Remove(Source source) => Sources.Remove(source);

    public static void Clear() => Sources.Clear();

    /// <summary>Every archive these seeds read, each once -- what a question about a selection's
    /// archives is asked of, so that the shaders it shades with or the shapes its meshes carry are
    /// read out of exactly what loading the seeds would load.</summary>
    public static string[] Archives(IEnumerable<string> seeds, CabTable map)
    {
        StatementOptions options = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> cabs = [];
        foreach (string seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed))
            {
                continue;
            }
            foreach (string cab in Resolve(seed, map, options).Cabs)
            {
                if (seen.Add(cab))
                {
                    cabs.Add(cab);
                }
            }
        }
        return cabs.ToArray();
    }

    /// <summary>The plan for a seed: what the registered sources state it is, else what the
    /// loaded map files under it -- an archive by name, the asset at a container path, or
    /// everything under a container folder -- read by the engine's own flattening.</summary>
    public static StatementPlan Resolve(string seed, CabTable map, StatementOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        foreach (Source source in Sources)
        {
            if (source(seed, map, options) is { } plan)
            {
                return plan;
            }
        }
        if (map.TryGetId(seed, out int cabId))
        {
            return ForCabs(map, [map.CabName(cabId)], seed,
                map.ContainerPathCount(cabId) > 0 ? map.ContainerPath(cabId, 0) : seed);
        }
        string[] cabs = CabMap.ResolveCabsForPaths(map, [seed]);
        if (cabs.Length == 0)
        {
            cabs = CabMap.ResolveCabsUnderFolder(map, seed);
        }
        if (cabs.Length > 0)
        {
            return ForCabs(map, cabs, seed, seed);
        }
        throw new ArgumentException(
            $"seed '{seed}' is neither an archive, a container path nor a container folder of the loaded "
            + "map, and no registered statement source claims it.");
    }

    private static StatementPlan ForCabs(CabTable map, string[] cabs, string seed, string named)
    {
        bool hasObjects = false;
        foreach (string cab in cabs)
        {
            hasObjects |= map.TryGetId(cab, out int cabId)
                && map.ClassIds(cabId).Contains((int)AssetRipper.SourceGenerated.ClassIDType.GameObject);
        }
        string path = named.Replace('\\', '/').TrimEnd('/');
        return new StatementPlan
        {
            Seed = seed,
            Label = Path.GetFileNameWithoutExtension(path[(path.LastIndexOf('/') + 1)..]),
            Kind = hasObjects ? StatementKind.Prefab : StatementKind.Loose,
            Cabs = cabs,
            SeededOnly = true,
        };
    }
}
