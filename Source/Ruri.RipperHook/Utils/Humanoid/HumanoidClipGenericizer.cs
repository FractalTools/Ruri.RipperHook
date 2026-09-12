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

namespace Ruri.RipperHook.Humanoid;

public sealed class SolvedHumanoidPose
{
    public required float SampleRate { get; init; }
    public required int FrameCount { get; init; }

    public required List<(string Path, Quaternion[] Rotations)> BoneRotations { get; init; }

    public (string Path, Vector3[] Positions)? HipsPositions { get; init; }

    public (Vector3[] Positions, Quaternion[] Rotations)? Motion { get; init; }
}

public static class HumanoidClipGenericizer
{
    private const float DefaultFloatWeight = 1f / 3f;

    public static bool HasMuscleCurves(IAnimationClip clip)
    {
        foreach (IFloatCurve floatCurve in clip.FloatCurves_C74)
        {
            if (AvatarMuscleReferential.IsMuscleAttribute(floatCurve.Attribute.String))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool HasMuscleChannel(List<(string Attribute, HermiteCurve Curve)> channels)
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

    public static SolvedHumanoidPose? Solve(AvatarMuscleReferential referential,
        List<(string Attribute, HermiteCurve Curve)> channels, float sampleRate,
        bool keepPositionXz, bool keepPositionY, bool keepOrientation)
    {
        if (channels.Count == 0 || !HasMuscleChannel(channels))
        {
            return null;
        }

        sampleRate = sampleRate > 0f ? sampleRate : 60f;
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
            : throw new InvalidOperationException("muscle sample table too large")];
        for (int c = 0; c < columnCount; c++)
        {
            channelIndex[channels[c].Attribute] = c;
            channels[c].Curve.SampleUniform(values, c, columnCount, frameCount, sampleRate);
        }

        RootChannelPlan root = referential.BindClip(channelIndex);

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

        List<(string Path, Quaternion[] Rotations)> boneRotations = new();
        (string, Vector3[])? hipsOut = null;
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
            Quaternion[] rotations = rotationBySlot[bone.Slot];
            AlignHemispheres(rotations);
            boneRotations.Add((bone.Path, rotations));
            if (isHips && hipsPositions is not null)
            {
                hipsOut = (bone.Path, hipsPositions);
            }
        }
        if (hasMotion)
        {
            AlignHemispheres(motionRotations);
        }

        return new SolvedHumanoidPose
        {
            SampleRate = sampleRate,
            FrameCount = frameCount,
            BoneRotations = boneRotations,
            HipsPositions = hipsOut,
            Motion = hasMotion ? (motionPositions, motionRotations) : null,
        };
    }

    public static int Convert(IAnimationClip clip, AvatarMuscleReferential referential)
    {
        SolvedHumanoidPose? pose = Solve(referential, CollectMuscleChannels(clip),
            clip.SampleRate_C74,
            clip.MuscleClipInfo_C74?.KeepOriginalPositionXZ ?? true,
            clip.MuscleClipInfo_C74?.KeepOriginalPositionY ?? true,
            clip.MuscleClipInfo_C74?.KeepOriginalOrientation ?? true);
        if (pose is null)
        {
            return 0;
        }

        int written = 0;
        foreach ((string path, Quaternion[] rotations) in pose.BoneRotations)
        {
            WriteRotationCurve(clip, path, rotations, pose.FrameCount, pose.SampleRate);
            written++;
        }
        if (pose.HipsPositions is { } hips)
        {
            WritePositionCurve(clip, hips.Path, hips.Positions, pose.FrameCount, pose.SampleRate);
            written++;
        }
        if (pose.Motion is { } motion)
        {
            WritePositionCurve(clip, string.Empty, motion.Positions, pose.FrameCount, pose.SampleRate);
            WriteRotationCurve(clip, string.Empty, motion.Rotations, pose.FrameCount, pose.SampleRate);
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

    private static void AlignHemispheres(Quaternion[] rotations)
    {
        for (int f = 1; f < rotations.Length; f++)
        {
            if (Quaternion.Dot(rotations[f], rotations[f - 1]) < 0f)
            {
                rotations[f] = -rotations[f];
            }
        }
    }

    private static void WriteRotationCurve(IAnimationClip clip, string path, Quaternion[] rotations,
        int frameCount, float sampleRate)
    {
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
