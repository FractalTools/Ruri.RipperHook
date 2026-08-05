using System.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.Bones;
using AssetRipper.SourceGenerated.Subclasses.Axes;
using AssetRipper.SourceGenerated.Subclasses.Hand;
using AssetRipper.SourceGenerated.Subclasses.Human;
using AssetRipper.SourceGenerated.Subclasses.Skeleton;
using AssetRipper.SourceGenerated.Subclasses.SkeletonPose;
using AssetRipper.SourceGenerated.Subclasses.Vector3Float;
using AssetRipper.SourceGenerated.Subclasses.Vector4Float;
using AssetRipper.SourceGenerated.Subclasses.Xform;

namespace Ruri.RipperHook.Humanoid;

public sealed class AvatarMuscleReferential
{
    private const int BodySlots = 25;
    private const int FingerSlotsPerHand = 15;
    private const int LeftFingerBase = BodySlots;
    private const int RightFingerBase = BodySlots + FingerSlotsPerHand;
    private const int TotalSlots = BodySlots + 2 * FingerSlotsPerHand;

    private static readonly Dictionary<string, (int Slot, int Axis)> MuscleDofTable = BuildMuscleDofTable();

    private static readonly Dictionary<BoneType, (BoneType Neighbor, float Weight)[]> MassCenterFormula = new()
    {
        [BoneType.Hips] = new[] { (BoneType.LeftUpperLeg, 1f / 3f), (BoneType.RightUpperLeg, 1f / 3f), (BoneType.Spine, 1f / 3f) },
        [BoneType.LeftUpperLeg] = new[] { (BoneType.LeftUpperLeg, 0.5f), (BoneType.LeftLowerLeg, 0.5f) },
        [BoneType.RightUpperLeg] = new[] { (BoneType.RightUpperLeg, 0.5f), (BoneType.RightLowerLeg, 0.5f) },
        [BoneType.LeftLowerLeg] = new[] { (BoneType.LeftLowerLeg, 0.5f), (BoneType.LeftFoot, 0.5f) },
        [BoneType.RightLowerLeg] = new[] { (BoneType.RightLowerLeg, 0.5f), (BoneType.RightFoot, 0.5f) },
        [BoneType.Spine] = new[] { (BoneType.Spine, 0.5f), (BoneType.Chest, 0.5f) },
        [BoneType.Chest] = new[] { (BoneType.Chest, 0.5f), (BoneType.UpperChest, 0.5f) },
        [BoneType.UpperChest] = new[] { (BoneType.UpperChest, 0.25f), (BoneType.Neck, 0.25f), (BoneType.LeftShoulder, 0.25f), (BoneType.RightShoulder, 0.25f) },
        [BoneType.Neck] = new[] { (BoneType.Neck, 0.5f), (BoneType.Head, 0.5f) },
        [BoneType.LeftShoulder] = new[] { (BoneType.LeftShoulder, 0.5f), (BoneType.LeftUpperArm, 0.5f) },
        [BoneType.RightShoulder] = new[] { (BoneType.RightShoulder, 0.5f), (BoneType.RightUpperArm, 0.5f) },
        [BoneType.LeftUpperArm] = new[] { (BoneType.LeftUpperArm, 0.5f), (BoneType.LeftLowerArm, 0.5f) },
        [BoneType.RightUpperArm] = new[] { (BoneType.RightUpperArm, 0.5f), (BoneType.RightLowerArm, 0.5f) },
        [BoneType.LeftLowerArm] = new[] { (BoneType.LeftLowerArm, 0.5f), (BoneType.LeftHand, 0.5f) },
        [BoneType.RightLowerArm] = new[] { (BoneType.RightLowerArm, 0.5f), (BoneType.RightHand, 0.5f) },
    };

    private static readonly (BoneType Parent, BoneType Child, Func<AvatarMuscleReferential, float> Factor)[] TwistSolvePairs =
    {
        (BoneType.LeftLowerArm, BoneType.LeftHand, r => r._foreArmTwist),
        (BoneType.LeftUpperArm, BoneType.LeftLowerArm, r => r._armTwist),
        (BoneType.RightLowerArm, BoneType.RightHand, r => r._foreArmTwist),
        (BoneType.RightUpperArm, BoneType.RightLowerArm, r => r._armTwist),
        (BoneType.LeftLowerLeg, BoneType.LeftFoot, r => r._legTwist),
        (BoneType.LeftUpperLeg, BoneType.LeftLowerLeg, r => r._upperLegTwist),
        (BoneType.RightLowerLeg, BoneType.RightFoot, r => r._legTwist),
        (BoneType.RightUpperLeg, BoneType.RightLowerLeg, r => r._upperLegTwist),
    };

