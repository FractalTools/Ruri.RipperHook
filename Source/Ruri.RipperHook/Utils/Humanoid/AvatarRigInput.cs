using System.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Subclasses.Axes;
using AssetRipper.SourceGenerated.Subclasses.AvatarConstant;
using AssetRipper.SourceGenerated.Subclasses.Human;
using AssetRipper.SourceGenerated.Subclasses.Skeleton;
using AssetRipper.SourceGenerated.Subclasses.SkeletonPose;
using AssetRipper.SourceGenerated.Subclasses.Vector3Float;
using AssetRipper.SourceGenerated.Subclasses.Vector4Float;
using AssetRipper.SourceGenerated.Subclasses.Xform;

namespace Ruri.RipperHook.Humanoid;

public sealed class AvatarRigInput
{
    public required int[] NodeParent { get; init; }
    public required int[] NodeAxesId { get; init; }

    public required uint[] NodeId { get; init; }

    public required AxesRow[] Axes { get; init; }

    public required Dictionary<uint, string> Tos { get; init; }

    public required int[] HumanBoneIndex { get; init; }
    public required int[] LeftHandBoneIndex { get; init; }
    public required int[] RightHandBoneIndex { get; init; }
    public required float[] HumanBoneMass { get; init; }

    public required Quaternion RootRestQ { get; init; }

    public required float ArmTwist { get; init; }
    public required float ForeArmTwist { get; init; }
    public required float UpperLegTwist { get; init; }
    public required float LegTwist { get; init; }

    public required (Vector3 T, Quaternion Q)[] SkeletonPose { get; init; }

    public required float HumanScale { get; init; }

    public required (Vector3 T, Quaternion Q, Vector3 S) RootParent { get; init; }

    public readonly record struct AxesRow(
        Quaternion PreQ, Quaternion PostQ, Vector3 Sgn, Vector3 LimitMin, Vector3 LimitMax);


    public static AvatarRigInput FromAvatar(IAvatar avatar)
    {
        IAvatarConstant constant = avatar.Avatar;
        IHuman human = constant.Human.Data;
        ISkeleton skeleton = human.Skeleton.Data;

        int nodeCount = skeleton.Node.Count;
        int[] nodeParent = new int[nodeCount];
        int[] nodeAxesId = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            nodeParent[i] = skeleton.Node[i].ParentId;
            nodeAxesId[i] = skeleton.Node[i].AxesId;
        }

        uint[] nodeId = new uint[skeleton.ID.Count];
        for (int i = 0; i < nodeId.Length; i++)
        {
            nodeId[i] = skeleton.ID[i];
        }

        AxesRow[] axes = new AxesRow[skeleton.AxesArray.Count];
        for (int i = 0; i < axes.Length; i++)
        {
            IAxes entry = skeleton.AxesArray[i];
            axes[i] = new AxesRow(
                ToQuaternion(entry.PreQ),
                ToQuaternion(entry.PostQ),
                GetSgn(entry),
                GetLimit(entry, min: true),
                GetLimit(entry, min: false));
        }

        Dictionary<uint, string> tos = new(avatar.TOS.Count);
        foreach (var pair in avatar.TOS)
        {
            Utf8String? path = pair.Value;
            if (path is not null && !path.IsEmpty)
            {
                tos[pair.Key] = path.String;
            }
        }

        float[] mass = new float[human.HumanBoneMass.Count];
        for (int i = 0; i < mass.Length; i++)
        {
            mass[i] = human.HumanBoneMass[i];
        }

        ISkeletonPose skeletonPose = human.SkeletonPose.Data;
        (Vector3, Quaternion)[] pose = new (Vector3, Quaternion)[skeletonPose.X.Count];
        for (int i = 0; i < pose.Length; i++)
        {
            IXform xform = skeletonPose.X[i];
            pose[i] = (ToXformTranslation(xform), ToQuaternion(xform.Q));
        }

