using System.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.Bones;
using AssetRipper.SourceGenerated.Subclasses.Axes;
using AssetRipper.SourceGenerated.Subclasses.Hand;
using AssetRipper.SourceGenerated.Subclasses.Human;
using AssetRipper.SourceGenerated.Subclasses.Skeleton;
using AssetRipper.SourceGenerated.Subclasses.Vector3Float;
using AssetRipper.SourceGenerated.Subclasses.Vector4Float;

namespace Ruri.RipperHook.GlbExporter;

/// <summary>
/// Unity humanoid muscle referential extracted from an Avatar: per driven human bone the Axes
/// (preQ/postQ/sgn/limit) and the TOS transform path.
/// Muscle math is a 1:1 port of the validated Blender-side solver
/// (RuriRipperImporter/humanoid_retarget.py, ``_axes_local``): a normalized muscle value in
/// [-1,1] scales to a radian angle via the per-axis limit and sign, the three axes compose as
/// swing(Y,Z) * twist(X), and ``preQ * swingTwist * postQ^-1`` (see ``LocalRotation``) IS the
/// bone's FULL absolute local rotation for the frame -- not a delta, and not composed with or
/// divided by any rest quaternion; see ``LocalRotation`` for the two wrong intermediate
/// revisions of this formula (a per-bone ``normRest`` table validated against contaminated
/// ground truth, then a per-bone character-rest division that algebraically canceled to a
/// no-op once wired into the real call site) and why each one's own validation didn't catch it.
/// The hips are driven by RootT/RootQ instead (body pose in the animation-root frame; MotionT
/// carries the extracted root motion) -- <b>this hips path is known-wrong as of 2026-07-10</b>
/// (see FRAMEWORK.md §13 "根运动坑"): Unity ground truth shows the Hips bone's own rest local
/// rotation is exact identity and its animated local delta is small, but RootT/RootQ read
/// directly as "hips local delta" produce a value that does not match Unity's actual Hips local
/// transform at all -- these two channels most likely belong on the Animator's own GameObject
/// (one level above the skeleton), not on the Hips bone itself. Not yet fixed in either
/// language; mirrored here 1:1 with the still-broken Python behavior per the port's own "match
/// Python's current state, don't silently diverge" rule.
/// </summary>
public sealed class AvatarMuscleReferential
{
    private const int BodySlots = 25;                       // BoneType.Hips .. BoneType.Jaw
    private const int FingerSlotsPerHand = 15;              // 5 fingers x 3 phalanges
    private const int LeftFingerBase = BodySlots;
    private const int RightFingerBase = BodySlots + FingerSlotsPerHand;
    private const int TotalSlots = BodySlots + 2 * FingerSlotsPerHand;

    /// <summary>muscle attribute string -> (bone slot, dof axis 0=X twist / 1=Y / 2=Z)。</summary>
    private static readonly Dictionary<string, (int Slot, int Axis)> MuscleDofTable = BuildMuscleDofTable();

    /// <summary>
    /// "Forearm Twist In-Out" needs its angle negated relative to what its own sgn already
    /// encodes -- validated bilaterally (Left AND Right forearm both need it, average error
    /// ~19deg -> ~8deg across 11 frames on top of the formula in ``LocalRotation``), unlike the
    /// analogous "Lower Leg Twist In-Out" which needs no such flip. Root cause not isolated
    /// (ruled out: swing composition order, preQ/postQ swap); kept as a targeted, ground-truth
    /// validated correction on the specific muscle (humanoid_retarget.py's
    /// ``_TWIST_SIGN_FLIP``, same set).
    /// </summary>
    private static readonly HashSet<string> TwistSignFlipMuscles = new(StringComparer.Ordinal)
    {
        "Left Forearm Twist In-Out", "Right Forearm Twist In-Out",
    };

    private readonly MuscleBone?[] _bones = new MuscleBone?[TotalSlots];
    private readonly List<MuscleBone> _drivenBones = new();

    public MuscleBone? Hips { get; private init; }

    /// <summary>Every bone this referential drives (hips included).</summary>
    public IReadOnlyList<MuscleBone> DrivenBones => _drivenBones;

    private AvatarMuscleReferential(MuscleBone? hips) => Hips = hips;

    public static bool IsMuscleAttribute(string attribute) => MuscleDofTable.ContainsKey(attribute);

    /// <summary>RootT/RootQ (body pose) and MotionT/MotionQ (root motion) channels.</summary>
    public static bool IsRootAttribute(string attribute)
    {
        int dot = attribute.IndexOf('.');
        ReadOnlySpan<char> head = dot < 0 ? attribute : attribute.AsSpan(0, dot);
        return head is "RootT" or "RootQ" or "MotionT" or "MotionQ";
    }