    private readonly MuscleBone?[] _bones = new MuscleBone?[TotalSlots];
    private readonly List<MuscleBone> _drivenBones = new();

    private float[] _humanBoneMass = Array.Empty<float>();
    private Quaternion _qRest = Quaternion.Identity;
    private int[] _nodeParent = Array.Empty<int>();
    private Vector3[] _nodeRestT = Array.Empty<Vector3>();
    private Quaternion[] _nodeRestQ = Array.Empty<Quaternion>();

    private int[] _nodeToSlot = Array.Empty<int>();

    private Vector3[] _fkPos = Array.Empty<Vector3>();
    private Quaternion[] _fkRot = Array.Empty<Quaternion>();
    private bool[] _fkDone = Array.Empty<bool>();
    private readonly Quaternion[] _frameQuats = new Quaternion[TotalSlots];
    private readonly bool[] _frameDriven = new bool[TotalSlots];

    private float _armTwist = 0.5f;
    private float _foreArmTwist = 0.5f;
    private float _upperLegTwist = 0.5f;
    private float _legTwist = 0.5f;

    public MuscleBone? Hips { get; private init; }

    public IReadOnlyList<MuscleBone> DrivenBones => _drivenBones;

    private AvatarMuscleReferential(MuscleBone? hips) => Hips = hips;

    public static bool IsMuscleAttribute(string attribute) => MuscleDofTable.ContainsKey(attribute);

    public static bool IsRootAttribute(string attribute)
    {
        int dot = attribute.IndexOf('.');
        ReadOnlySpan<char> head = dot < 0 ? attribute : attribute.AsSpan(0, dot);
        return head is "RootT" or "RootQ" or "MotionT" or "MotionQ";
    }

    public static AvatarMuscleReferential? TryCreate(IAvatar avatar)
    {
        IHuman human = avatar.Avatar.Human.Data;
        ISkeleton skeleton = human.Skeleton.Data;
        if (skeleton.Node.Count == 0 || human.HumanBoneIndex.Count == 0)
        {
            return null;
        }

        MuscleBone?[] bones = new MuscleBone?[TotalSlots];
        List<MuscleBone> driven = new();

        for (int slot = 0; slot < BodySlots && slot < human.HumanBoneIndex.Count; slot++)
        {
            AddBone(bones, driven, avatar, skeleton, slot, human.HumanBoneIndex[slot]);
        }
        AddHandBones(bones, driven, avatar, skeleton, human.LeftHand.Data, LeftFingerBase);
        AddHandBones(bones, driven, avatar, skeleton, human.RightHand.Data, RightFingerBase);

        if (driven.Count == 0)
        {
            return null;
        }

        foreach ((string muscle, (int slot, int axis)) in MuscleDofTable)
        {
            bones[slot]?.SetDofMuscle(axis, muscle);
        }

        MuscleBone? hips = bones[(int)BoneType.Hips];
        if (hips is not null)
        {
            hips.IsHips = true;
        }

        AvatarMuscleReferential referential = new(hips);
        bones.CopyTo(referential._bones, 0);
        referential._drivenBones.AddRange(driven);

        referential._humanBoneMass = new float[human.HumanBoneMass.Count];
        for (int i = 0; i < human.HumanBoneMass.Count; i++)
        {
            referential._humanBoneMass[i] = human.HumanBoneMass[i];
        }
        referential._qRest = ToQuaternion(human.RootX.Q);
        referential._armTwist = human.ArmTwist;
        referential._foreArmTwist = human.ForeArmTwist;
        referential._upperLegTwist = human.UpperLegTwist;
        referential._legTwist = human.LegTwist;

        int nodeCount = skeleton.Node.Count;
        referential._nodeParent = new int[nodeCount];
        referential._nodeRestT = new Vector3[nodeCount];
        referential._nodeRestQ = new Quaternion[nodeCount];
        referential._nodeToSlot = new int[nodeCount];
        Array.Fill(referential._nodeToSlot, -1);
        for (int slot = 0; slot < BodySlots; slot++)
        {
            if (bones[slot] is { } bodyBone && (uint)bodyBone.NodeIndex < (uint)nodeCount)
            {
                referential._nodeToSlot[bodyBone.NodeIndex] = slot;
            }
        }
        referential._fkPos = new Vector3[nodeCount];
        referential._fkRot = new Quaternion[nodeCount];
        referential._fkDone = new bool[nodeCount];
        ISkeletonPose skeletonPose = human.SkeletonPose.Data;
        for (int i = 0; i < nodeCount; i++)
        {
            referential._nodeParent[i] = skeleton.Node[i].ParentId;
            if (i < skeletonPose.X.Count)
            {
                IXform xform = skeletonPose.X[i];
                referential._nodeRestT[i] = ToXformTranslation(xform);
                referential._nodeRestQ[i] = ToQuaternion(xform.Q);
            }
            else
            {
                referential._nodeRestQ[i] = Quaternion.Identity;
            }
        }

        return referential;
    }

