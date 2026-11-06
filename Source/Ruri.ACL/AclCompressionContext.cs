using System.Numerics;
using System.Runtime.InteropServices;

namespace Ruri.ACL;

internal sealed class AclClipContext
{
    public AclSegmentContext[] Segments = [];
    public AclTransformRange[] Ranges = [];
    public AclTransformMetadata[] Metadata = [];
    public uint[] LeafTransformChains = [];
    public int[] SortedTransformsParentFirst = [];
    public AclRigidShellMetadata[] ClipShellMetadata = [];
    public AclKeyframeStrippingMetadata[]? ContributingError;
    public int NumSegments;
    public int NumBones;
    public int NumSamplesAllocated;
    public int NumSamples;
    public int NumLeafTransforms;
    public float SampleRate;
    public float Duration;
    public bool WrapOptimized;
    public bool AreRotationsNormalized;
    public bool AreTranslationsNormalized;
    public bool AreScalesNormalized;
    public bool HasScale;
    public bool HasStrippedKeyframes;
    public bool IsInitialized;

    public AclBoneChain GetBoneChain(int boneIndex)
    {
        AclTransformMetadata metadata = Metadata[boneIndex];
        return new AclBoneChain(metadata.TransformChain, metadata.TransformChainOffset, boneIndex);
    }
}

internal sealed class AclSegmentContext
{
    public AclClipContext Clip = null!;
    public AclTransformStreams[] BoneStreams = [];
    public AclTransformRange[] Ranges = [];
    public AclKeyframeStrippingMetadata[]? ContributingError;
    public int NumSamplesAllocated;
    public int NumSamples;
    public int NumBones;
    public int ClipSampleOffset;
    public int SegmentIndex;
    public uint HardKeyframes;
    public bool AreRotationsNormalized;
    public bool AreTranslationsNormalized;
    public bool AreScalesNormalized;
    public int AnimatedRotationBitSize;
    public int AnimatedTranslationBitSize;
    public int AnimatedScaleBitSize;
    public int AnimatedPoseBitSize;
    public int AnimatedDataSize;
    public int RangeDataSize;
    public int SegmentDataSize;
    public int TotalHeaderSize;
}

internal struct AclTransformMetadata
{
    public uint[] TransformChain;
    public int TransformChainOffset;
    public int ParentIndex;
    public float Precision;
    public float ShellDistance;
}

internal struct AclRigidShellMetadata
{
    public float LocalShellDistance;
    public float ParentShellDistance;
    public float Precision;
}

internal struct AclKeyframeStrippingMetadata
{
    public int KeyframeIndex;
    public int SegmentIndex;
    public int StrippingIndex;
    public float StrippingError;
    public bool IsKeyframeTrivial;

    public AclKeyframeStrippingMetadata(int keyframeIndex, int segmentIndex, int strippingIndex, float strippingError, bool isKeyframeTrivial)
    {
        KeyframeIndex = keyframeIndex;
        SegmentIndex = segmentIndex;
        StrippingIndex = strippingIndex;
        StrippingError = strippingError;
        IsKeyframeTrivial = isKeyframeTrivial;
    }
}

internal struct AclTrackStreamRange
{
    public Vector4 Min;
    public Vector4 Max;
    public Vector4 Extent;

    public static AclTrackStreamRange FromMinMax(Vector4 minimum, Vector4 maximum) => new() { Min = minimum, Max = maximum, Extent = maximum - minimum };
    public static AclTrackStreamRange FromMinExtent(Vector4 minimum, Vector4 extent) => new() { Min = minimum, Max = minimum + extent, Extent = extent };
    public readonly Vector4 GetCenter() => Extent * 0.5f + Min;

    public readonly bool IsConstant(float threshold)
    {
        if (threshold == 0.0f)
            return Min == Max;
        return Extent.X <= threshold && Extent.Y <= threshold && Extent.Z <= threshold && Extent.W <= threshold;
    }
}

