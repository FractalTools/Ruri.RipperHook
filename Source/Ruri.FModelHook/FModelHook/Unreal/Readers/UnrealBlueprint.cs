using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Unreal.Readers;

/// <summary>
/// The actor a Blueprint class builds. The class chain from the native actor down contributes,
/// root-most first, the components its construction script states; the leaf's default object
/// contributes the native components with the values the Blueprint set on them; the leaf's
/// inherited-component records replace the templates it overrides in an ancestor's script. Each
/// scene component attaches to the component it names -- a native component by property name, a
/// script variable by name, else the actor's root -- so the actor stands exactly as it does in a
/// level, and a level's actors and a Blueprint's script reach a consumer through one shape.
/// </summary>
public static class UnrealBlueprint
{
    private const string ActorClassName = "Actor";
    private const string RootComponentName = "RootComponent";
    private const string ParentVariableName = "ParentComponentOrVariableName";

    /// <summary>
    /// The actor a Blueprint class builds, stated into <paramref name="collector"/>: the native
    /// components of its default object, then the components each class in the chain's
    /// construction script adds, with the templates a leaf-ward class overrides substituted.
    /// Returns how many classes contributed.
    /// </summary>
    public static int Collect(UnrealSceneGraph.Collector collector, UBlueprintGeneratedClass leaf)
    {
        List<UBlueprintGeneratedClass> chain = Chain(leaf);
        UObject? defaults = leaf.ClassDefaultObject.Load() as UObject;
        Dictionary<string, USceneComponent> byName = new(StringComparer.Ordinal);
        USceneComponent? actorRoot = Natives(collector, defaults, byName);
        Dictionary<(string OwnerClass, string Variable), USceneComponent> overrides = Overrides(chain);
        foreach (UBlueprintGeneratedClass owner in chain)
        {
            if (owner.SimpleConstructionScript?.Load<USimpleConstructionScript>() is not { } script)
            {
                continue;
            }
            foreach (FPackageIndex? pointer in script.RootNodes)
            {
                if (pointer?.Load<USCS_Node>() is { } node)
                {
                    actorRoot = Script(collector, owner, node, ParentOf(node, byName) ?? actorRoot, actorRoot, byName, overrides);
                }
            }
        }
        return chain.Count;
    }

    /// <summary>Whether a generated class already read is one whose actor stands in a scene.</summary>
    public static bool IsActorClass(UBlueprintGeneratedClass leaf, TypeMappings? mappings) =>
        IsActorClass(leaf.SuperStruct?.ResolvedObject, mappings);

    /// <summary>Whether a generated class is one whose actor stands in a scene, by its super chain.</summary>
    public static bool IsActorClass(ResolvedObject? super, TypeMappings? mappings) =>
        mappings is not null
        && UnrealActorScan.NativeAncestor(super, mappings) is { } native
        && UnrealClasses.IsA(native, ActorClassName, mappings);

