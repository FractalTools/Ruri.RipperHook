using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

internal static class SceneTransform
{
    private const float ScaleDownRatio = 0.01f;

    public static Matrix4x4 NodeMatrix(Transform placement)
    {
        return placement.Matrix;
    }

    public static Transform CalculateTransform(IPropertyHolder component, Transform relation)
    {
        if (component.TryGetValue(out FPackageIndex attachParent, "AttachParent") &&
            attachParent.TryLoad(out UObject parent))
        {
            relation = CalculateTransform(parent, relation);
        }

        return new Transform
        {
            Relation = relation.Matrix,
            Position = component.GetOrDefault("RelativeLocation", FVector.ZeroVector) * ScaleDownRatio,
            Rotation = component.GetOrDefault("RelativeRotation", FRotator.ZeroRotator).Quaternion(),
            Scale = component.GetOrDefault("RelativeScale3D", FVector.OneVector),
        };
    }

    public static Transform InstanceTransform(Transform componentRelation, FVector translation, FQuat rotation, FVector scale)
    {
        return new Transform
        {
            Relation = componentRelation.Matrix,
            Position = translation * ScaleDownRatio,
            Rotation = rotation,
            Scale = scale,
        };
    }
}