    private static void AddHandBones(MuscleBone?[] bones, List<MuscleBone> driven, IAvatar avatar,
        ISkeleton skeleton, IHand hand, int slotBase)
    {
        for (int i = 0; i < FingerSlotsPerHand && i < hand.HandBoneIndex.Count; i++)
        {
            AddBone(bones, driven, avatar, skeleton, slotBase + i, hand.HandBoneIndex[i]);
        }
    }

    private static void AddBone(MuscleBone?[] bones, List<MuscleBone> driven, IAvatar avatar,
        ISkeleton skeleton, int slot, int nodeIndex)
    {
        if (nodeIndex < 0 || nodeIndex >= skeleton.Node.Count || nodeIndex >= skeleton.ID.Count)
        {
            return;
        }
        if (!avatar.TOS.TryGetValue(skeleton.ID[nodeIndex], out Utf8String? path) || path.IsEmpty)
        {
            return;
        }

        int axesId = skeleton.Node[nodeIndex].AxesId;
        IAxes? axes = axesId >= 0 && axesId < skeleton.AxesArray.Count ? skeleton.AxesArray[axesId] : null;
        if (axes is null && slot != (int)BoneType.Hips)
        {
            return;
        }

        MuscleBone bone = axes is null
            ? new MuscleBone(path.String, nodeIndex, slot, Quaternion.Identity, Quaternion.Identity, Vector3.One, Vector3.Zero, Vector3.Zero)
            : new MuscleBone(
                path.String,
                nodeIndex,
                slot,
                ToQuaternion(axes.PreQ),
                ToQuaternion(axes.PostQ),
                GetSgn(axes),
                GetLimit(axes, min: true),
                GetLimit(axes, min: false));
        bones[slot] = bone;
        driven.Add(bone);
    }

    public static Quaternion LocalRotation(MuscleBone bone, ReadOnlySpan<float> frame)
        => ComposeFromAngles(bone, ComputeAngles(bone, frame));

    private static (float X, float Y, float Z) ComputeAngles(MuscleBone bone, ReadOnlySpan<float> frame)
    {
        float angleX = 0f, angleY = 0f, angleZ = 0f;
        int[] channels = bone.DofChannel;
        for (int dof = 0; dof < 3; dof++)
        {
            int column = channels[dof];
            if (column < 0)
            {
                continue;
            }
            float angle = MuscleAngle(frame[column], GetComponent(bone.Sgn, dof),
                GetComponent(bone.LimitMin, dof), GetComponent(bone.LimitMax, dof));
            switch (dof)
            {
                case 0: angleX = angle; break;
                case 1: angleY = angle; break;
                default: angleZ = angle; break;
            }
        }
        return (angleX, angleY, angleZ);
    }

    private static Quaternion ComposeFromAngles(MuscleBone bone, (float X, float Y, float Z) angles)
    {
        Quaternion swingTwist = SwingTwist(angles.X, angles.Y, angles.Z);
        return bone.PreQ * swingTwist * Quaternion.Inverse(bone.PostQ);
    }

