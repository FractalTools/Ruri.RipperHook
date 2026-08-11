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
using Ruri.RipperHook.Animation;
using Ruri.RipperHook.Humanoid;

namespace Ruri.RipperHook.AR;

public static class HumanoidClipGenericizer
{
    private const float DefaultFloatWeight = 1f / 3f;

    public static int Convert(IAnimationClip clip, AvatarMuscleReferential referential)
    {
        List<(string Attribute, HermiteCurve Curve)> channels = CollectMuscleChannels(clip);
        if (channels.Count == 0 || !HasMuscleChannel(channels))
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
                continue;
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
            WritePositionCurve(clip, string.Empty, motionPositions, frameCount, sampleRate);
            WriteRotationCurve(clip, string.Empty, motionRotations, frameCount, sampleRate);
            written += 2;
        }

        DropConsumedFloatCurves(clip);
        return written;
    }

    /// <summary>
    /// Whether the clip is actually MUSCLE-encoded, rather than just carrying root motion.
    ///
    /// RootT/RootQ alone do not make a humanoid clip: a generic clip -- an ACL-compressed one
    /// especially -- ships complete per-bone transform tracks AND Unity's root-motion channels.
    /// Solving that would bind no muscle at all, derive a body pose from the REST skeleton's own
    /// FK, and write it over the hips' real transform track, which is the whole body's placement.
    /// </summary>
    private static bool HasMuscleChannel(List<(string Attribute, HermiteCurve Curve)> channels)
    {
        foreach ((string attribute, _) in channels)
        {
            if (AvatarMuscleReferential.IsMuscleAttribute(attribute))
            {
                return true;
            }
        }
        return false;
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
        // One binding per path per kind: a second curve on a path that already has one leaves
        // which of them drives the bone up to whoever reads the clip, and a reader that keys by
        // path silently keeps only one of them.
        var curves = clip.RotationCurves_C74;
        for (int i = curves.Count - 1; i >= 0; i--)
        {
            if (curves[i].Path == path)
            {
                curves.RemoveAt(i);
            }
        }
        IQuaternionCurve curve = curves.AddNew();
        curve.SetValues(path);
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
        // See WriteRotationCurve: one binding per path per kind.
        var curves = clip.PositionCurves_C74;
        for (int i = curves.Count - 1; i >= 0; i--)
        {
            if (curves[i].Path == path)
            {
                curves.RemoveAt(i);
            }
        }
        IVector3Curve curve = curves.AddNew();
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
