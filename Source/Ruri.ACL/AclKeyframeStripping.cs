using System.Numerics;

namespace Ruri.ACL;

internal static partial class AclCompression
{
    private static void FindContributingError(AclQuantizationContext context)
    {
        AclSegmentContext segment = context.Segment;
        segment.ContributingError ??= new AclKeyframeStrippingMetadata[32];
        int numFrames = context.NumSamples;
        int numBones = context.NumBones;
        int segmentIndex = segment.SegmentIndex;
        AclKeyframeStrippingMetadata[] contributing = segment.ContributingError;
        uint retained = uint.MaxValue;
        contributing[0] = new(0, segmentIndex, -1, float.PositiveInfinity, false);
        contributing[numFrames - 1] = new(numFrames - 1, segmentIndex, -1, float.PositiveInfinity, false);
        context.LossyTransformsStart ??= new AclTransform[numBones];
        context.LossyTransformsEnd ??= new AclTransform[numBones];
        AclTransform[] startTransforms = context.LossyTransformsStart;
        AclTransform[] endTransforms = context.LossyTransformsEnd;
        context.AllLocalQuery.Build(context.BitRatePerBone);
        for (int iteration = 1; iteration < numFrames - 1; iteration++)
        {
            var best = new AclKeyframeStrippingMetadata(-1, segmentIndex, -1, float.PositiveInfinity, false);
            for (int frame = 1; frame < numFrames - 1; frame++)
            {
                if ((retained & (1u << (31 - frame))) == 0)
                    continue;
                int start = frame - 1;
                while ((retained & (1u << (31 - start))) == 0)
                    start--;
                int end = frame + 1;
                while ((retained & (1u << (31 - end))) == 0)
                    end++;
                float startTime = MathF.Min((start + context.SegmentSampleStartIndex) / context.SampleRate, context.ClipDuration);
                float endTime = MathF.Min((end + context.SegmentSampleStartIndex) / context.SampleRate, context.ClipDuration);
                context.BitRateDatabase.Sample(context.AllLocalQuery, startTime, startTransforms);
                context.BitRateDatabase.Sample(context.AllLocalQuery, endTime, endTransforms);
                float maxError = 0.0f;
                bool trivial = true;
                for (int interpolationFrame = start + 1; interpolationFrame < end; interpolationFrame++)
                {
                    float alpha = (float)(interpolationFrame - start) / (end - start);
                    for (int bone = 0; bone < numBones; bone++)
                        context.LossyLocalPose[bone] = AclMath.QvvLerp(startTransforms[bone], endTransforms[bone], alpha);
                    AclMath.LocalToObjectSpace(context.LossyLocalPose, context.ParentTransformIndices, context.SelfTransformIndices, context.LossyObjectPose, context.HasScale);
                    for (int bone = 0; bone < numBones; bone++)
                    {
                        AclRigidShellMetadata shell = context.ShellMetadataPerTransform[bone];
                        float error = AclMath.CalculateError(context.RawObjectTransforms[interpolationFrame * numBones + bone], context.LossyObjectPose[bone], shell.LocalShellDistance, context.HasScale);
                        maxError = MathF.Max(maxError, error);
                        trivial &= error <= shell.Precision;
                    }
                }
                if (maxError < best.StrippingError)
                    best = new(frame, segmentIndex, iteration - 1, maxError, trivial);
            }
            contributing[best.KeyframeIndex] = best;
            retained &= ~(1u << (31 - best.KeyframeIndex));
        }
        Array.Sort(contributing, 0, numFrames, Comparer<AclKeyframeStrippingMetadata>.Create(static (left, right) => ((uint)left.StrippingIndex).CompareTo((uint)right.StrippingIndex)));
    }

    private static void SortContributingError(AclClipContext clip)
    {
        clip.ContributingError ??= new AclKeyframeStrippingMetadata[clip.NumSamples];
        int[] strippedPerSegment = new int[clip.NumSegments];
        for (int strippingIndex = 0; strippingIndex < clip.NumSamples; strippingIndex++)
        {
            var best = new AclKeyframeStrippingMetadata(-1, 0, -1, float.PositiveInfinity, false);
            for (int segmentIndex = 0; segmentIndex < clip.NumSegments; segmentIndex++)
            {
                AclSegmentContext segment = clip.Segments[segmentIndex];
                int segmentStripIndex = strippedPerSegment[segmentIndex];
                if (segmentStripIndex >= segment.NumSamples)
                    continue;
                if (segment.ContributingError![segmentStripIndex].StrippingError <= best.StrippingError)
                    best = segment.ContributingError[segmentStripIndex];
            }
            best.StrippingIndex = strippingIndex;
            best.KeyframeIndex += clip.Segments[best.SegmentIndex].ClipSampleOffset;
            strippedPerSegment[best.SegmentIndex]++;
            clip.ContributingError[strippingIndex] = best;
        }
    }

    internal static void StripKeyframes(AclClipContext clip, AclCompressionSettings settings)
    {
        if (!settings.IsKeyframeStrippingEnabled)
            return;
        foreach (AclSegmentContext segment in clip.Segments)
            segment.HardKeyframes = segment.NumSamples == 0 ? 0 : segment.NumSamples == 32 ? uint.MaxValue : uint.MaxValue << (32 - segment.NumSamples);
        int numMovableFrames = clip.NumSamples >= 2 ? clip.NumSamples - clip.NumSegments * 2 : 0;
        int numToStrip = 0;
        if (settings.StripTrivialKeyframes)
        {
            for (; numToStrip < numMovableFrames; numToStrip++)
                if (!clip.ContributingError![numToStrip].IsKeyframeTrivial)
                    break;
        }
        for (; numToStrip < numMovableFrames; numToStrip++)
            if (clip.ContributingError![numToStrip].StrippingError > settings.KeyframeStrippingThreshold)
                break;
        int proportionToStrip = Math.Min(numMovableFrames, (int)(settings.KeyframeStrippingProportion * clip.NumSamples));
        numToStrip = Math.Max(numToStrip, proportionToStrip);
        int bitsSaved = 0;
        for (int index = 0; index < numToStrip; index++)
            bitsSaved += clip.Segments[clip.ContributingError![index].SegmentIndex].AnimatedPoseBitSize;
        if (bitsSaved / 8 <= clip.NumSegments * 4)
            numToStrip = 0;
        for (int index = 0; index < numToStrip; index++)
        {
            AclKeyframeStrippingMetadata contributing = clip.ContributingError![index];
            AclSegmentContext segment = clip.Segments[contributing.SegmentIndex];
            int keyframe = contributing.KeyframeIndex - segment.ClipSampleOffset;
            segment.HardKeyframes &= ~(1u << (31 - keyframe));
            int storedSamples = BitOperations.PopCount(segment.HardKeyframes);
            segment.AnimatedDataSize = (segment.AnimatedPoseBitSize * storedSamples + 7) / 8;
        }
        clip.HasStrippedKeyframes = numToStrip != 0;
    }
}
