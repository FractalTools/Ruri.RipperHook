using System.Numerics;
using System.Runtime.InteropServices;

namespace Ruri.ACL;

internal static partial class AclCompression
{
    internal static Vector4 LoadRotationSample(ReadOnlySpan<byte> data, AclRotationFormat format, byte bitRate)
    {
        if (format == AclRotationFormat.QuatFull)
            return MemoryMarshal.Read<Vector4>(data);
        if (format == AclRotationFormat.QuatDropWFull || bitRate == 24)
            return AclBitPacking.UnpackVector3_96(data);
        return bitRate == 0 ? AclBitPacking.UnpackVector3U48(data) : AclBitPacking.UnpackVector3UXX(BitRateNumBits[bitRate], data);
    }

    internal static Vector4 LoadVectorSample(ReadOnlySpan<byte> data, AclVectorFormat format, byte bitRate)
    {
        if (format == AclVectorFormat.Vector3Full || bitRate == 24)
            return AclBitPacking.UnpackVector3_96(data);
        return bitRate == 0 ? AclBitPacking.UnpackVector3U48(data) : AclBitPacking.UnpackVector3UXX(BitRateNumBits[bitRate], data);
    }

    internal static Quaternion RotationToQuat32(Vector4 rotation, AclRotationFormat format) => format == AclRotationFormat.QuatFull
        ? AclMath.ToQuaternion(rotation) : AclMath.Normalize(AclMath.FromPositiveW(rotation));

    internal static Quaternion GetRotationSample(AclTransformStreams track, int sampleIndex)
    {
        AclRotationTrackStream stream = track.Rotations;
        AclSegmentContext segment = track.Segment;
        AclClipContext clip = segment.Clip;
        byte bitRate = stream.BitRate;
        if (stream.RotationFormat == AclRotationFormat.QuatDropWVariable && bitRate == 0)
            sampleIndex = 0;
        Vector4 value = LoadRotationSample(stream.Samples.AsSpan(sampleIndex * stream.SampleSize), stream.RotationFormat, bitRate);
        if (!track.IsRotationConstant && clip.AreRotationsNormalized && bitRate != 24)
        {
            if (segment.AreRotationsNormalized && bitRate != 0)
                value = value * segment.Ranges[track.BoneIndex].Rotation.Extent + segment.Ranges[track.BoneIndex].Rotation.Min;
            value = value * clip.Ranges[track.BoneIndex].Rotation.Extent + clip.Ranges[track.BoneIndex].Rotation.Min;
        }
        return RotationToQuat32(value, stream.RotationFormat);
    }

    internal static Quaternion GetRotationSample(AclTransformStreams track, AclTransformStreams rawTrack, int sampleIndex, byte bitRate)
    {
        AclSegmentContext segment = track.Segment;
        AclClipContext clip = segment.Clip;
        AclRotationFormat format = track.Rotations.RotationFormat;
        Vector4 value;
        if (bitRate == 0)
            value = AclBitPacking.DecayVector3U48(track.ConstantRotation);
        else if (bitRate == 24)
        {
            value = rawTrack.Rotations.GetRawSample(segment.ClipSampleOffset + sampleIndex);
            value = ConvertRotation(value, AclRotationFormat.QuatFull, format);
        }
        else
        {
            Vector4 rotation = LoadRotationSample(track.Rotations.Samples.AsSpan(sampleIndex * track.Rotations.SampleSize), format, 0);
            value = AclBitPacking.DecayVector3UXX(rotation, BitRateNumBits[bitRate]);
        }
        if (bitRate != 24)
        {
            if (segment.AreRotationsNormalized && bitRate != 0)
                value = value * segment.Ranges[track.BoneIndex].Rotation.Extent + segment.Ranges[track.BoneIndex].Rotation.Min;
            value = value * clip.Ranges[track.BoneIndex].Rotation.Extent + clip.Ranges[track.BoneIndex].Rotation.Min;
        }
        return RotationToQuat32(value, format);
    }