internal struct AclTransformRange
{
    public AclTrackStreamRange Rotation;
    public AclTrackStreamRange Translation;
    public AclTrackStreamRange Scale;
}

internal enum AclAnimationTrackType : byte
{
    Rotation,
    Translation,
    Scale,
}

internal abstract class AclTrackStream
{
    public byte[] Samples;
    public int NumSamplesAllocated;
    public int NumSamples;
    public int SampleSize;
    public float SampleRate;
    public AclAnimationTrackType TrackType;
    public byte Format;
    public byte BitRate;

    protected AclTrackStream(int numSamples, int sampleSize, float sampleRate, AclAnimationTrackType trackType, byte format, byte bitRate)
    {
        Samples = new byte[numSamples * sampleSize + 15];
        NumSamplesAllocated = numSamples;
        NumSamples = numSamples;
        SampleSize = sampleSize;
        SampleRate = sampleRate;
        TrackType = trackType;
        Format = format;
        BitRate = bitRate;
    }

    public bool IsBitRateVariable => BitRate != 255;
    public float FiniteDuration => NumSamples > 1 ? (NumSamples - 1) / SampleRate : 0.0f;
    public int PackedSampleSize => TrackType == AclAnimationTrackType.Rotation && Format == (byte)AclRotationFormat.QuatFull ? 16 : 12;
    public Span<byte> GetRawSampleSpan(int sampleIndex) => Samples.AsSpan(sampleIndex * SampleSize, SampleSize);
    public Vector4 GetRawSample(int sampleIndex) => MemoryMarshal.Read<Vector4>(Samples.AsSpan(sampleIndex * SampleSize, 16));
    public void SetRawSample(int sampleIndex, Vector4 sample) => MemoryMarshal.Write(GetRawSampleSpan(sampleIndex), in sample);

    public void StripLastSample()
    {
        if (NumSamplesAllocated <= 1)
            return;
        NumSamples = NumSamplesAllocated - 1;
    }

    protected void DuplicateInto(AclTrackStream copy)
    {
        copy.NumSamplesAllocated = NumSamples;
        copy.NumSamples = NumSamples;
        copy.SampleSize = SampleSize;
        copy.SampleRate = SampleRate;
        copy.Format = Format;
        copy.BitRate = BitRate;
        copy.Samples = new byte[SampleSize * NumSamples + 15];
        Samples.AsSpan(0, SampleSize * NumSamples).CopyTo(copy.Samples);
    }
}

internal sealed class AclRotationTrackStream : AclTrackStream
{
    public AclRotationTrackStream(int numSamples = 0, int sampleSize = 0, float sampleRate = 0, AclRotationFormat format = AclRotationFormat.QuatFull, byte bitRate = 255)
        : base(numSamples, sampleSize, sampleRate, AclAnimationTrackType.Rotation, (byte)format, bitRate) { }

    public AclRotationFormat RotationFormat => (AclRotationFormat)Format;
    public Quaternion GetSample(int sampleIndex)
    {
        Vector4 value = GetRawSample(sampleIndex);
        return RotationFormat == AclRotationFormat.QuatFull ? new Quaternion(value.X, value.Y, value.Z, value.W) : AclMath.Normalize(AclMath.FromPositiveW(value));
    }
    public Quaternion GetSampleClamped(int sampleIndex) => GetSample(Math.Min(sampleIndex, NumSamples - 1));
    public AclRotationTrackStream Duplicate()
    {
        var copy = new AclRotationTrackStream();
        DuplicateInto(copy);
        return copy;
    }
}

internal sealed class AclTranslationTrackStream : AclTrackStream
{
    public AclTranslationTrackStream(int numSamples = 0, int sampleSize = 0, float sampleRate = 0, AclVectorFormat format = AclVectorFormat.Vector3Full, byte bitRate = 255)
        : base(numSamples, sampleSize, sampleRate, AclAnimationTrackType.Translation, (byte)format, bitRate) { }