        return new AvatarRigInput
        {
            NodeParent = nodeParent,
            NodeAxesId = nodeAxesId,
            NodeId = nodeId,
            Axes = axes,
            Tos = tos,
            HumanBoneIndex = ToIntArray(human.HumanBoneIndex),
            LeftHandBoneIndex = ToIntArray(human.LeftHand.Data.HandBoneIndex),
            RightHandBoneIndex = ToIntArray(human.RightHand.Data.HandBoneIndex),
            HumanBoneMass = mass,
            RootRestQ = ToQuaternion(human.RootX.Q),
            ArmTwist = human.ArmTwist,
            ForeArmTwist = human.ForeArmTwist,
            UpperLegTwist = human.UpperLegTwist,
            LegTwist = human.LegTwist,
            SkeletonPose = pose,
            HumanScale = human.Scale,
            RootParent = RootParentRest(constant),
        };
    }

    private static int[] ToIntArray(AssetRipper.Assets.Generics.AssetList<int> list)
    {
        int[] result = new int[list.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = list[i];
        }
        return result;
    }

    private static Quaternion ToQuaternion(IVector4Float v) => new(v.X, v.Y, v.Z, v.W);

    private static Vector3 GetSgn(IAxes axes)
    {
        if (axes.Has_Sgn_Vector3Float())
        {
            return ToVector3(axes.Sgn_Vector3Float);
        }
        Vector4Float? sgn4 = axes.Sgn_Vector4Float;
        return sgn4 is null ? Vector3.One : new Vector3(sgn4.X, sgn4.Y, sgn4.Z);
    }

    private static Vector3 GetLimit(IAxes axes, bool min)
    {
        if (min)
        {
            if (axes.Limit.Has_Min_Vector3Float())
            {
                return ToVector3(axes.Limit.Min_Vector3Float);
            }
            Vector4Float? min4 = axes.Limit.Min_Vector4Float;
            return min4 is null ? Vector3.Zero : new Vector3(min4.X, min4.Y, min4.Z);
        }
        if (axes.Limit.Has_Max_Vector3Float())
        {
            return ToVector3(axes.Limit.Max_Vector3Float);
        }
        Vector4Float? max4 = axes.Limit.Max_Vector4Float;
        return max4 is null ? Vector3.Zero : new Vector3(max4.X, max4.Y, max4.Z);
    }

    private static Vector3 ToVector3(IVector3Float? v) => v is null ? Vector3.Zero : new Vector3(v.X, v.Y, v.Z);

    private static Vector3 ToXformTranslation(IXform xform)
    {
        return xform.Has_T3() ? ToVector3(xform.T3) : new Vector3(xform.T4!.X, xform.T4.Y, xform.T4.Z);
    }

    private static Vector3 ToXformScale(IXform xform)
    {
        return xform.Has_S3() ? ToVector3(xform.S3) : new Vector3(xform.S4!.X, xform.S4.Y, xform.S4.Z);
    }

    /// <summary>The rest of the hips' real parent, which the human skeleton's pose flattens into the hips.</summary>
    private static (Vector3 T, Quaternion Q, Vector3 S) RootParentRest(IAvatarConstant constant)
    {
        (Vector3 T, Quaternion Q, Vector3 S) world = (Vector3.Zero, Quaternion.Identity, Vector3.One);
        if (!constant.Has_DefaultPose() || constant.HumanSkeletonIndexArray.Count == 0)
        {
            return world;
        }
        ISkeleton skeleton = constant.AvatarSkeleton.Data;
        ISkeletonPose pose = constant.DefaultPose.Data;
        List<int> chain = [];
        for (int node = constant.HumanSkeletonIndexArray[0]; node >= 0 && node < skeleton.Node.Count; node = skeleton.Node[node].ParentId)
        {
            chain.Add(node);
        }
        for (int index = chain.Count - 1; index >= 0; index--)
        {
            IXform local = pose.X[chain[index]];
            world = (world.T + Vector3.Transform(world.S * ToXformTranslation(local), world.Q),
                Quaternion.Normalize(world.Q * ToQuaternion(local.Q)), world.S * ToXformScale(local));
        }
        return world;
    }

}
