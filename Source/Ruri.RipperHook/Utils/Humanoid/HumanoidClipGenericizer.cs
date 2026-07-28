using System.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.Keyframe.TangentMode;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Quaternionf;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Vector3f;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;

namespace Ruri.RipperHook.Humanoid;

/// <summary>
/// Rewrites a humanoid AnimationClip into a plain generic one, in place, on the asset itself.
///
/// A humanoid clip stores the main skeleton as ~95 normalised muscle floats plus a root reference
/// (RootT/RootQ) rather than per-bone transform curves, so anything that does not implement Unity's
/// Mecanim solver sees a body that never moves. Solving it at the ASSET level -- once, here -- means
/// every downstream consumer (the .anim YAML the project exporter writes, the glTF exporter, the
/// in-process Blender bridge) reads one ordinary generic clip and needs no humanoid knowledge at
/// all. The alternative, which this replaces, was one solver per consumer: a C# one living inside
/// the glTF exporter and a second, independently maintained Python one inside the Blender importer,
/// each obliged to stay bit-identical to Unity and to each other.
///
/// What the rewrite produces, per driven human bone, at the clip's own sample rate:
///   * a rotation curve at the bone's Avatar-TOS transform path, carrying the bone's FULL absolute
///     local rotation (see <see cref="AvatarMuscleReferential.BodyLocalQuats"/> -- already including
///     Unity's twist-solve parent/child redistribution);
///   * for the hips, additionally a position curve, since its own transform is reconstructed rather
///     than stored (see <see cref="AvatarMuscleReferential.BodyTransform"/>);
///   * on the animator root path (the empty string), the extracted root motion, so that
///     root ∘ hips reproduces the original RootT/RootQ exactly.
/// The consumed muscle/root float curves are then dropped, because leaving them in would present
/// the same motion twice in two encodings.
///
/// Curves already present for other bones (IK markers, twist helpers, cloth) are untouched: they
/// are ordinary transform curves that were never part of the muscle encoding.
/// </summary>
public static class HumanoidClipGenericizer
{
    /// <summary>Keyframe tangent weight AssetRipper's own converter writes for unweighted keys.</summary>
    private const float DefaultFloatWeight = 1f / 3f;

    /// <summary>
    /// Convert one clip. Returns the number of bone curves written, or 0 when the clip carries no
    /// muscle data (already generic -- left completely untouched).
    /// </summary>
    public static int Convert(IAnimationClip clip, AvatarMuscleReferential referential)
    {
        List<(string Attribute, HermiteCurve Curve)> channels = CollectMuscleChannels(clip);
        if (channels.Count == 0)
        {
            return 0;
        }

        float sampleRate = clip.SampleRate_C74 > 0f ? clip.SampleRate_C74 : 60f;
        float duration = 0f;
        foreach ((_, HermiteCurve curve) in channels)
        {
            duration = MathF.Max(duration, curve.LastTime);
        }
        int frameCount = Math.Max(1, (int)MathF.Round(duration * sampleRate) + 1);

        // One column per muscle/root channel, one row per frame, flat so a frame is a contiguous
        // span the solver can index by column without hashing an attribute string.
        int columnCount = channels.Count;
        Dictionary<string, int> channelIndex = new(columnCount, StringComparer.Ordinal);
        float[] values = new float[(long)frameCount * columnCount <= int.MaxValue
            ? frameCount * columnCount
            : throw new InvalidOperationException($"clip '{clip.Name}' sample table too large")];
        for (int c = 0; c < columnCount; c++)
        {
            channelIndex[channels[c].Attribute] = c;
            channels[c].Curve.SampleUniform(values, c, columnCount, frameCount, sampleRate);
        }

        RootChannelPlan root = referential.BindClip(channelIndex);

        bool keepPositionXz = clip.MuscleClipInfo_C74?.KeepOriginalPositionXZ ?? true;
        bool keepPositionY = clip.MuscleClipInfo_C74?.KeepOriginalPositionY ?? true;
        bool keepOrientation = clip.MuscleClipInfo_C74?.KeepOriginalOrientation ?? true;

        int slotCount = AvatarMuscleReferential.SlotCount;
        Quaternion[] quats = new Quaternion[slotCount];
        bool[] driven = new bool[slotCount];

        // Per driven bone, its rotation for every frame; hips also gets a position track, and the
        // extracted root motion is accumulated alongside.
        Dictionary<int, Quaternion[]> rotationBySlot = new();
        foreach (MuscleBone bone in referential.DrivenBones)
        {
            rotationBySlot[bone.Slot] = new Quaternion[frameCount];
        }
        Vector3[]? hipsPositions = referential.Hips is null ? null : new Vector3[frameCount];
        Vector3[] motionPositions = new Vector3[frameCount];
        Quaternion[] motionRotations = new Quaternion[frameCount];
        bool hasMotion = false;
        bool hasHips = false;

        for (int f = 0; f < frameCount; f++)
        {
            ReadOnlySpan<float> frame = values.AsSpan(f * columnCount, columnCount);
            referential.BodyLocalQuats(frame, quats, driven);

            foreach (MuscleBone bone in referential.DrivenBones)
            {
                if (bone.IsHips)
                {
                    continue;
                }
                if (driven[bone.Slot])
                {
                    rotationBySlot[bone.Slot][f] = quats[bone.Slot];
                }
            }

            if (referential.Hips is { } hips && hipsPositions is not null)
            {
                var body = referential.BodyTransform(frame, root, quats, driven,
                    keepPositionXz, keepPositionY, keepOrientation);
                if (body is not null)
                {
                    (Vector3 position, Quaternion rotation, (Vector3 motionT, Quaternion motionQ)) = body.Value;
                    hipsPositions[f] = position;
                    rotationBySlot[hips.Slot][f] = rotation;
                    motionPositions[f] = motionT;
                    motionRotations[f] = motionQ;
                    hasHips = true;
                    if (motionT.LengthSquared() > 1e-10f || MathF.Abs(motionQ.W) < 0.99999995f)
                    {
                        hasMotion = true;
                    }
                }
            }
        }

        int written = 0;
        foreach (MuscleBone bone in referential.DrivenBones)
        {
            bool isHips = bone.IsHips;
            if (isHips && !hasHips)
            {
                continue;
            }
            if (!isHips && !AnyMuscleBound(bone))
            {
                continue;   // clip drives none of this bone's axes: leave it at rest, write nothing
            }
            WriteRotationCurve(clip, bone.Path, rotationBySlot[bone.Slot], frameCount, sampleRate);
            written++;
            if (isHips && hipsPositions is not null)
            {
                WritePositionCurve(clip, bone.Path, hipsPositions, frameCount, sampleRate);
                written++;
            }
        }

        if (hasMotion)
        {
            // The animator root is the clip's own empty path -- root ∘ hips == the original
            // RootT/RootQ, which is the whole point of the split.
            WritePositionCurve(clip, string.Empty, motionPositions, frameCount, sampleRate);
            WriteRotationCurve(clip, string.Empty, motionRotations, frameCount, sampleRate);
            written += 2;
        }

        DropConsumedFloatCurves(clip);
        return written;
    }

