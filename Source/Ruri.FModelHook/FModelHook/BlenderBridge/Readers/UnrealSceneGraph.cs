using AssetRipper.Import.Logging;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.BlenderBridge.Readers;

/// <summary>
/// What a level places, read once for every consumer.
///
/// A level is a list of actors, each a small tree of scene components: the component the actor
/// calls its root, the components it was built with, and -- for a foliage actor -- the instanced
/// component of every foliage type it carries. Which of those count, what each is called, and
/// whether it shows are decisions about UNREAL, not about whatever is being built from them, so
/// they are made here: the Unity conversion builds GameObjects from this reading, and a host
/// reading the decoder's datasets builds its own objects from the same one.
///
/// A Blueprint's construction script describes the same kind of tree and is read by
/// <see cref="BlueprintConverter"/>; both fill the same <see cref="Collector"/>, so both
/// consumers see one shape.
/// </summary>
public static class UnrealSceneGraph
{
    private const string HiddenName = "bHidden";
    private const string RootComponentName = "RootComponent";
    private static readonly string[] ComponentListNames = ["InstanceComponents", "BlueprintCreatedComponents"];

    /// <summary>One placed component: where it sits in the tree, what it is called, whether it shows,
    /// and which actor of this reading it belongs to.</summary>
    public readonly record struct Placed(USceneComponent Component, int Parent, string Name, bool Active, int Actor);

    /// <summary>
    /// The components a reading states, in the order it states them, and the one ordering every
    /// consumer needs: parents before children, each stating the index of the component it
    /// attaches to (-1 for one whose parent this reading does not place).
    /// </summary>
    public sealed class Collector
    {
        private readonly List<USceneComponent> components = new();
        private readonly List<USceneComponent?> parents = new();
        private readonly List<string> names = new();
        private readonly List<bool> actives = new();
        private readonly List<int> actors = new();
        private readonly Dictionary<USceneComponent, int> index = new(ReferenceEqualityComparer.Instance);

        public int Count => components.Count;

        /// <summary>
        /// Which actor the components stated from here on belong to.
        ///
        /// Only the reading knows where one actor ends and the next begins: a Blueprint generated
        /// class IS one actor, however many components it states and whatever their attachment
        /// says, so that reading never touches this; a world states many, so its reading advances
        /// it per actor. A consumer that has to tell "two meshes of one character" apart from "two
        /// characters" -- which is the difference between one rig and two -- has no other way to.
        /// </summary>
        public int Actor { get; set; }

        /// <summary>State one component; the first statement of a component is the one that stands.</summary>
        public void Add(USceneComponent component, USceneComponent? parent, string name, bool active)
        {
            if (index.ContainsKey(component))
            {
                return;
            }
            index[component] = components.Count;
            components.Add(component);
            parents.Add(parent);
            names.Add(name);
            actives.Add(active);
            actors.Add(Actor);
        }

        public bool Contains(USceneComponent component) => index.ContainsKey(component);

        public List<Placed> Ordered()
        {
            List<Placed> ordered = new(components.Count);
            int[] placedAt = new int[components.Count];
            Array.Fill(placedAt, -1);
            for (int entry = 0; entry < components.Count; entry++)
            {
                Place(entry, placedAt, ordered, 0);
            }
            return ordered;
        }

        private int Place(int entry, int[] placedAt, List<Placed> ordered, int depth)
        {
            if (placedAt[entry] >= 0)
            {
                return placedAt[entry];
            }
            int parent = -1;
            if (parents[entry] is { } parentComponent && index.TryGetValue(parentComponent, out int parentEntry)
                && parentEntry != entry && depth < components.Count)
            {
                parent = Place(parentEntry, placedAt, ordered, depth + 1);
            }
            placedAt[entry] = ordered.Count;
            ordered.Add(new Placed(components[entry], parent, names[entry], actives[entry], actors[entry]));
            return placedAt[entry];
        }
    }

