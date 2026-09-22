using AssetRipper.Assets;
using AssetRipper.Processing;
using System.Numerics;
using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.Statements;

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
/// pieces under one anchor) or a loose mesh, and the name it is shown under.</summary>
public sealed record WindowPlacement(string AssetPath, string Name, Vector3 Position, Quaternion Rotation,
    Vector3 Scale, IReadOnlyList<string> MaterialPaths, bool IsPrefab, string Stem, string MeshName);

/// <summary>What one seed resolves to: which archives to read and how to read what is in
/// them. Inert on purpose -- resolving touches no closure.</summary>
/// <summary>One light a plan states: what it is, where it points, and how bright, in the
/// engine's own frame. The direction is a forward vector because that is what the source states;
/// turning it into a rotation is the statement's job, done once.</summary>
public sealed record PlanLight(string Name, int Type, System.Numerics.Vector3 Forward,
    float Red, float Green, float Blue, float Intensity);

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

    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>A prefab whose renderers carry no mesh of their own: the mesh each renderer's
    /// transform draws, and the detail level the list states for it (-1 unstated), as the
    /// title keeps it beside the rig.</summary>
    public IReadOnlyDictionary<string, (string Mesh, int Lod)> RendererMeshes { get; init; } =
        new Dictionary<string, (string Mesh, int Lod)>(StringComparer.Ordinal);

    /// <summary>A flattening of the plan's own, for an engine the Unity path does not read.</summary>
    public Func<StatementOptions, Statement>? Flatten { get; init; }

    /// <summary>Which clips of the loaded closure this seed states, and what each is called.
    /// Asked once the closure is loaded because the answer is topology rather than a name: one
    /// position of one controller is a handful of clips out of an archive holding thousands.
    /// Absent means every clip the seed's own archives carry.</summary>
    public Func<GameData, IReadOnlyDictionary<IUnityObjectBase, string>>? Clips { get; init; }

    public string Signature => string.Join('|', Seed, Kind, string.Join(';', Cabs), SeededOnly ? 1 : 0,
        string.Join(';', NamedRoots), Meshes.Count, Parts.Count, Placements.Count, RendererMeshes.Count);
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

    /// <summary>The plan for a seed: what the registered sources state it is, else an archive
    /// name or container path of the loaded map read by the engine's own flattening.</summary>
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
            return ForCab(map, cabId, seed);
        }
        string[] cabs = CabMap.ResolveCabsForPaths(map, [seed]);
        if (cabs.Length > 0 && map.TryGetId(cabs[0], out int pathCab))
        {
            return ForCab(map, pathCab, seed);
        }
        throw new ArgumentException(
            $"seed '{seed}' is neither an archive nor a container path of the loaded map, and no "
            + "registered statement source claims it.");
    }

    private static StatementPlan ForCab(CabTable map, int cabId, string seed)
    {
        ReadOnlySpan<int> classIds = map.ClassIds(cabId);
        bool hasObjects = classIds.Contains((int)AssetRipper.SourceGenerated.ClassIDType.GameObject);
        StatementKind kind = hasObjects ? StatementKind.Prefab : StatementKind.Loose;
        string cab = map.CabName(cabId);
        string container = map.ContainerPathCount(cabId) > 0 ? map.ContainerPath(cabId, 0) : cab;
        return new StatementPlan
        {
            Seed = seed,
            Label = Path.GetFileNameWithoutExtension(container),
            Kind = kind,
            Cabs = [cab],
            SeededOnly = true,
        };
    }
}