    private static bool AnyMuscleBound(MuscleBone bone)
    {
        for (int dof = 0; dof < 3; dof++)
        {
            if (bone.DofChannel[dof] >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void WriteRotationCurve(IAnimationClip clip, string path, Quaternion[] rotations,
        int frameCount, float sampleRate)
    {
        IQuaternionCurve curve = clip.RotationCurves_C74.AddNew();
        curve.SetValues(path);
        // q and -q are the same rotation, but a consumer interpolating the four components
        // independently sweeps through a degenerate quaternion between an antipodal pair -- the
        // classic one-frame whole-bone twitch on any bone that turns through 180 degrees. Align
        // each key with its predecessor once, here, so no consumer has to.
        for (int f = 1; f < frameCount; f++)
        {
            if (Quaternion.Dot(rotations[f], rotations[f - 1]) < 0f)
            {
                rotations[f] = -rotations[f];
            }
        }
        for (int f = 0; f < frameCount; f++)
        {
            Quaternion value = rotations[f];
            IKeyframe_Quaternionf key = curve.Curve.Curve.AddNew();
            key.Value.SetValues(value.X, value.Y, value.Z, value.W);
            key.InSlope.SetValues(0f, 0f, 0f, 0f);
            key.OutSlope.SetValues(0f, 0f, 0f, 0f);
            key.Time = f / sampleRate;
            key.TangentMode = TangentMode.FreeFree.ToTangent(clip.Collection.Version);
            key.WeightedMode = (int)WeightedMode.None;
            key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
            key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
        }
    }

    private static void WritePositionCurve(IAnimationClip clip, string path, Vector3[] positions,
        int frameCount, float sampleRate)
    {
        IVector3Curve curve = clip.PositionCurves_C74.AddNew();
        curve.SetValues(path);
        for (int f = 0; f < frameCount; f++)
        {
            Vector3 value = positions[f];
            IKeyframe_Vector3f key = curve.Curve.Curve.AddNew();
            key.Value.SetValues(value.X, value.Y, value.Z);
            key.InSlope.SetValues(0f, 0f, 0f);
            key.OutSlope.SetValues(0f, 0f, 0f);
            key.Time = f / sampleRate;
            key.TangentMode = TangentMode.FreeFree.ToTangent(clip.Collection.Version);
            key.WeightedMode = (int)WeightedMode.None;
            key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
            key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
        }
    }

    /// <summary>
    /// Remove the muscle/root float curves the rewrite just consumed. Any other float curve
    /// (blendshape weights, custom material properties) is genuinely independent data and stays.
    /// </summary>
    private static void DropConsumedFloatCurves(IAnimationClip clip)
    {
        var floats = clip.FloatCurves_C74;
        for (int i = floats.Count - 1; i >= 0; i--)
        {
            string attribute = floats[i].Attribute.String;
            if (AvatarMuscleReferential.IsMuscleAttribute(attribute)
                || AvatarMuscleReferential.IsRootAttribute(attribute))
            {
                floats.RemoveAt(i);
            }
        }
    }

    private static List<(string, HermiteCurve)> CollectMuscleChannels(IAnimationClip clip)
    {
        List<(string, HermiteCurve)> channels = new();
        foreach (IFloatCurve floatCurve in clip.FloatCurves_C74)
        {
            string attribute = floatCurve.Attribute.String;
            if (!AvatarMuscleReferential.IsMuscleAttribute(attribute)
                && !AvatarMuscleReferential.IsRootAttribute(attribute))
            {
                continue;
            }
            HermiteCurve curve = HermiteCurve.FromKeyframes(floatCurve.Curve.Curve);
            if (curve.KeyCount > 0)
            {
                channels.Add((attribute, curve));
            }
        }
        return channels;
    }
}
