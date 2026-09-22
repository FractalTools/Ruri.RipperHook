using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Subclasses.AvatarConstant;
using AssetRipper.SourceGenerated.Subclasses.Skeleton;
using AssetRipper.SourceGenerated.Subclasses.SkeletonPose;
using AssetRipper.SourceGenerated.Subclasses.Xform;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// The FULL skeleton an Avatar carries, posed by its default pose: the whole transform tree
/// with parent links and CRC32-of-path ids, which is the rest a part mesh authored against a
/// shared skeleton was bound in. Not the human subset (normalised to another frame) and not
/// the avatar-skeleton pose, which re-poses a face into an incoherent shape.
/// </summary>
public sealed class AvatarSkeleton
{
    public required IReadOnlyDictionary<uint, double[]> WorldRests { get; init; }

    public required IReadOnlyList<string> Paths { get; init; }

    public required IReadOnlyDictionary<uint, string> PathByHash { get; init; }

    public static AvatarSkeleton Read(IAvatar avatar)
    {
        IAvatarConstant constant = avatar.Avatar;
        Dictionary<uint, string> tos = new();
        foreach (var pair in avatar.TOS)
        {
            if (pair.Value is not null && !pair.Value.IsEmpty)
            {
                tos[pair.Key] = pair.Value.String;
            }
        }
        ISkeleton skeleton = constant.AvatarSkeleton.Data;
        Dictionary<uint, double[]> worlds = new();
        if (constant.Has_DefaultPose())
        {
            ISkeletonPose pose = constant.DefaultPose.Data;
            int count = skeleton.Node.Count;
            double[]?[] resolved = new double[count][];
            double[] Resolve(int index)
            {
                if (resolved[index] is { } known)
                {
                    return known;
                }
                double[] local = index < pose.X.Count ? Local(pose.X[index]) : Mat4.Identity();
                int parent = skeleton.Node[index].ParentId;
                double[] world = parent >= 0 && parent < count ? Mat4.Multiply(Resolve(parent), local) : local;
                resolved[index] = world;
                return world;
            }
            for (int index = 0; index < count; index++)
            {
                if (index < skeleton.ID.Count)
                {
                    worlds[skeleton.ID[index]] = Resolve(index);
                }
            }
        }
        return new AvatarSkeleton
        {
            WorldRests = worlds,
            Paths = tos.Values.ToList(),
            PathByHash = tos,
        };
    }

    /// <summary>A local TRS entry as a matrix, scale before rotation, the quaternion taken as
    /// stored -- the pose array is authored normalised and is read as authored.</summary>
    private static double[] Local(IXform xform)
    {
        double tx, ty, tz;
        if (xform.Has_T3())
        {
            tx = xform.T3.X; ty = xform.T3.Y; tz = xform.T3.Z;
        }
        else
        {
            tx = xform.T4!.X; ty = xform.T4.Y; tz = xform.T4.Z;
        }
        double sx, sy, sz;
        if (xform.Has_S3())
        {
            sx = xform.S3.X; sy = xform.S3.Y; sz = xform.S3.Z;
        }
        else
        {
            sx = xform.S4!.X; sy = xform.S4.Y; sz = xform.S4.Z;
        }
        double qx = xform.Q.X, qy = xform.Q.Y, qz = xform.Q.Z, w = xform.Q.W;
        return
        [
            (1 - 2 * (qy * qy + qz * qz)) * sx, 2 * (qx * qy - qz * w) * sy, 2 * (qx * qz + qy * w) * sz, tx,
            2 * (qx * qy + qz * w) * sx, (1 - 2 * (qx * qx + qz * qz)) * sy, 2 * (qy * qz - qx * w) * sz, ty,
            2 * (qx * qz - qy * w) * sx, 2 * (qy * qz + qx * w) * sy, (1 - 2 * (qx * qx + qy * qy)) * sz, tz,
            0, 0, 0, 1,
        ];
    }
}