    public void BodyLocalQuats(ReadOnlySpan<float> frame, Span<Quaternion> quats, Span<bool> driven)
    {
        driven.Clear();
        for (int slot = 0; slot < TotalSlots; slot++)
        {
            if (slot == (int)BoneType.Hips || _bones[slot] is not { } bone)
            {
                continue;
            }
            quats[slot] = LocalRotation(bone, frame);
            driven[slot] = true;
        }

        foreach ((BoneType parent, BoneType child, Func<AvatarMuscleReferential, float> factorOf) in TwistSolvePairs)
        {
            if (_bones[(int)parent] is not { } parentBone || !driven[(int)parent] || !driven[(int)child])
            {
                continue;
            }
            Quaternion parentOld = quats[(int)parent];
            Quaternion childOld = quats[(int)child];
            (float x, float y, float z) = ComputeAngles(parentBone, frame);
            Quaternion parentNew = ComposeFromAngles(parentBone, (x * factorOf(this), y, z));
            Quaternion delta = Quaternion.Normalize(Quaternion.Inverse(parentOld) * parentNew);
            quats[(int)parent] = parentNew;
            quats[(int)child] = Quaternion.Normalize(Quaternion.Inverse(delta) * childOld);
        }
    }

    public static int SlotCount => TotalSlots;

    public RootChannelPlan BindClip(IReadOnlyDictionary<string, int> channelIndex)
    {
        foreach (MuscleBone bone in _drivenBones)
        {
            bone.BindDofChannels(channelIndex);
        }
        return RootChannelPlan.Bind(channelIndex);
    }

    public (Vector3 Position, Quaternion Rotation, (Vector3 Position, Quaternion Rotation) Motion)? BodyTransform(
        ReadOnlySpan<float> frame, in RootChannelPlan root, ReadOnlySpan<Quaternion> bodyQuats,
        ReadOnlySpan<bool> driven, bool keepPositionXz = true, bool keepPositionY = true,
        bool keepOrientation = true)
    {
        if (!root.HasRootTranslation)
        {
            return null;
        }
        Vector3 fullT = new(RootChannelPlan.Read(frame, root.RootTx),
            RootChannelPlan.Read(frame, root.RootTy),
            RootChannelPlan.Read(frame, root.RootTz));
        Quaternion fullQ = root.RootQw < 0
            ? Quaternion.Identity
            : Quaternion.Normalize(new Quaternion(
                RootChannelPlan.Read(frame, root.RootQx),
                RootChannelPlan.Read(frame, root.RootQy),
                RootChannelPlan.Read(frame, root.RootQz),
                frame[root.RootQw]));

        Vector3 rootTSimple;
        Vector3 motionT;
        Quaternion rootQ;
        Quaternion motionQ;
        if (root.HasMotion)
        {
            motionT = new Vector3(RootChannelPlan.Read(frame, root.MotionTx),
                RootChannelPlan.Read(frame, root.MotionTy),
                RootChannelPlan.Read(frame, root.MotionTz));
            motionQ = root.MotionQw < 0
                ? Quaternion.Identity
                : Quaternion.Normalize(new Quaternion(
                    RootChannelPlan.Read(frame, root.MotionQx),
                    RootChannelPlan.Read(frame, root.MotionQy),
                    RootChannelPlan.Read(frame, root.MotionQz),
                    frame[root.MotionQw]));
            rootTSimple = fullT - motionT;
            rootQ = Quaternion.Normalize(Quaternion.Inverse(motionQ) * fullQ);
        }
        else if (keepPositionXz && keepPositionY && keepOrientation)
        {
            rootTSimple = fullT;
            rootQ = fullQ;
            motionT = Vector3.Zero;
            motionQ = Quaternion.Identity;
        }
        else
        {
            float rootX = keepPositionXz ? fullT.X : 0f;
            float rootY = keepPositionY ? fullT.Y : 0f;
            float rootZ = keepPositionXz ? fullT.Z : 0f;
            rootTSimple = new Vector3(rootX, rootY, rootZ);
            motionT = fullT - rootTSimple;
            if (keepOrientation)
            {
                rootQ = fullQ;
                motionQ = Quaternion.Identity;
            }
            else
            {
                Quaternion twistY = Quaternion.Normalize(new Quaternion(0f, fullQ.Y, 0f, fullQ.W));
                rootQ = Quaternion.Normalize(Quaternion.Inverse(twistY) * fullQ);
                motionQ = twistY;
            }
        }
        Vector3 rootT = Vector3.Transform(rootTSimple, Quaternion.Inverse(motionQ));
        (Vector3, Quaternion) motion = (motionT, motionQ);

        (Vector3 Pos, Quaternion Rot)?[] fk = ProvisionalFk(bodyQuats, driven);
        if (fk[(int)BoneType.LeftUpperArm] is null || fk[(int)BoneType.RightUpperArm] is null
            || fk[(int)BoneType.LeftUpperLeg] is null || fk[(int)BoneType.RightUpperLeg] is null)
        {
            return (rootT, rootQ, motion);
        }

        Quaternion qProvisional = ComputeOrientation(fk);
        Vector3 massCenterProvisional = ComputeMassCenter(fk);

        Quaternion rHips = Quaternion.Normalize(rootQ * _qRest * Quaternion.Inverse(qProvisional));
        Vector3 tHips = rootT - Vector3.Transform(massCenterProvisional, rHips);
        return (tHips, rHips, motion);
    }