    public AclVectorFormat VectorFormat => (AclVectorFormat)Format;
    public Vector4 GetSample(int sampleIndex) => GetRawSample(sampleIndex);
    public Vector4 GetSampleClamped(int sampleIndex) => GetSample(Math.Min(sampleIndex, NumSamples - 1));
    public AclTranslationTrackStream Duplicate()
    {
        var copy = new AclTranslationTrackStream();
        DuplicateInto(copy);
        return copy;
    }
}

internal sealed class AclScaleTrackStream : AclTrackStream
{
    public AclScaleTrackStream(int numSamples = 0, int sampleSize = 0, float sampleRate = 0, AclVectorFormat format = AclVectorFormat.Vector3Full, byte bitRate = 255)
        : base(numSamples, sampleSize, sampleRate, AclAnimationTrackType.Scale, (byte)format, bitRate) { }

    public AclVectorFormat VectorFormat => (AclVectorFormat)Format;
    public Vector4 GetSample(int sampleIndex) => GetRawSample(sampleIndex);
    public Vector4 GetSampleClamped(int sampleIndex) => GetSample(Math.Min(sampleIndex, NumSamples - 1));
    public AclScaleTrackStream Duplicate()
    {
        var copy = new AclScaleTrackStream();
        DuplicateInto(copy);
        return copy;
    }
}

internal sealed class AclTransformStreams
{
    public AclTransform DefaultValue = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
    public Vector4 ConstantRotation;
    public Vector4 ConstantTranslation;
    public Vector4 ConstantScale;
    public AclSegmentContext Segment = null!;
    public int BoneIndex = -1;
    public int ParentBoneIndex = -1;
    public int OutputIndex = -1;
    public AclRotationTrackStream Rotations = new();
    public AclTranslationTrackStream Translations = new();
    public AclScaleTrackStream Scales = new();
    public bool IsRotationConstant;
    public bool IsRotationDefault;
    public bool IsTranslationConstant;
    public bool IsTranslationDefault;
    public bool IsScaleConstant;
    public bool IsScaleDefault;
    public bool IsStrippedFromOutput => OutputIndex == -1;

    public AclTransformStreams Duplicate() => new()
    {
        DefaultValue = DefaultValue,
        ConstantRotation = ConstantRotation,
        ConstantTranslation = ConstantTranslation,
        ConstantScale = ConstantScale,
        Segment = Segment,
        BoneIndex = BoneIndex,
        ParentBoneIndex = ParentBoneIndex,
        OutputIndex = OutputIndex,
        Rotations = Rotations.Duplicate(),
        Translations = Translations.Duplicate(),
        Scales = Scales.Duplicate(),
        IsRotationConstant = IsRotationConstant,
        IsRotationDefault = IsRotationDefault,
        IsTranslationConstant = IsTranslationConstant,
        IsTranslationDefault = IsTranslationDefault,
        IsScaleConstant = IsScaleConstant,
        IsScaleDefault = IsScaleDefault,
    };
}

internal readonly struct AclBoneChain(uint[] chain, int chainOffset, int boneIndex)
{
    public Enumerator GetEnumerator() => new(chain, chainOffset, boneIndex);

    public struct Enumerator(uint[] chain, int chainOffset, int boneIndex)
    {
        private int offset = -1;
        public readonly int Current => offset;
        public bool MoveNext()
        {
            do { offset++; }
            while (offset <= boneIndex && (chain[chainOffset + (offset >> 5)] & (1u << (31 - (offset & 31)))) == 0);
            return offset <= boneIndex;
        }
    }
}

internal struct AclTransformBitRates
{
    public byte Rotation;
    public byte Translation;
    public byte Scale;
    public readonly bool Equals(AclTransformBitRates other) => Rotation == other.Rotation && Translation == other.Translation && Scale == other.Scale;
}

internal static partial class AclCompression
{
    internal static readonly byte[] BitRateNumBits = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 32];
}
