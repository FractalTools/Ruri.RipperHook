using AssetRipper.Import.Logging;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using Ruri.RipperHook.Conversion;
using System.Numerics;

namespace Ruri.FModelHook.Unreal.Readers;

/// <summary>
/// An animation sequence decoded into sampled tracks: every bone track of its skeleton sampled
/// once per frame (CUE4Parse decodes whatever codec the sequence used, ACL included) as local
/// position, rotation and scale in the host's basis, addressed by the bone's transform path.
///
/// This is the whole of reading a sequence. What is done with the samples afterwards -- reduced
/// to keys and written into a Unity clip, or reduced and handed to a host as curves -- is the
/// consumer's business, and every consumer reads the sequence through here.
/// </summary>
public static class UnrealClip
{
    private const string AclCodecMarker = "ACL";
    private const float AclErrorThresholdCentimetres = 0.01f;
    private const float AclVirtualVertexDistanceCentimetres = 3f;

    /// <summary>One sequence sampled: its rate, the frames it was sampled at, its tracks, and the precision it was compressed to.</summary>
    public sealed record Sampled(float SampleRate, int FrameCount, List<ClipTrack> Tracks, ClipTolerance? Tolerance);

    /// <summary>
    /// The sequence sampled, or null with a line saying why. A sequence whose skeleton does not
    /// load, or one the decoder yields no sequence for, is data about the build, not a fault.
    /// </summary>
    public static Sampled? Read(UAnimSequence source, SourceBasis basis, string owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Skeleton.TryLoad<USkeleton>(out USkeleton? skeleton))
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {owner}:{source.Name} references no loadable skeleton.");
            return null;
        }
        CAnimSet animSet = skeleton.ConvertAnims(source);
        if (animSet.Sequences.Count == 0)
        {
            return null;
        }
        CAnimSequence sequence = animSet.Sequences[0];
        int sourceFrames = Math.Max(1, sequence.NumFrames);
        float assetRate = AssetRate(source, sourceFrames);
        float statedRate = UnrealSourceOptions.AnimationSampleRateValue();
        float sampleRate = statedRate > 0f ? statedRate : assetRate;
        int frameCount = statedRate > 0f ? Math.Max(1, (int)MathF.Round(source.SequenceLength * statedRate) + 1) : sourceFrames;
        float sourceStep = frameCount > 1 ? (sourceFrames - 1f) / (frameCount - 1f) : 0f;

        SkeletonDto skeletonDto = new(skeleton);
        UnrealRig rig = UnrealRig.From(skeletonDto.Bones, basis);
        skeletonDto.Dispose();

        List<ClipTrack> tracks = new();
        for (int bone = 0; bone < rig.Bones.Count && bone < sequence.Tracks.Count; bone++)
        {
            CAnimTrack track = sequence.Tracks[bone];
            if (!track.HasKeys())
            {
                continue;
            }
            UnrealRig.Bone rest = rig.Bones[bone];
            FTransform restPose = skeleton.ReferenceSkeleton.FinalRefBonePose[bone];
            Quaternion[]? rotations = track.KeyQuat.Length > 0 ? new Quaternion[frameCount] : null;
            Vector3[]? positions = track.KeyPos.Length > 0 ? new Vector3[frameCount] : null;
            Vector3[]? scales = track.KeyScale.Length > 0 ? new Vector3[frameCount] : null;
            for (int frame = 0; frame < frameCount; frame++)
            {
                FQuat quaternion = restPose.Rotation;
                FVector position = restPose.Translation;
                FVector scale = restPose.Scale3D;
                track.GetBoneTransform(frame * sourceStep, sourceFrames, ref quaternion, ref position, ref scale);
                if (rotations is not null)
                {
                    rotations[frame] = basis.Rotation(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
                }
                if (positions is not null)
                {
                    positions[frame] = basis.Position(position.X, position.Y, position.Z);
                }
                if (scales is not null)
                {
                    scales[frame] = basis.Scale(scale.X, scale.Y, scale.Z);
                }
            }
            tracks.Add(new ClipTrack { Path = rest.Path, Positions = positions, Rotations = rotations, Scales = scales });
        }
        float toleranceScale = UnrealSourceOptions.AnimationToleranceValue();
        return new Sampled(sampleRate, frameCount, tracks,
            toleranceScale > 0f ? Tolerance(source, basis, toleranceScale) : null);
    }

    /// <summary>
    /// The precision the sequence was compressed at, as the tolerance its written curves keep:
    /// the ACL codec that compressed it states an error threshold and the virtual vertex distance
    /// that threshold is measured at (UAnimBoneCompressionCodec_ACLBase), so a key is dropped
    /// only where the played curve stays within the displacement the game itself accepted,
    /// scaled by the stated multiple. A codec property the cooked object leaves unstated holds
    /// its class default, which is what an unstated property means. A sequence compressed by
    /// anything else keeps every sample.
    /// </summary>
    private static ClipTolerance? Tolerance(UAnimSequence source, SourceBasis basis, float scale)
    {
        if (source.BoneCompressionSettings?.Load<UAnimBoneCompressionSettings>() is not { } settings
            || settings.GetCodec(source.BoneCodecDDCHandle ?? string.Empty) is not { } codec
            || !codec.ExportType.Contains(AclCodecMarker, StringComparison.Ordinal))
        {
            return null;
        }
        float threshold = codec.GetOrDefault("ErrorThreshold", AclErrorThresholdCentimetres);
        float vertexDistance = codec.GetOrDefault("DefaultVirtualVertexDistance", AclVirtualVertexDistanceCentimetres);
        Logger.Verbose(LogCategory.Import, $"[Unreal] {source.Name}: {codec.ExportType} '{codec.Name}' threshold {threshold} cm at {vertexDistance} cm, tolerance x{scale}; stated {string.Join(", ", codec.Properties.Select(static property => property.Name.Text + "=" + property.Tag?.GenericValue))}.");
        float displacement = threshold * basis.UnitScale * scale;
        float angular = threshold / vertexDistance * scale;
        return new ClipTolerance(displacement, angular, angular, displacement);
    }

    /// <summary>
    /// The rate the sequence was sampled at, as the engine defines it: the platform target frame
    /// rate the cooked sequence carries (UAnimSequence::GetSamplingFrameRate), and for a sequence
    /// cooked before that field existed, the relation the engine keeps between its key count and
    /// its length (keys = length * rate + 1).
    /// </summary>
    private static float AssetRate(UAnimSequence source, int sourceFrames)
    {
        FPerPlatformFrameRate? target = source.GetOrDefault<FPerPlatformFrameRate>("PlatformTargetFrameRate");
        if (target is { Default.Denominator: > 0 })
        {
            return (float)target.Default.Numerator / target.Default.Denominator;
        }
        return source.SequenceLength > 0f && sourceFrames > 1 ? (sourceFrames - 1) / source.SequenceLength : 1f;
    }
}