    /// <summary>
    /// Build the referential from an Avatar. Returns null when the avatar carries no human
    /// (generic rig) or the human skeleton is degenerate.
    /// </summary>
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
        // Hips 没有 Axes(不被肌肉驱动)但仍需 TOS 路径与 rest 来接 RootT/RootQ。
        if (axes is null && slot != (int)BoneType.Hips)
        {
            return;
        }

        MuscleBone bone = axes is null
            ? new MuscleBone(path.String, Quaternion.Identity, Quaternion.Identity, Vector3.One, Vector3.Zero, Vector3.Zero)
            : new MuscleBone(
                path.String,
                ToQuaternion(axes.PreQ),
                ToQuaternion(axes.PostQ),
                GetSgn(axes),
                GetLimit(axes, min: true),
                GetLimit(axes, min: false));
        bones[slot] = bone;
        driven.Add(bone);
    }

    /// <summary>
    /// The bone's FULL absolute local rotation for this frame:
    /// preQ * swingTwist(angles) * inv(postQ) (humanoid_retarget.py's ``_axes_local``, same
    /// formula). This is NOT a delta and is NOT identity at muscle=0 -- it already IS the
    /// answer the caller wants in place of the bone's rest local rotation, with no further
    /// composition or division by any rest quaternion (character's own, or the avatar's
    /// ``m_SkeletonPose`` "normalized rest") needed or correct. preQ and postQ are NOT
    /// interchangeable -- on a real rig they differ by 60-280+ degrees per bone, so the
    /// sandwich is asymmetric, not a similarity-transform conjugation by postQ alone.
    ///
    /// Verified against live Unity Mecanim ground truth (Editor
    /// AnimationMode.SampleAnimationClip + Animator.GetBoneTransform on a real humanoid
    /// Avatar+clip) across 18 body bones at 11 frames each (187 samples): grand-average error
    /// 4.93 degrees with this exact formula used directly, no per-bone table, no rest division
    /// of any kind. See the class-level doc comment for two earlier, wrong versions of this
    /// method and why each one's own validation was invalid or pointless.
    /// </summary>
    public static Quaternion LocalRotation(MuscleBone bone, Func<string, float?> muscleLookup)
    {
        float angleX = 0f, angleY = 0f, angleZ = 0f;
        for (int dof = 0; dof < 3; dof++)
        {
            string? muscle = bone.GetDofMuscle(dof);
            if (muscle is null)
            {
                continue;
            }
            float? value = muscleLookup(muscle);
            if (value is null)
            {
                continue;
            }
            float angle = MuscleAngle(value.Value, GetComponent(bone.Sgn, dof),
                GetComponent(bone.LimitMin, dof), GetComponent(bone.LimitMax, dof));
            switch (dof)
            {
                case 0: angleX = angle; break;
                case 1: angleY = angle; break;
                default: angleZ = angle; break;
            }
        }
        if (bone.GetDofMuscle(0) is string twistMuscle && TwistSignFlipMuscles.Contains(twistMuscle))
        {
            angleX = -angleX;
        }
        Quaternion swingTwist = SwingTwist(angleX, angleY, angleZ);
        return bone.PreQ * swingTwist * Quaternion.Inverse(bone.PostQ);
    }

    /// <summary>
    /// The hips' local position+rotation from the clip's Root curves, or null when the clip
    /// carries no root translation channels (humanoid_retarget.py:304-331 `body_transform`).
    /// RootT is the body position in the animation-root frame; MotionT is the extracted root
    /// motion, so the hips' root-local offset is RootT - MotionT and the global flight belongs
    /// to the root node (baked separately from MotionT/MotionQ).
    /// </summary>
    public static (Vector3 Position, Quaternion Rotation)? BodyTransform(Func<string, float?> muscleLookup)
    {
        float? tx = muscleLookup("RootT.x");
        float? ty = muscleLookup("RootT.y");
        float? tz = muscleLookup("RootT.z");
        if (tx is null && ty is null && tz is null)
        {
            return null;
        }
        float mx = muscleLookup("MotionT.x") ?? 0f;
        float my = muscleLookup("MotionT.y") ?? 0f;
        float mz = muscleLookup("MotionT.z") ?? 0f;
        Vector3 position = new((tx ?? 0f) - mx, (ty ?? 0f) - my, (tz ?? 0f) - mz);

        float? qw = muscleLookup("RootQ.w");
        Quaternion rotation = qw is null
            ? Quaternion.Identity
            : Quaternion.Normalize(new Quaternion(
                muscleLookup("RootQ.x") ?? 0f,
                muscleLookup("RootQ.y") ?? 0f,
                muscleLookup("RootQ.z") ?? 0f,
                qw.Value));
        return (position, rotation);
    }

    /// <summary>
    /// Map a normalized muscle in [-1,1] to a radian angle via the per-axis limit and sign:
    /// -1 -> min, 0 -> 0, +1 -> max (humanoid_retarget.py:188-192 `_muscle_angle`).
    /// </summary>
    private static float MuscleAngle(float muscle, float sgn, float limitMin, float limitMax)
    {
        float scale = muscle >= 0f ? limitMax : -limitMin;
        return sgn * muscle * scale;
    }

    /// <summary>
    /// Compose three per-axis angles into a quaternion as swing(Y,Z) * twist(X)
    /// (humanoid_retarget.py:195-204 `_swing_twist`).
    /// </summary>
    private static Quaternion SwingTwist(float angleX, float angleY, float angleZ)
    {
        Quaternion twist = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angleX);
        Vector3 swingAxis = new(0f, angleY, angleZ);
        float swingAngle = swingAxis.Length();
        if (swingAngle <= 1e-9f)
        {
            return twist;
        }
        Quaternion swing = Quaternion.CreateFromAxisAngle(swingAxis * (1f / swingAngle), swingAngle);
        return swing * twist;
    }

    private static float GetComponent(Vector3 v, int index) => index switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static Quaternion ToQuaternion(IVector4Float v) => new(v.X, v.Y, v.Z, v.W);

    private static Vector3 GetSgn(IAxes axes)
    {
        if (axes.Has_Sgn_Vector3Float_5_5())
        {
            return ToVector3(axes.Sgn_Vector3Float_5_5);
        }
        if (axes.Has_Sgn_Vector3Float_5_4())
        {
            return ToVector3(axes.Sgn_Vector3Float_5_4);
        }
        Vector4Float_4? sgn4 = axes.Sgn_Vector4Float_4;
        return sgn4 is null ? Vector3.One : new Vector3(sgn4.X, sgn4.Y, sgn4.Z);
    }

    private static Vector3 GetLimit(IAxes axes, bool min)
    {
        if (min)
        {
            if (axes.Limit.Has_Min_Vector3Float_5_5())
            {
                return ToVector3(axes.Limit.Min_Vector3Float_5_5);
            }
            if (axes.Limit.Has_Min_Vector3Float_5_4())
            {
                return ToVector3(axes.Limit.Min_Vector3Float_5_4);
            }
            Vector4Float_4? min4 = axes.Limit.Min_Vector4Float_4;
            return min4 is null ? Vector3.Zero : new Vector3(min4.X, min4.Y, min4.Z);
        }
        if (axes.Limit.Has_Max_Vector3Float_5_5())
        {
            return ToVector3(axes.Limit.Max_Vector3Float_5_5);
        }
        if (axes.Limit.Has_Max_Vector3Float_5_4())
        {
            return ToVector3(axes.Limit.Max_Vector3Float_5_4);
        }
        Vector4Float_4? max4 = axes.Limit.Max_Vector4Float_4;
        return max4 is null ? Vector3.Zero : new Vector3(max4.X, max4.Y, max4.Z);
    }

    private static Vector3 ToVector3(IVector3Float? v) => v is null ? Vector3.Zero : new Vector3(v.X, v.Y, v.Z);

    /// <summary>
    /// Body rows mirror RuriRipperImporter/humanoid_retarget.py:_MUSCLE_DOF (validated against IK
    /// markers) extended with the eye/jaw muscles it omitted; finger rows are generated from the
    /// Unity muscle taxonomy (attribute pattern "&lt;Hand&gt;.&lt;Finger&gt;.&lt;DoF&gt;",
    /// 1/2/3 Stretched -> Z of proximal/intermediate/distal phalange, Spread -> Y of proximal).
    /// </summary>
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

/// <summary>One human bone the muscle referential drives: its Axes plus the TOS transform path.</summary>
public sealed class MuscleBone
{
    public string Path { get; }
    public Quaternion PreQ { get; }
    public Quaternion PostQ { get; }
    public Vector3 Sgn { get; }
    public Vector3 LimitMin { get; }
    public Vector3 LimitMax { get; }
    public bool IsHips { get; internal set; }

    private readonly string?[] _dofMuscles = new string?[3];

    internal MuscleBone(string path, Quaternion preQ, Quaternion postQ, Vector3 sgn,
        Vector3 limitMin, Vector3 limitMax)
    {
        Path = path;
        PreQ = preQ;
        PostQ = postQ;
        Sgn = sgn;
        LimitMin = limitMin;
        LimitMax = limitMax;
    }

    internal void SetDofMuscle(int axis, string muscle) => _dofMuscles[axis] = muscle;
    public string? GetDofMuscle(int axis) => _dofMuscles[axis];
}
