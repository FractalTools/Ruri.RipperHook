using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.RipperHook.Conversion;
using System.Numerics;

namespace Ruri.FModelHook.Unreal.Readers;

/// <summary>
/// What a scene component states about itself: whether it shows, where it sits, and which
/// material each of its mesh slots draws with. Three facts about Unreal, each with exactly one
/// place they are read, so no consumer of the decoder can hold a different answer.
/// </summary>
public static class UnrealComponents
{
    private const string HiddenInGameName = "bHiddenInGame";
    private const string VisibleName = "bVisible";

    /// <summary>Whether the component shows: hidden in game or marked invisible means not.</summary>
    public static bool Visible(USceneComponent component)
    {
        if (component.TryGetValue(out bool hidden, HiddenInGameName) && hidden)
        {
            return false;
        }
        return !component.TryGetValue(out bool visible, VisibleName) || visible;
    }

    /// <summary>A transform through the host's basis -- the one place a placement changes hands.</summary>
    public static (Vector3 Position, Quaternion Rotation, Vector3 Scale) Transform(SourceBasis basis, FTransform transform)
    {
        Vector3 position = basis.Position(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
        Quaternion rotation = basis.Rotation(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        Vector3 scale = basis.Scale(transform.Scale3D.X, transform.Scale3D.Y, transform.Scale3D.Z);
        return (position, rotation, scale);
    }

    /// <summary>
    /// The object path of the material each of a mesh's slots draws with: the component's
    /// override where it states one, else the mesh's own; empty for a slot nothing names.
    /// </summary>
    public static string[] MaterialPaths(UObject mesh, FPackageIndex?[] overrides)
    {
        FPackageIndex?[] slots = mesh switch
        {
            UStaticMesh staticMesh => staticMesh.StaticMaterials.Select(static slot => slot.MaterialInterface).ToArray(),
            USkeletalMesh skeletalMesh => skeletalMesh.SkeletalMaterials.Select(static slot => slot.Material).ToArray(),
            _ => [],
        };
        string[] paths = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            FPackageIndex? chosen = i < overrides.Length && overrides[i] is { IsNull: false } ? overrides[i] : slots[i];
            paths[i] = chosen is { IsNull: false } ? chosen.ResolvedObject?.GetPathName() ?? string.Empty : string.Empty;
        }
        return paths;
    }
}
