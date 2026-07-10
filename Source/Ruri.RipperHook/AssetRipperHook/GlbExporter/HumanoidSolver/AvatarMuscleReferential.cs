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
/// RootT/RootQ do NOT drive the hips directly: they are Unity's internal
/// mecanim::human::Human "root reference" -- a mass-weighted center of mass across the 25 body
/// bones (RootT) and an orientation frame built from the shoulder/hip bone positions (RootQ),
/// relative to the same quantities computed once from the avatar's rest pose. Both were
/// confirmed by decompiling Unity's own native HumanComputeBoneMassCenter/
/// HumanComputeOrientation/HumanSetupAxes (IDA Pro on Unity.dll) and validated against live
/// Unity ground truth (Animator + AnimationClip.SampleAnimation on a real walk-cycle clip): the
/// orientation-frame formula matches Unity's own rest-pose computation to 0.00002 degrees, and
/// the resulting hips rotation and (Y, X) position track live Unity output to a few degrees /
/// centimeters across a full multi-frame gait cycle. See <see cref="BodyTransform"/> for the
/// full derivation (a provisional FK with hips forced to the origin/identity, whose mass-center
/// and orientation are solved against RootT/RootQ for the hips' true transform).
///
/// KNOWN LIMITATION: clips authored without a separate MotionT/MotionQ curve (ground-projected
/// root motion) still bake full world-space walking progress into RootT itself; since that
/// motion belongs on the character's root GameObject rather than on the hips, this shows up as
/// drift along the walk direction when no MotionT curve exists to subtract it back out first.
/// This is the same class of issue the MotionT subtraction in <see cref="BodyTransform"/>
/// already exists for -- a pre-existing limitation for MotionT-less clips, not a regression
/// introduced by this formula.
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
    /// Port of mecanim::human::HumanComputeBoneMassCenter's per-body-slot table: most bones use
    /// the midpoint of their own and an adjacent bone's provisional position. Any body slot not
    /// listed here (feet, hands, toes, eyes, head, jaw) uses its own provisional position
    /// directly, matching HumanComputeBoneMassCenter's default case (see
    /// humanoid_retarget.py's ``_MASS_CENTER_FORMULA``, same table).
    /// </summary>
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

    // Root-motion (hips) reconstruction inputs -- see the class doc comment and BodyTransform.
    private float[] _humanBoneMass = Array.Empty<float>();
    private Quaternion _qRest = Quaternion.Identity;
    private int[] _nodeParent = Array.Empty<int>();
    private Vector3[] _nodeRestT = Array.Empty<Vector3>();
    private Quaternion[] _nodeRestQ = Array.Empty<Quaternion>();

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

        referential._humanBoneMass = new float[human.HumanBoneMass.Count];
        for (int i = 0; i < human.HumanBoneMass.Count; i++)
        {
            referential._humanBoneMass[i] = human.HumanBoneMass[i];
        }
        referential._qRest = ToQuaternion(human.RootX.Q);

        int nodeCount = skeleton.Node.Count;
        referential._nodeParent = new int[nodeCount];
        referential._nodeRestT = new Vector3[nodeCount];
        referential._nodeRestQ = new Quaternion[nodeCount];
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
        // Hips 没有 Axes(不被肌肉驱动)但仍需 TOS 路径与 rest 来接 RootT/RootQ。
        if (axes is null && slot != (int)BoneType.Hips)
        {
            return;
        }

        MuscleBone bone = axes is null
            ? new MuscleBone(path.String, nodeIndex, Quaternion.Identity, Quaternion.Identity, Vector3.One, Vector3.Zero, Vector3.Zero)
            : new MuscleBone(
                path.String,
                nodeIndex,
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
    /// The hips' own FULL absolute local position+rotation for this frame, reconstructed from
    /// the clip's Root curves, plus whatever root motion was extracted onto the character's own
    /// root object, or null when the clip carries no root translation channels
    /// (humanoid_retarget.py's ``body_transform``, same formula). The hips value is NOT a delta
    /// -- used directly, like <see cref="LocalRotation"/>'s contract for every other bone -- and
    /// RootT/RootQ are NOT the hips' own transform (see the class doc comment): they are the
    /// avatar's mass-center/orientation reference, so recovering the hips' transform means
    /// building a PROVISIONAL pose with the hips forced to the origin/identity (every other body
    /// bone at its frame's absolute local rotation), then solving the rigid transform that makes
    /// the provisional pose's mass-center/orientation match RootT/RootQ:
    /// mass_center(actual) = T + R @ mass_center(provisional),
    /// orientation(actual) = R @ orientation(provisional).
    /// MotionT is the ground-projected root motion; subtracting it here (as before) removes
    /// that same rigid drift from the mass-center target.
    ///
    /// ``keepPositionXz``/``keepPositionY``/``keepOrientation`` mirror the clip's own
    /// ``MuscleClipInfo.KeepOriginalPosition{Xz,Y}``/``KeepOriginalOrientation``. When a setting
    /// is False, Unity extracts that component as root motion belonging to the character's root
    /// GameObject rather than the hips (confirmed by decompiling
    /// mecanim::animation::EvaluateRootMotion/GetClipX/GetCycleX on Unity.dll, validated against
    /// live Unity ground truth). The returned ``Motion`` is exactly that extracted amount (zero
    /// for any axis where the corresponding ``keep*`` is True) -- the caller MUST bake it onto
    /// the character's root object separately (see <see cref="HumanoidClipBaker"/>) or the
    /// character will animate its stride in place with no actual locomotion.
    /// </summary>
    public (Vector3 Position, Quaternion Rotation, (Vector3 Position, Quaternion Rotation) Motion)? BodyTransform(
        Func<string, float?> muscleLookup, bool keepPositionXz = true, bool keepPositionY = true,
        bool keepOrientation = true)
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
        float fullX = (tx ?? 0f) - mx;
        float fullY = (ty ?? 0f) - my;
        float fullZ = (tz ?? 0f) - mz;
        float rootX = keepPositionXz ? fullX : 0f;
        float rootY = keepPositionY ? fullY : 0f;
        float rootZ = keepPositionXz ? fullZ : 0f;
        Vector3 rootTSimple = new(rootX, rootY, rootZ);
        Vector3 motionT = new(fullX - rootX, fullY - rootY, fullZ - rootZ);

        float? qw = muscleLookup("RootQ.w");
        Quaternion fullQ = qw is null
            ? Quaternion.Identity
            : Quaternion.Normalize(new Quaternion(
                muscleLookup("RootQ.x") ?? 0f,
                muscleLookup("RootQ.y") ?? 0f,
                muscleLookup("RootQ.z") ?? 0f,
                qw.Value));
        Quaternion rootQ;
        Quaternion motionQ;
        if (keepOrientation)
        {
            rootQ = fullQ;
            motionQ = Quaternion.Identity;
        }
        else
        {
            // Extract only the yaw (Y-axis) twist component -- drop it, keep any residual
            // swing (lean/tilt), the same swing-twist shape LocalRotation already uses.
            //
            // Composition order matters: in the baked scene the root object's rotation is
            // the OUTER transform and the hips' local rotation is INNER (world = motionQ *
            // rHips, standard parent-then-child composition), so recovering fullQ from that
            // product needs rootQ = inverse(motionQ) * fullQ -- NOT fullQ * inverse(motionQ),
            // which solves the decomposition for the opposite (hips-outer) composition order
            // and silently produces a wrong residual rotation whenever fullQ and twistY don't
            // commute (i.e. whenever there is also swing/lean, exactly the walking-with-
            // natural-gait-lean case). This showed up visually as feet not meeting the ground.
            Quaternion twistY = Quaternion.Normalize(new Quaternion(0f, fullQ.Y, 0f, fullQ.W));
            rootQ = Quaternion.Normalize(Quaternion.Inverse(twistY) * fullQ);
            motionQ = twistY;
        }
        // rootTSimple is expressed in the same (unrotated) frame as fullT; since the object's
        // rotation (motionQ) sits between it and the hips in the scene composition, the hips'
        // own position must be counter-rotated by the same amount so world = motionQ *
        // (rootT + ...) recomposes back to rootTSimple exactly (see the rotation comment above
        // for why order matters here).
        Vector3 rootT = Vector3.Transform(rootTSimple, Quaternion.Inverse(motionQ));
        (Vector3, Quaternion) motion = (motionT, motionQ);

        (Vector3 Pos, Quaternion Rot)?[] fk = ProvisionalFk(muscleLookup);
        if (fk[(int)BoneType.LeftUpperArm] is null || fk[(int)BoneType.RightUpperArm] is null
            || fk[(int)BoneType.LeftUpperLeg] is null || fk[(int)BoneType.RightUpperLeg] is null)
        {
            return (rootT, rootQ, motion); // avatar too incomplete to solve; best-effort fallback
        }

        Quaternion qProvisional = ComputeOrientation(fk);
        Vector3 massCenterProvisional = ComputeMassCenter(fk);

        Quaternion rHips = Quaternion.Normalize(rootQ * _qRest * Quaternion.Inverse(qProvisional));
        Vector3 tHips = rootT - Vector3.Transform(massCenterProvisional, rHips);
        return (tHips, rHips, motion);
    }

    /// <summary>
    /// Every body bone's world position+rotation for this frame, treating the hips as sitting at
    /// the origin with identity rotation (see <see cref="BodyTransform"/>). Walks the raw
    /// skeleton node hierarchy (not just human-to-human parenting) since some human bones'
    /// immediate parent is an unmapped intermediate node.
    /// </summary>
    private (Vector3 Pos, Quaternion Rot)?[] ProvisionalFk(Func<string, float?> muscleLookup)
    {
        Quaternion?[] localRot = new Quaternion?[BodySlots];
        for (int slot = 0; slot < BodySlots; slot++)
        {
            if (_bones[slot] is { IsHips: false } bone)
            {
                localRot[slot] = LocalRotation(bone, muscleLookup);
            }
        }

        int hipsNode = Hips?.NodeIndex ?? -1;
        Dictionary<int, int> nodeToSlot = new();
        for (int slot = 0; slot < BodySlots; slot++)
        {
            if (_bones[slot] is { } b)
            {
                nodeToSlot[b.NodeIndex] = slot;
            }
        }

        Dictionary<int, (Vector3, Quaternion)> memo = new();
        (Vector3, Quaternion) Solve(int nodeIndex)
        {
            if (memo.TryGetValue(nodeIndex, out (Vector3, Quaternion) cached))
            {
                return cached;
            }
            if (nodeIndex == hipsNode || nodeIndex < 0)
            {
                (Vector3, Quaternion) origin = (Vector3.Zero, Quaternion.Identity);
                memo[nodeIndex] = origin;
                return origin;
            }
            int parentIndex = nodeIndex < _nodeParent.Length ? _nodeParent[nodeIndex] : -1;
            (Vector3 parentPos, Quaternion parentRot) = Solve(parentIndex);
            Quaternion rot = nodeToSlot.TryGetValue(nodeIndex, out int slot) && localRot[slot] is { } lr
                ? lr
                : _nodeRestQ[nodeIndex];
            Vector3 worldPos = parentPos + Vector3.Transform(_nodeRestT[nodeIndex], parentRot);
            Quaternion worldRot = Quaternion.Normalize(parentRot * rot);
            (Vector3, Quaternion) result = (worldPos, worldRot);
            memo[nodeIndex] = result;
            return result;
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

    /// <summary>
    /// Port of mecanim::human::HumanComputeBoneMassCenter's per-body-slot table; see
    /// <see cref="MassCenterFormula"/>.
    /// </summary>
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

    /// <summary>
    /// Port of the mass-weighted center-of-mass loop in
    /// mecanim::human::HumanSetupAxes/RetargetTo.
    /// </summary>
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

    /// <summary>
    /// Port of mecanim::human::HumanComputeOrientation: the body's orientation frame from the
    /// shoulder-center/hip-center world positions. Matches Unity's own rest-pose computation of
    /// this same formula to 0.00002 degrees (validated against m_RootX.q).
    /// </summary>
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
        return MatrixToQuaternion(right, up, forward);
    }

    /// <summary>3x3 rotation matrix (given as its 3 columns, each a world-space unit vector for a
    /// local axis) -> quaternion, Shepperd's method.</summary>
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

    /// <summary>``math::trsX``'s translation is versioned (Vector3 vs Vector4 layout), same
    /// version-guard shape as GetSgn/GetLimit above.</summary>
    private static Vector3 ToXformTranslation(IXform xform)
    {
        return xform.Has_T3() ? ToVector3(xform.T3) : new Vector3(xform.T4!.X, xform.T4.Y, xform.T4.Z);
    }

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
    public int NodeIndex { get; }
    public Quaternion PreQ { get; }
    public Quaternion PostQ { get; }
    public Vector3 Sgn { get; }
    public Vector3 LimitMin { get; }
    public Vector3 LimitMax { get; }
    public bool IsHips { get; internal set; }

    private readonly string?[] _dofMuscles = new string?[3];

    internal MuscleBone(string path, int nodeIndex, Quaternion preQ, Quaternion postQ, Vector3 sgn,
        Vector3 limitMin, Vector3 limitMax)
    {
        Path = path;
        NodeIndex = nodeIndex;
        PreQ = preQ;
        PostQ = postQ;
        Sgn = sgn;
        LimitMin = limitMin;
        LimitMax = limitMax;
    }

    internal void SetDofMuscle(int axis, string muscle) => _dofMuscles[axis] = muscle;
    public string? GetDofMuscle(int axis) => _dofMuscles[axis];
}