    private (Vector3 Pos, Quaternion Rot)?[] ProvisionalFk(ReadOnlySpan<Quaternion> bodyQuats,
        ReadOnlySpan<bool> driven)
    {
        bodyQuats.CopyTo(_frameQuats);
        driven.CopyTo(_frameDriven);
        Quaternion[] frameQuats = _frameQuats;
        bool[] frameDriven = _frameDriven;

        int hipsNode = Hips?.NodeIndex ?? -1;
        int[] nodeToSlot = _nodeToSlot;
        Vector3[] fkPos = _fkPos;
        Quaternion[] fkRot = _fkRot;
        bool[] fkDone = _fkDone;
        Array.Clear(fkDone);

        (Vector3, Quaternion) Solve(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex == hipsNode)
            {
                return (Vector3.Zero, Quaternion.Identity);
            }
            if (fkDone[nodeIndex])
            {
                return (fkPos[nodeIndex], fkRot[nodeIndex]);
            }
            int parentIndex = nodeIndex < _nodeParent.Length ? _nodeParent[nodeIndex] : -1;
            (Vector3 parentPos, Quaternion parentRot) = Solve(parentIndex);
            int slot = nodeToSlot[nodeIndex];
            Quaternion rot = slot >= 0 && slot != (int)BoneType.Hips && frameDriven[slot]
                ? frameQuats[slot]
                : _nodeRestQ[nodeIndex];
            Vector3 worldPos = parentPos + Vector3.Transform(_nodeRestT[nodeIndex], parentRot);
            Quaternion worldRot = Quaternion.Normalize(parentRot * rot);
            fkPos[nodeIndex] = worldPos;
            fkRot[nodeIndex] = worldRot;
            fkDone[nodeIndex] = true;
            return (worldPos, worldRot);
        }