    /// <summary>The Blueprint classes from the root-most down to the leaf.</summary>
    private static List<UBlueprintGeneratedClass> Chain(UBlueprintGeneratedClass leaf)
    {
        List<UBlueprintGeneratedClass> chain = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        UBlueprintGeneratedClass? cursor = leaf;
        while (cursor is not null && seen.Add(cursor.GetPathName()))
        {
            chain.Add(cursor);
            cursor = cursor.SuperStruct.Load<UBlueprintGeneratedClass>();
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// The default object's own scene components: those its properties name, keyed by the
    /// property, plus any other scene subobject it owns. Returns the root component it states.
    /// </summary>
    private static USceneComponent? Natives(UnrealSceneGraph.Collector collector, UObject? defaults, Dictionary<string, USceneComponent> byName)
    {
        if (defaults is null)
        {
            return null;
        }
        string owner = defaults.GetPathName();
        List<(USceneComponent Component, string Name)> natives = new();
        USceneComponent? root = null;
        foreach (FPropertyTag tag in defaults.Properties)
        {
            if (tag.Tag?.GenericValue is not FPackageIndex pointer || pointer.Load() is not USceneComponent component || !IsOwnedBy(component, owner))
            {
                continue;
            }
            string name = tag.Name.Text;
            byName[name] = component;
            if (string.Equals(name, RootComponentName, StringComparison.Ordinal))
            {
                root = component;
            }
            else
            {
                natives.Add((component, name));
            }
        }
        if (defaults.Owner is { } package)
        {
            foreach (UObject export in package.GetExports())
            {
                if (export is USceneComponent component && IsOwnedBy(component, owner) && !byName.ContainsValue(component))
                {
                    byName[component.Name] = component;
                    natives.Add((component, component.Name));
                }
            }
        }
        if (root is not null)
        {
            collector.Add(root, null, root.Name, UnrealComponents.Visible(root));
        }
        foreach ((USceneComponent component, string name) in natives)
        {
            if (!collector.Contains(component))
            {
                collector.Add(component, component.AttachParent?.Load<USceneComponent>() ?? root, name, UnrealComponents.Visible(component));
            }
        }
        return root;
    }

    private static bool IsOwnedBy(UObject component, string ownerPath) =>
        string.Equals(component.Outer?.GetPathName(), ownerPath, StringComparison.Ordinal);

    /// <summary>The templates the leaf-ward classes substitute for ancestor script nodes, the nearest class to the leaf winning.</summary>
    private static Dictionary<(string OwnerClass, string Variable), USceneComponent> Overrides(IReadOnlyList<UBlueprintGeneratedClass> chain)
    {
        Dictionary<(string OwnerClass, string Variable), USceneComponent> overrides = new();
        foreach (UBlueprintGeneratedClass owner in chain)
        {
            if (owner.InheritableComponentHandler?.Load<UInheritableComponentHandler>() is not { } handler)
            {
                continue;
            }
            foreach (FComponentOverrideRecord record in handler.Records)
            {
                if (record.ComponentKey.OwnerClass is { } ownerClass && record.ComponentTemplate?.Load<USceneComponent>() is { } template)
                {
                    overrides[(ownerClass.GetPathName(), record.ComponentKey.SCSVariableName.Text)] = template;
                }
            }
        }
        return overrides;
    }

    /// <summary>The component a script node states it attaches to, by native property name or script variable.</summary>
    private static USceneComponent? ParentOf(USCS_Node node, Dictionary<string, USceneComponent> byName)
    {
        FName parentName = node.GetOrDefault<FName>(ParentVariableName);
        return !parentName.IsNone && byName.TryGetValue(parentName.Text, out USceneComponent? parent) ? parent : null;
    }

    /// <summary>
    /// One script node and its children into the tree. A node without a scene template still
    /// passes its children on to its own parent. Returns the actor root, which the first scene
    /// component becomes when the default object states none.
    /// </summary>
    private static USceneComponent? Script(UnrealSceneGraph.Collector collector, UBlueprintGeneratedClass owner, USCS_Node node, USceneComponent? parent,
        USceneComponent? actorRoot, Dictionary<string, USceneComponent> byName, Dictionary<(string OwnerClass, string Variable), USceneComponent> overrides)
    {
        string variable = node.InternalVariableName.Text;
        if (!overrides.TryGetValue((owner.GetPathName(), variable), out USceneComponent? template))
        {
            template = node.ComponentTemplate?.Load<USceneComponent>();
        }
        USceneComponent? childParent = parent;
        if (template is not null)
        {
            byName[variable] = template;
            actorRoot ??= template;
            collector.Add(template, ReferenceEquals(parent, template) ? null : parent, variable, UnrealComponents.Visible(template));
            childParent = template;
        }
        foreach (FPackageIndex? pointer in node.ChildNodes)
        {
            if (pointer?.Load<USCS_Node>() is { } child)
            {
                actorRoot = Script(collector, owner, child, childParent, actorRoot, byName, overrides);
            }
        }
        return actorRoot;
    }
}