    internal static Quaternion GetRotationSample(AclTransformStreams track, int sampleIndex, AclRotationFormat desiredFormat)
    {
        AclRotationFormat format = track.Rotations.RotationFormat;
        Vector4 value = LoadRotationSample(track.Rotations.Samples.AsSpan(sampleIndex * track.Rotations.SampleSize), format, 0);
        if (track.Segment.Clip.AreRotationsNormalized && !track.IsRotationConstant)
        {
            if (track.Segment.AreRotationsNormalized)
                value = value * track.Segment.Ranges[track.BoneIndex].Rotation.Extent + track.Segment.Ranges[track.BoneIndex].Rotation.Min;
            value = value * track.Segment.Clip.Ranges[track.BoneIndex].Rotation.Extent + track.Segment.Clip.Ranges[track.BoneIndex].Rotation.Min;
        }
        return RotationToQuat32(value, format);
    }

    internal static Vector4 GetTranslationSample(AclTransformStreams track, int sampleIndex) => GetVectorSample(track, sampleIndex, false);
    internal static Vector4 GetScaleSample(AclTransformStreams track, int sampleIndex) => GetVectorSample(track, sampleIndex, true);
    internal static Vector4 GetTranslationSample(AclTransformStreams track, AclTransformStreams rawTrack, int sampleIndex, byte bitRate) => GetVectorSample(track, rawTrack, sampleIndex, bitRate, false);
    internal static Vector4 GetScaleSample(AclTransformStreams track, AclTransformStreams rawTrack, int sampleIndex, byte bitRate) => GetVectorSample(track, rawTrack, sampleIndex, bitRate, true);
    internal static Vector4 GetTranslationSample(AclTransformStreams track, int sampleIndex, AclVectorFormat desiredFormat) => GetVectorSampleFixed(track, sampleIndex, false);
    internal static Vector4 GetScaleSample(AclTransformStreams track, int sampleIndex, AclVectorFormat desiredFormat) => GetVectorSampleFixed(track, sampleIndex, true);

    private static Vector4 GetVectorSample(AclTransformStreams track, int sampleIndex, bool scale)
    {
        AclTrackStream stream = scale ? track.Scales : track.Translations;
        AclSegmentContext segment = track.Segment;
        AclClipContext clip = segment.Clip;
        byte bitRate = stream.BitRate;
        if ((AclVectorFormat)stream.Format == AclVectorFormat.Vector3Variable && bitRate == 0)
            sampleIndex = 0;
        Vector4 value = LoadVectorSample(stream.Samples.AsSpan(sampleIndex * stream.SampleSize), (AclVectorFormat)stream.Format, bitRate);
        bool constant = scale ? track.IsScaleConstant : track.IsTranslationConstant;
        bool normalized = scale ? clip.AreScalesNormalized : clip.AreTranslationsNormalized;
        if (!constant && normalized && bitRate != 24)
        {
            if ((scale ? segment.AreScalesNormalized : segment.AreTranslationsNormalized) && bitRate != 0)
            {
                AclTrackStreamRange range = scale ? segment.Ranges[track.BoneIndex].Scale : segment.Ranges[track.BoneIndex].Translation;
                value = value * range.Extent + range.Min;
            }
            AclTrackStreamRange clipRange = scale ? clip.Ranges[track.BoneIndex].Scale : clip.Ranges[track.BoneIndex].Translation;
            value = value * clipRange.Extent + clipRange.Min;
        }
        return value;
    }

