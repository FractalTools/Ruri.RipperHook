using AssetRipper.SourceGenerated;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using System.Collections.Concurrent;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// What an Unreal class IS, in the vocabulary the browser lists every game's assets in.
///
/// A cabmap row carries the class ids of what a package holds, and that list is one vocabulary
/// across every title so one browser can filter them all. Unreal has its own class names, so
/// this is the table that says which of the shared ids each family answers to -- pure data:
/// adding a family is a row, and nothing here reads, converts or builds anything.
///
/// Ancestry is the reflection schema's own: a class the table never heard of is matched by
/// walking its super chain, so a subclass no one wrote a row for still lands on its family.
/// </summary>
public static class UnrealClasses
{
    private static readonly (string[] Names, ClassIDType[] Produces)[] Families =
    [
        (["StaticMesh"], [ClassIDType.Mesh, ClassIDType.GameObject, ClassIDType.MeshRenderer]),
        (["SkeletalMesh"], [ClassIDType.Mesh, ClassIDType.GameObject, ClassIDType.SkinnedMeshRenderer]),
        (["Skeleton"], [ClassIDType.GameObject, ClassIDType.Transform]),
        (["Texture2D", "LightMapTexture2D", "ShadowMapTexture2D", "VirtualTexture2D"], [ClassIDType.Texture2D]),
        (["MaterialInterface", "Material", "MaterialInstance", "MaterialInstanceConstant",
            "MaterialInstanceDynamic", "LandscapeMaterialInstanceConstant"], [ClassIDType.Material, ClassIDType.Shader]),
        (["AnimSequence"], [ClassIDType.AnimationClip]),
        (["World"], [ClassIDType.GameObject, ClassIDType.Transform, ClassIDType.MeshRenderer,
            ClassIDType.SkinnedMeshRenderer, ClassIDType.Light, ClassIDType.RenderSettings]),
        (["DataTable"], [ClassIDType.MonoBehaviour]),
        (["BlueprintGeneratedClass"], [ClassIDType.GameObject, ClassIDType.Transform, ClassIDType.MeshRenderer,
            ClassIDType.SkinnedMeshRenderer, ClassIDType.Light, ClassIDType.MonoBehaviour]),
    ];

    /// <summary>What a class no family claims is listed as: an object carrying its own values.</summary>
    private static readonly ClassIDType[] Unclaimed = [ClassIDType.MonoBehaviour];

    private static readonly ConcurrentDictionary<string, ClassIDType[]> ByClassName = new(StringComparer.Ordinal);

    /// <summary>The class ids a package holding this class is listed under.</summary>
    public static IReadOnlyList<ClassIDType> Of(string className, TypeMappings? mappings) =>
        ByClassName.GetOrAdd(className, name => Resolve(name, mappings));

    public static void Forget() => ByClassName.Clear();

    /// <summary>
    /// Whether a class is one no asset family claims -- an object that is nothing but the values
    /// its designer typed into it (a DataAsset and everything a game derives from one). The answer
    /// is the families table's own, so a reader asking this never grows a second class list.
    /// </summary>
    public static bool IsValueObject(string className, TypeMappings? mappings) =>
        ReferenceEquals(Of(className, mappings), Unclaimed);

    /// <summary>Whether <paramref name="className"/> is <paramref name="ancestor"/> or descends from it in the reflection schema.</summary>
    public static bool IsA(string className, string ancestor, TypeMappings? mappings)
    {
        foreach (string name in Ancestry(className, mappings))
        {
            if (string.Equals(name, ancestor, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The name of an object's class, empty when the class import did not resolve.</summary>
    public static string NameOf(ResolvedObject? @class) => @class?.Name.Text ?? string.Empty;

    /// <summary>The class name an export was allocated under: its class import's name.</summary>
    public static string Of(ResolvedObject header) => NameOf(header.Class);

    private static ClassIDType[] Resolve(string className, TypeMappings? mappings)
    {
        foreach (string name in Ancestry(className, mappings))
        {
            foreach ((string[] names, ClassIDType[] produces) in Families)
            {
                foreach (string handled in names)
                {
                    if (string.Equals(handled, name, StringComparison.Ordinal))
                    {
                        return produces;
                    }
                }
            }
        }
        return Unclaimed;
    }

    /// <summary>The class and its ancestors, nearest first, as far as the reflection schema names them.</summary>
    private static IEnumerable<string> Ancestry(string className, TypeMappings? mappings)
    {
        string? cursor = className;
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (cursor is not null && cursor.Length > 0 && seen.Add(cursor))
        {
            yield return cursor;
            cursor = mappings is not null && mappings.Types.TryGetValue(cursor, out Struct? schema) ? schema.SuperType : null;
        }
    }
}