    /// <summary>Every actor of a world's persistent level; none, with a line saying so, when the level does not load.</summary>
    public static IEnumerable<UObject> Actors(UWorld world, string package)
    {
        // A world that names no persistent level, or a level that states no actor
        // array, is a world with nothing placed in it -- said once, rather than
        // thrown at whoever asked. An empty package name reaches here too: the
        // panel greys the button out, a script or a keymap does not have to.
        if (world is null
            || world.PersistentLevel is not { } persistent
            || persistent.Load<ULevel>() is not { } level
            || level.Actors is not { } actors)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {package}: the persistent level did not load; no actors placed.");
            yield break;
        }
        foreach (FPackageIndex? pointer in actors)
        {
            if (pointer?.Load() is { } actor)
            {
                yield return actor;
            }
        }
    }

    /// <summary>The component an actor calls its root, or null when it names none.</summary>
    public static USceneComponent? Root(UObject actor) =>
        actor.GetOrDefault<FPackageIndex?>(RootComponentName)?.Load<USceneComponent>();

    /// <summary>What the actor is called: the label it was given in the editor, else its own name.</summary>
    public static string Label(UObject actor) =>
        actor is AActor { ActorLabel: { Length: > 0 } label } ? label : actor.Name;

    /// <summary>Whether the actor itself is hidden, which hides the node its root component becomes.</summary>
    public static bool Hidden(UObject actor) => actor.GetOrDefault<bool>(HiddenName);

    /// <summary>
    /// The scene components of one actor: its root first, then the ones it was built with, then
    /// the instanced component of every foliage type a foliage actor carries.
    /// </summary>
    public static IEnumerable<USceneComponent> Components(UObject actor, USceneComponent? root)
    {
        if (root is not null)
        {
            yield return root;
        }
        foreach (string listName in ComponentListNames)
        {
            foreach (FPackageIndex? pointer in actor.GetOrDefault<FPackageIndex?[]>(listName, []))
            {
                if (pointer?.Load<USceneComponent>() is { } component)
                {
                    yield return component;
                }
            }
        }
        if (actor is AInstancedFoliageActor { FoliageInfos: { } foliage })
        {
            foreach (FFoliageInfo info in foliage.Values)
            {
                if (info.Implementation is FFoliageStaticMesh { Component: { IsNull: false } pointer }
                    && pointer.Load<USceneComponent>() is { } component)
                {
                    yield return component;
                }
            }
        }
    }

    /// <summary>
    /// One component's name and whether it shows: a component that IS the actor's root carries
    /// the actor's label and the actor's own hidden flag; every other carries its own name.
    /// </summary>
    public static (string Name, bool Active) Node(UObject actor, USceneComponent component, USceneComponent? root)
    {
        bool isRoot = ReferenceEquals(component, root);
        bool active = UnrealComponents.Visible(component) && !(isRoot && Hidden(actor));
        return (isRoot ? Label(actor) : component.Name, active);
    }

    /// <summary>
    /// Every scene component of every actor in a world, into <paramref name="collector"/>. An
    /// actor that throws while being read is reported and skipped; the rest still place, because
    /// one broken actor is not a broken level. Returns how many actors were read.
    /// </summary>
    public static int Collect(Collector collector, UWorld world, string package)
    {
        int actors = 0;
        foreach (UObject actor in Actors(world, package))
        {
            try
            {
                collector.Actor = actors;
                USceneComponent? root = Root(actor);
                foreach (USceneComponent component in Components(actor, root))
                {
                    (string name, bool active) = Node(actor, component, root);
                    collector.Add(component, component.AttachParent?.Load<USceneComponent>(), name, active);
                }
                actors++;
            }
            catch (Exception exception)
            {
                Logger.Warning(LogCategory.Import, $"[Unreal] {package} actor '{actor.Name}': {exception.GetType().Name}: {exception.Message}");
            }
        }
        return actors;
    }
}