        (Vector3 Pos, Quaternion Rot)?[] fk = new (Vector3, Quaternion)?[BodySlots];
        for (int slot = 0; slot < BodySlots; slot++)
        {
            if (_bones[slot] is { } b)
            {
                fk[slot] = Solve(b.NodeIndex);
            }
        }
        return fk;
    }

    private static Vector3 MassCenterOf(BoneType type, (Vector3 Pos, Quaternion Rot)?[] fk)
    {
        if (!MassCenterFormula.TryGetValue(type, out (BoneType Neighbor, float Weight)[]? formula))
        {
            return fk[(int)type]!.Value.Pos;
        }
        Vector3 total = Vector3.Zero;
        foreach ((BoneType neighbor, float weight) in formula)
        {
            total += fk[(int)neighbor]!.Value.Pos * weight;
        }
        return total;
    }

    private Vector3 ComputeMassCenter((Vector3 Pos, Quaternion Rot)?[] fk)
    {
        Vector3 total = Vector3.Zero;
        float totalMass = 0f;
        for (int slot = 0; slot < BodySlots; slot++)
        {
            if (fk[slot] is null || slot >= _humanBoneMass.Length)
            {
                continue;
            }
            float mass = _humanBoneMass[slot];
            if (mass < 0f)
            {
                continue;
            }
            total += MassCenterOf((BoneType)slot, fk) * mass;
            totalMass += mass;
        }
        return total * (1f / totalMass);
    }

    private static Quaternion ComputeOrientation((Vector3 Pos, Quaternion Rot)?[] fk)
    {
        Vector3 leftUpperArm = fk[(int)BoneType.LeftUpperArm]!.Value.Pos;
        Vector3 rightUpperArm = fk[(int)BoneType.RightUpperArm]!.Value.Pos;
        Vector3 leftUpperLeg = fk[(int)BoneType.LeftUpperLeg]!.Value.Pos;
        Vector3 rightUpperLeg = fk[(int)BoneType.RightUpperLeg]!.Value.Pos;

        Vector3 shoulderCenter = (rightUpperArm + leftUpperArm) * 0.5f;
        Vector3 hipCenter = (leftUpperLeg + rightUpperLeg) * 0.5f;
        Vector3 up = Vector3.Normalize(shoulderCenter - hipCenter);
        Vector3 right = Vector3.Normalize((rightUpperArm - leftUpperArm) + (rightUpperLeg - leftUpperLeg));
        Vector3 forward = Vector3.Normalize(Vector3.Cross(right, up));
        right = Vector3.Cross(up, forward);
        return MatrixToQuaternion(right, up, forward);
    }

    private static Quaternion MatrixToQuaternion(Vector3 colX, Vector3 colY, Vector3 colZ)
    {
        float m00 = colX.X, m10 = colX.Y, m20 = colX.Z;
        float m01 = colY.X, m11 = colY.Y, m21 = colY.Z;
        float m02 = colZ.X, m12 = colZ.Y, m22 = colZ.Z;
        float trace = m00 + m11 + m22;
        float w, x, y, z;
        if (trace > 0f)
        {
            float s = 0.5f / MathF.Sqrt(trace + 1f);
            w = 0.25f / s;
            x = (m21 - m12) * s;
            y = (m02 - m20) * s;
            z = (m10 - m01) * s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = 2f * MathF.Sqrt(1f + m00 - m11 - m22);
            w = (m21 - m12) / s;
            x = 0.25f * s;
            y = (m01 + m10) / s;
            z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            float s = 2f * MathF.Sqrt(1f + m11 - m00 - m22);
            w = (m02 - m20) / s;
            x = (m01 + m10) / s;
            y = 0.25f * s;
            z = (m12 + m21) / s;
        }
        else
        {
            float s = 2f * MathF.Sqrt(1f + m22 - m00 - m11);
            w = (m10 - m01) / s;
            x = (m02 + m20) / s;
            y = (m12 + m21) / s;
            z = 0.25f * s;
        }
        return Quaternion.Normalize(new Quaternion(x, y, z, w));
    }

    private static float MuscleAngle(float muscle, float sgn, float limitMin, float limitMax)
    {
        float scale = muscle >= 0f ? limitMax : -limitMin;
        return sgn * muscle * scale;
    }

    private static Quaternion SwingTwist(float angleX, float angleY, float angleZ)
    {
        float tx = MathF.Tan(angleX * 0.5f);
        float ty = MathF.Tan(angleY * 0.5f);
        float tz = MathF.Tan(angleZ * 0.5f);
        return Quaternion.Normalize(new Quaternion(tx, ty + tx * tz, tz - tx * ty, 1f));
    }

    private static float GetComponent(Vector3 v, int index) => index switch { 0 => v.X, 1 => v.Y, _ => v.Z };

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

    private static Dictionary<string, (int Slot, int Axis)> BuildMuscleDofTable()
    {
        Dictionary<string, (int, int)> table = new(95, StringComparer.Ordinal)
        {
            ["Spine Front-Back"] = ((int)BoneType.Spine, 2),
            ["Spine Left-Right"] = ((int)BoneType.Spine, 1),
            ["Spine Twist Left-Right"] = ((int)BoneType.Spine, 0),
            ["Chest Front-Back"] = ((int)BoneType.Chest, 2),
            ["Chest Left-Right"] = ((int)BoneType.Chest, 1),
            ["Chest Twist Left-Right"] = ((int)BoneType.Chest, 0),
            ["UpperChest Front-Back"] = ((int)BoneType.UpperChest, 2),
            ["UpperChest Left-Right"] = ((int)BoneType.UpperChest, 1),
            ["UpperChest Twist Left-Right"] = ((int)BoneType.UpperChest, 0),
            ["Neck Nod Down-Up"] = ((int)BoneType.Neck, 2),
            ["Neck Tilt Left-Right"] = ((int)BoneType.Neck, 1),
            ["Neck Turn Left-Right"] = ((int)BoneType.Neck, 0),
            ["Head Nod Down-Up"] = ((int)BoneType.Head, 2),
            ["Head Tilt Left-Right"] = ((int)BoneType.Head, 1),
            ["Head Turn Left-Right"] = ((int)BoneType.Head, 0),
            ["Left Eye Down-Up"] = ((int)BoneType.LeftEye, 2),
            ["Left Eye In-Out"] = ((int)BoneType.LeftEye, 1),
            ["Right Eye Down-Up"] = ((int)BoneType.RightEye, 2),
            ["Right Eye In-Out"] = ((int)BoneType.RightEye, 1),
            ["Jaw Close"] = ((int)BoneType.Jaw, 2),
            ["Jaw Left-Right"] = ((int)BoneType.Jaw, 1),
            ["Left Upper Leg Front-Back"] = ((int)BoneType.LeftUpperLeg, 2),
            ["Left Upper Leg In-Out"] = ((int)BoneType.LeftUpperLeg, 1),
            ["Left Upper Leg Twist In-Out"] = ((int)BoneType.LeftUpperLeg, 0),
            ["Left Lower Leg Stretch"] = ((int)BoneType.LeftLowerLeg, 2),
            ["Left Lower Leg Twist In-Out"] = ((int)BoneType.LeftLowerLeg, 0),
            ["Left Foot Up-Down"] = ((int)BoneType.LeftFoot, 2),
            ["Left Foot Twist In-Out"] = ((int)BoneType.LeftFoot, 1),
            ["Left Toes Up-Down"] = ((int)BoneType.LeftToes, 1),
            ["Right Upper Leg Front-Back"] = ((int)BoneType.RightUpperLeg, 2),
            ["Right Upper Leg In-Out"] = ((int)BoneType.RightUpperLeg, 1),
            ["Right Upper Leg Twist In-Out"] = ((int)BoneType.RightUpperLeg, 0),
            ["Right Lower Leg Stretch"] = ((int)BoneType.RightLowerLeg, 2),
            ["Right Lower Leg Twist In-Out"] = ((int)BoneType.RightLowerLeg, 0),
            ["Right Foot Up-Down"] = ((int)BoneType.RightFoot, 2),
            ["Right Foot Twist In-Out"] = ((int)BoneType.RightFoot, 1),
            ["Right Toes Up-Down"] = ((int)BoneType.RightToes, 1),
            ["Left Shoulder Down-Up"] = ((int)BoneType.LeftShoulder, 2),
            ["Left Shoulder Front-Back"] = ((int)BoneType.LeftShoulder, 1),
            ["Left Arm Down-Up"] = ((int)BoneType.LeftUpperArm, 2),
            ["Left Arm Front-Back"] = ((int)BoneType.LeftUpperArm, 1),
            ["Left Arm Twist In-Out"] = ((int)BoneType.LeftUpperArm, 0),
            ["Left Forearm Stretch"] = ((int)BoneType.LeftLowerArm, 2),
            ["Left Forearm Twist In-Out"] = ((int)BoneType.LeftLowerArm, 0),
            ["Left Hand Down-Up"] = ((int)BoneType.LeftHand, 2),
            ["Left Hand In-Out"] = ((int)BoneType.LeftHand, 1),
            ["Right Shoulder Down-Up"] = ((int)BoneType.RightShoulder, 2),
            ["Right Shoulder Front-Back"] = ((int)BoneType.RightShoulder, 1),
            ["Right Arm Down-Up"] = ((int)BoneType.RightUpperArm, 2),
            ["Right Arm Front-Back"] = ((int)BoneType.RightUpperArm, 1),
            ["Right Arm Twist In-Out"] = ((int)BoneType.RightUpperArm, 0),
            ["Right Forearm Stretch"] = ((int)BoneType.RightLowerArm, 2),
            ["Right Forearm Twist In-Out"] = ((int)BoneType.RightLowerArm, 0),
            ["Right Hand Down-Up"] = ((int)BoneType.RightHand, 2),
            ["Right Hand In-Out"] = ((int)BoneType.RightHand, 1),
        };

        for (int arm = 0; arm < 2; arm++)
        {
            string hand = arm == 0 ? "LeftHand" : "RightHand";
            int slotBase = arm == 0 ? LeftFingerBase : RightFingerBase;
            for (FingerType finger = FingerType.Thumb; finger < FingerType.Last; finger++)
            {
                int fingerBase = slotBase + (int)finger * 3;
                string name = finger.ToAttributeString();
                table[$"{hand}.{name}.1 Stretched"] = (fingerBase, 2);
                table[$"{hand}.{name}.Spread"] = (fingerBase, 1);
                table[$"{hand}.{name}.2 Stretched"] = (fingerBase + 1, 2);
                table[$"{hand}.{name}.3 Stretched"] = (fingerBase + 2, 2);
            }
        }
        return table;
    }
}