    private static Vector4 GetVectorSample(AclTransformStreams track, AclTransformStreams rawTrack, int sampleIndex, byte bitRate, bool scale)
    {
        AclTrackStream stream = scale ? track.Scales : track.Translations;
        AclTrackStream rawStream = scale ? rawTrack.Scales : rawTrack.Translations;
        AclSegmentContext segment = track.Segment;
        Vector4 value = bitRate == 0 ? AclBitPacking.DecayVector3U48(scale ? track.ConstantScale : track.ConstantTranslation)
            : bitRate == 24 ? rawStream.GetRawSample(segment.ClipSampleOffset + sampleIndex)
            : AclBitPacking.DecayVector3UXX(stream.GetRawSample(sampleIndex), BitRateNumBits[bitRate]);
        if (bitRate != 24)
        {
            if ((scale ? segment.AreScalesNormalized : segment.AreTranslationsNormalized) && bitRate != 0)
            {
                AclTrackStreamRange range = scale ? segment.Ranges[track.BoneIndex].Scale : segment.Ranges[track.BoneIndex].Translation;
                value = value * range.Extent + range.Min;
            }
            AclTrackStreamRange clipRange = scale ? segment.Clip.Ranges[track.BoneIndex].Scale : segment.Clip.Ranges[track.BoneIndex].Translation;
            value = value * clipRange.Extent + clipRange.Min;
        }
        return value;
    }

    private static Vector4 GetVectorSampleFixed(AclTransformStreams track, int sampleIndex, bool scale)
    {
        AclTrackStream stream = scale ? track.Scales : track.Translations;
        AclSegmentContext segment = track.Segment;
        Vector4 value = LoadVectorSample(stream.Samples.AsSpan(sampleIndex * stream.SampleSize), (AclVectorFormat)stream.Format, 0);
        bool normalized = scale ? segment.Clip.AreScalesNormalized && !track.IsScaleConstant : segment.Clip.AreTranslationsNormalized && !track.IsTranslationConstant;
        if (normalized)
        {
            if (scale ? segment.AreScalesNormalized : segment.AreTranslationsNormalized)
            {
                AclTrackStreamRange range = scale ? segment.Ranges[track.BoneIndex].Scale : segment.Ranges[track.BoneIndex].Translation;
                value = value * range.Extent + range.Min;
            }
            AclTrackStreamRange clipRange = scale ? segment.Clip.Ranges[track.BoneIndex].Scale : segment.Clip.Ranges[track.BoneIndex].Translation;
            value = value * clipRange.Extent + clipRange.Min;
        }
        return value;
    }

    internal static int GetUniformSampleKey(AclSegmentContext segment, float sampleTime)
    {
        AclClipContext clip = segment.Clip;
        float sampleIndex = sampleTime * clip.SampleRate;
        uint key0 = (uint)sampleIndex;
        uint key1 = Math.Min(key0 + 1, (uint)clip.NumSamples - 1);
        float alpha = sampleIndex - key0;
        alpha = MathF.Floor(alpha + 0.5f);
        key0 = unchecked(key0 - (uint)segment.ClipSampleOffset);
        if (key0 >= segment.NumSamples)
        {
            key0 = 0;
            alpha = 1.0f;
        }
        key1 = unchecked(key1 - (uint)segment.ClipSampleOffset);
        if (key1 >= segment.NumSamples)
        {
            key1 = (uint)segment.NumSamples - 1;
            alpha = 0.0f;
        }
        return (int)(alpha == 0.0f ? key0 : key1);
    }

    internal static void SampleStreams(AclTransformStreams[] streams, int numBones, float sampleTime, Span<AclTransform> output)
    {
        if (numBones == 0)
            return;
        int key = GetUniformSampleKey(streams[0].Segment, sampleTime);
        bool hasScale = streams[0].Segment.Clip.HasScale;
        for (int index = 0; index < numBones; index++)
        {
            AclTransformStreams track = streams[index];
            Quaternion rotation = track.IsRotationDefault ? track.DefaultValue.Rotation : AclMath.Normalize(GetRotationSample(track, track.IsRotationConstant ? 0 : key));
            Vector4 translation = track.IsTranslationDefault ? new Vector4(track.DefaultValue.Translation, 0.0f) : GetTranslationSample(track, track.IsTranslationConstant ? 0 : key);
            Vector4 scale = !hasScale || track.IsScaleDefault ? new Vector4(track.DefaultValue.Scale, 0.0f) : GetScaleSample(track, track.IsScaleConstant ? 0 : key);
            output[index] = new() { Rotation = rotation, Translation = AclMath.ToVector3(translation), Scale = AclMath.ToVector3(scale) };
        }
    }
}
