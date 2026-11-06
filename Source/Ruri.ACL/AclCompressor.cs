namespace Ruri.ACL;

public static class AclCompressor
{
    public static byte[] CompressTransforms(AclTransformTrack[] tracks, float sampleRate, AclCompressionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ValidateSampleRate(sampleRate);
        int numSamples = tracks.Length != 0 ? tracks[0].Samples.Length : 0;
        foreach (AclTransformTrack track in tracks)
        {
            if (track.Samples.Length != numSamples)
                throw new ArgumentException("Track array requires the same number of samples in every track", nameof(tracks));
            if (track.ParentIndex < -1 || track.ParentIndex >= tracks.Length)
                throw new ArgumentException("Invalid parent_index", nameof(tracks));
            ValidatePrecision(track.Precision);
            if (!float.IsFinite(track.ShellDistance) || track.ShellDistance < 0.0f)
                throw new ArgumentException("The shell distance must be finite and positive or zero", nameof(tracks));
        }
        return AclCompression.CompressTransformTrackList(tracks, sampleRate, (settings ?? AclCompressionSettings.GetDefault()).Copy());
    }

    public static byte[] CompressFloats(AclFloatTrack[] tracks, float sampleRate, AclCompressionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ValidateSampleRate(sampleRate);
        int numSamples = tracks.Length != 0 ? tracks[0].Samples.Length : 0;
        foreach (AclFloatTrack track in tracks)
        {
            if (track.Samples.Length != numSamples)
                throw new ArgumentException("Track array requires the same number of samples in every track", nameof(tracks));
            ValidatePrecision(track.Precision);
        }
        return AclCompression.CompressFloats(tracks, sampleRate, (settings ?? AclCompressionSettings.GetDefault()).Copy());
    }

    private static void ValidatePrecision(float precision)
    {
        if (!float.IsFinite(precision) || precision < 0.0f)
            throw new ArgumentException("The precision must be finite and positive or zero");
    }

    private static void ValidateSampleRate(float sampleRate)
    {
        if (!float.IsFinite(sampleRate) || sampleRate <= 0.0f)
            throw new ArgumentException("The sample rate must be finite and greater than zero", nameof(sampleRate));
    }
}

internal static partial class AclCompression
{
    internal static byte[] CompressTransformTrackList(AclTransformTrack[] tracks, float sampleRate, AclCompressionSettings settings)
    {
        if (!float.IsFinite(settings.KeyframeStrippingProportion) || settings.KeyframeStrippingProportion < 0.0f || settings.KeyframeStrippingProportion > 1.0f)
            throw new ArgumentException("proportion must be in the range [0.0, 1.0]", nameof(settings));
        if (!float.IsFinite(settings.KeyframeStrippingThreshold) || settings.KeyframeStrippingThreshold < 0.0f)
            throw new ArgumentException("threshold must be positive definite", nameof(settings));
        bool allFullPrecision = settings.RotationFormat != AclRotationFormat.QuatDropWVariable && settings.TranslationFormat != AclVectorFormat.Vector3Variable && settings.ScaleFormat != AclVectorFormat.Vector3Variable;
        if (allFullPrecision && settings.IsKeyframeStrippingEnabled)
            throw new ArgumentException("Raw tracks have no contributing error", nameof(settings));

        AclClipContext raw = InitializeClipContext(tracks, sampleRate, settings);
        AclClipContext lossy = InitializeClipContext(tracks, sampleRate, settings);
        if (settings.Level == AclCompressionLevel.Automatic)
            settings.Level = FindBestCompressionLevel(lossy);

        raw.ClipShellMetadata = ComputeClipShellDistances(raw);
        lossy.ClipShellMetadata = raw.ClipShellMetadata;
        OptimizeLooping(lossy, settings);
        ConvertRotationStreams(lossy, settings.RotationFormat);
        ExtractClipBoneRanges(lossy);
        CompactConstantStreams(lossy, settings);
        NormalizeClipStreams(lossy, settings);
        if (!allFullPrecision)
            SegmentStreams(lossy);
        if (lossy.NumSegments > 1)
        {
            ExtractSegmentBoneRanges(lossy);
            NormalizeSegmentStreams(lossy, settings);
        }
        QuantizeStreams(lossy, settings, raw);
        CalculateAnimatedDataSize(lossy);
        StripKeyframes(lossy, settings);
        return PackTransforms(lossy, settings);
    }

    private static AclCompressionLevel FindBestCompressionLevel(AclClipContext context)
    {
        if (context.NumSamples > 500)
            return AclCompressionLevel.Medium;
        int longestChainLength = 0;
        int words = (context.NumBones + 31) / 32;
        for (int leaf = 0; leaf < context.NumLeafTransforms; ++leaf)
        {
            int count = 0;
            for (int word = 0; word < words; ++word)
                count += System.Numerics.BitOperations.PopCount(context.LeafTransformChains[leaf * words + word]);
            longestChainLength = Math.Max(longestChainLength, count);
        }
        return longestChainLength < 18 ? AclCompressionLevel.Highest : longestChainLength < 25 ? AclCompressionLevel.High : AclCompressionLevel.Medium;
    }
}
