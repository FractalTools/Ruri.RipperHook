using System.Buffers.Binary;
using System.Numerics;

namespace Ruri.ACL;

internal static class AclBitPacking
{
    internal static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    internal static void WriteUInt32(Span<byte> data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, 4), value);

    internal static void WriteFloat(Span<byte> data, int offset, float value)
        => WriteUInt32(data, offset, BitConverter.SingleToUInt32Bits(value));

    internal static float ReadFloat(ReadOnlySpan<byte> data, int offset)
        => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));

    internal static uint Hash32(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261U;
        foreach (byte value in data)
            hash = unchecked((hash ^ value) * 16777619U);
        return hash;
    }

    internal static void CompleteBuffer(Span<byte> data)
    {
        WriteUInt32(data, 0, (uint)data.Length);
        WriteUInt32(data, 4, Hash32(data.Slice(8)));
    }

    internal static void WriteHeader(Span<byte> data, byte trackType, int numTracks, int numSamples, float sampleRate, uint misc)
    {
        WriteUInt32(data, 8, 0xAC11AC11U);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(12, 2), 10);
        data[14] = 0;
        data[15] = trackType;
        WriteUInt32(data, 16, (uint)numTracks);
        WriteUInt32(data, 20, numTracks != 0 ? (uint)numSamples : 0U);
        WriteFloat(data, 24, numTracks != 0 ? sampleRate : 0.0F);
        WriteUInt32(data, 28, misc);
    }

    internal static void CopyBits(Span<byte> destination, int destinationBitOffset, ReadOnlySpan<byte> source, int sourceBitOffset, int numBits)
    {
        while (numBits != 0)
        {
            int destinationByte = destinationBitOffset / 8;
            int sourceByte = sourceBitOffset / 8;
            int destinationShift = destinationBitOffset % 8;
            int sourceShift = sourceBitOffset % 8;
            int count = Math.Min(Math.Min(8 - destinationShift, 8 - sourceShift), numBits);
            uint destinationMask = ~(0xFFU >> destinationShift) | ~(0xFFU << (8 - count - destinationShift));
            uint sourceMask = 0xFFU >> (8 - count);
            uint oldValue = destination[destinationByte] & destinationMask;
            uint sourceValue = ((uint)source[sourceByte] >> (8 - sourceShift - count)) & sourceMask;
            destination[destinationByte] = (byte)(oldValue | (sourceValue << (8 - count - destinationShift)));
            numBits -= count;
            destinationBitOffset += count;
            sourceBitOffset += count;
        }
    }

    internal static void WriteBits(Span<byte> destination, int bitOffset, uint value, int numBits)
    {
        Span<byte> source = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(source, numBits == 32 ? value : value << (32 - numBits));
        CopyBits(destination, bitOffset, source, 0, numBits);
    }

    internal static uint ReadBits(ReadOnlySpan<byte> data, int bitOffset, int numBits)
    {
        uint result = 0;
        for (int index = 0; index < numBits; index++)
        {
            int position = bitOffset + index;
            result = (result << 1) | (uint)((data[position / 8] >> (7 - position % 8)) & 1);
        }
        return result;
    }

    internal static float RoundSymmetric(float value)
        => MathF.Abs(value) >= 8388608.0F || float.IsNaN(value)
            ? value
            : MathF.Truncate(value + MathF.CopySign(0.5F, value));

    internal static uint PackScalarUnsigned(float value, int numBits)
        => (uint)RoundSymmetric(value * ((1U << numBits) - 1));

    internal static void PackVector4_128(Vector4 value, Span<byte> output)
    {
        WriteFloat(output, 0, value.X);
        WriteFloat(output, 4, value.Y);
        WriteFloat(output, 8, value.Z);
        WriteFloat(output, 12, value.W);
    }

    internal static void PackVector3_96(Vector4 value, Span<byte> output)
    {
        WriteFloat(output, 0, value.X);
        WriteFloat(output, 4, value.Y);
        WriteFloat(output, 8, value.Z);
    }

    internal static Vector4 UnpackVector3_96(ReadOnlySpan<byte> data)
        => new(ReadFloat(data, 0), ReadFloat(data, 4), ReadFloat(data, 8), 0.0F);

    internal static void PackVector3U48(Vector4 value, Span<byte> output)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(0, 2), (ushort)PackScalarUnsigned(value.X, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(2, 2), (ushort)PackScalarUnsigned(value.Y, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(4, 2), (ushort)PackScalarUnsigned(value.Z, 16));
    }

    internal static Vector4 UnpackVector3U48(ReadOnlySpan<byte> data)
        => new Vector4(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2)), 0.0F) * (1.0F / 65535.0F);

    internal static void PackVector3UXX(Vector4 value, int numBits, Span<byte> output)
    {
        WriteBits(output, 0, PackScalarUnsigned(value.X, numBits), numBits);
        WriteBits(output, numBits, PackScalarUnsigned(value.Y, numBits), numBits);
        WriteBits(output, numBits * 2, PackScalarUnsigned(value.Z, numBits), numBits);
    }

    internal static Vector4 UnpackVector3UXX(int numBits, ReadOnlySpan<byte> data, int bitOffset = 0)
    {
        float inverseMaximum = numBits == 0 ? 1.0F : 1.0F / ((1U << numBits) - 1);
        return new Vector4(ReadBits(data, bitOffset, numBits),
            ReadBits(data, bitOffset + numBits, numBits),
            ReadBits(data, bitOffset + numBits * 2, numBits), 0.0F) * inverseMaximum;
    }

    internal static Vector4 DecayVector3U48(Vector4 value) => DecayVector3UXX(value, 16);

    internal static Vector4 DecayVector3UXX(Vector4 value, int numBits)
    {
        float maximum = (1U << numBits) - 1;
        float inverseMaximum = 1.0F / maximum;
        Vector4 scaled = value * maximum;
        return new Vector4(RoundSymmetric(scaled.X), RoundSymmetric(scaled.Y),
            RoundSymmetric(scaled.Z), RoundSymmetric(scaled.W)) * inverseMaximum;
    }
}

internal static partial class AclCompression
{
    internal static void CalculateAnimatedDataSize(AclClipContext clip)
    {
        foreach (AclSegmentContext segment in clip.Segments)
        {
            int rotationBits = 0;
            int translationBits = 0;
            int scaleBits = 0;
            foreach (AclTransformStreams bone in segment.BoneStreams)
            {
                if (!bone.IsRotationConstant)
                    rotationBits += PackingSampleBits(bone.Rotations);
                if (!bone.IsTranslationConstant)
                    translationBits += PackingSampleBits(bone.Translations);
                if (!bone.IsScaleConstant)
                    scaleBits += PackingSampleBits(bone.Scales);
            }
            segment.AnimatedRotationBitSize = rotationBits;
            segment.AnimatedTranslationBitSize = translationBits;
            segment.AnimatedScaleBitSize = scaleBits;
            segment.AnimatedPoseBitSize = rotationBits + translationBits + scaleBits;
            segment.AnimatedDataSize = (segment.AnimatedPoseBitSize * segment.NumSamples + 7) / 8;
        }
    }

    private static int PackingSampleBits(AclTrackStream stream)
        => stream.IsBitRateVariable ? BitRateNumBits[stream.BitRate] * 3 : stream.PackedSampleSize * 8;

    private static AclTrackStream PackingStream(AclTransformStreams bone, int type)
        => type == 0 ? bone.Rotations : type == 1 ? bone.Translations : bone.Scales;

    private static bool PackingConstant(AclTransformStreams bone, int type)
        => type == 0 ? bone.IsRotationConstant : type == 1 ? bone.IsTranslationConstant : bone.IsScaleConstant;

    private static bool PackingDefault(AclTransformStreams bone, int type)
        => type == 0 ? bone.IsRotationDefault : type == 1 ? bone.IsTranslationDefault : bone.IsScaleDefault;

    private static bool PackingVariable(AclCompressionSettings settings, int type)
        => type == 0 ? settings.RotationFormat == AclRotationFormat.QuatDropWVariable
            : type == 1 ? settings.TranslationFormat == AclVectorFormat.Vector3Variable
            : settings.ScaleFormat == AclVectorFormat.Vector3Variable;

    private static AclTrackStreamRange PackingRange(AclTransformRange range, int type)
        => type == 0 ? range.Rotation : type == 1 ? range.Translation : range.Scale;

    private static int[] PackingTracks(AclSegmentContext segment, int type, bool constant)
    {
        List<int> result = new();
        for (int index = 0; index < segment.NumBones; index++)
        {
            AclTransformStreams bone = segment.BoneStreams[index];
            if (constant ? PackingConstant(bone, type) && !PackingDefault(bone, type) : !PackingConstant(bone, type))
                result.Add(index);
        }
        return result.ToArray();
    }

    internal static byte[] PackTransforms(AclClipContext clip, AclCompressionSettings settings)
    {
        AclSegmentContext firstSegment = clip.Segments[0];
        int typeCount = clip.HasScale ? 3 : 2;
        int[][] animated = new int[typeCount][];
        int[][] constant = new int[typeCount][];
        int formatSize = 0;
        int constantSize = 0;
        int clipRangeSize = 0;
        for (int type = 0; type < typeCount; type++)
        {
            animated[type] = PackingTracks(firstSegment, type, false);
            constant[type] = PackingTracks(firstSegment, type, true);
            if (PackingVariable(settings, type))
            {
                formatSize += type == 0 ? AclBitPacking.Align(animated[type].Length, 4) : animated[type].Length;
                clipRangeSize += animated[type].Length * 24;
            }
            int sampleSize = type == 0 && settings.RotationFormat == AclRotationFormat.QuatFull ? 16 : 12;
            constantSize += constant[type].Length * sampleSize;
        }
        int startIndicesOffset = 84;
        int startIndicesSize = clip.NumSegments > 1 ? (clip.NumSegments + 1) * 4 : 0;
        int segmentHeaderStride = clip.HasStrippedKeyframes ? 20 : 16;
        int segmentHeadersOffset = AclBitPacking.Align(startIndicesOffset + startIndicesSize, 4);
        int subTrackTypesOffset = segmentHeadersOffset + clip.NumSegments * segmentHeaderStride;
        int packedEntryCount = (clip.NumBones + 15) / 16;
        int constantOffset = subTrackTypesOffset + packedEntryCount * typeCount * 4;
        int clipRangeOffset = AclBitPacking.Align(constantOffset + constantSize, 4);
        int[] segmentOffsets = new int[clip.NumSegments];
        int[] segmentRangeOffsets = new int[clip.NumSegments];
        int[] segmentAnimatedOffsets = new int[clip.NumSegments];
        int size = clipRangeOffset + clipRangeSize;
        for (int index = 0; index < clip.NumSegments; index++)
        {
            AclSegmentContext segment = clip.Segments[index];
            int begin = size;
            segmentOffsets[index] = size;
            size = AclBitPacking.Align(size + formatSize, 2);
            segmentRangeOffsets[index] = size;
            segment.RangeDataSize = clip.NumSegments > 1 ? formatSize * 6 : 0;
            size += segment.RangeDataSize;
            segment.TotalHeaderSize = size - begin;
            size = AclBitPacking.Align(size, 4);
            segmentAnimatedOffsets[index] = size;
            size += segment.AnimatedDataSize;
            segment.SegmentDataSize = size - begin;
        }
        byte[] output = new byte[size + 15];
        uint misc = (clip.HasScale ? 1U : 0U) | 2U | ((uint)settings.ScaleFormat << 2)
            | ((uint)settings.TranslationFormat << 3) | ((uint)settings.RotationFormat << 4) | (1U << 9)
            | (clip.HasStrippedKeyframes ? 1U << 10 : 0U) | (clip.WrapOptimized ? 1U << 30 : 0U);
        AclBitPacking.WriteHeader(output, 12, clip.NumBones, clip.NumSamples, clip.SampleRate, misc);
        AclBitPacking.WriteUInt32(output, 32, (uint)clip.NumSegments);
        AclBitPacking.WriteUInt32(output, 36, (uint)formatSize);
        AclBitPacking.WriteUInt32(output, 40, (uint)animated[0].Length);
        AclBitPacking.WriteUInt32(output, 44, (uint)animated[1].Length);
        AclBitPacking.WriteUInt32(output, 48, clip.HasScale ? (uint)animated[2].Length : 0U);
        AclBitPacking.WriteUInt32(output, 52, (uint)constant[0].Length);
        AclBitPacking.WriteUInt32(output, 56, (uint)constant[1].Length);
        AclBitPacking.WriteUInt32(output, 60, clip.HasScale ? (uint)constant[2].Length : 0U);
        AclBitPacking.WriteUInt32(output, 64, uint.MaxValue);
        AclBitPacking.WriteUInt32(output, 68, (uint)(segmentHeadersOffset - 32));
        AclBitPacking.WriteUInt32(output, 72, (uint)(subTrackTypesOffset - 32));
        AclBitPacking.WriteUInt32(output, 76, (uint)(constantOffset - 32));
        AclBitPacking.WriteUInt32(output, 80, (uint)(clipRangeOffset - 32));
        if (clip.NumSegments > 1)
        {
            for (int index = 0; index < clip.NumSegments; index++)
                AclBitPacking.WriteUInt32(output, startIndicesOffset + index * 4, (uint)clip.Segments[index].ClipSampleOffset);
            AclBitPacking.WriteUInt32(output, startIndicesOffset + clip.NumSegments * 4, uint.MaxValue);
        }
        for (int index = 0; index < clip.NumSegments; index++)
        {
            AclSegmentContext segment = clip.Segments[index];
            int offset = segmentHeadersOffset + index * segmentHeaderStride;
            AclBitPacking.WriteUInt32(output, offset, (uint)segment.AnimatedPoseBitSize);
            AclBitPacking.WriteUInt32(output, offset + 4, (uint)segment.AnimatedRotationBitSize);
            AclBitPacking.WriteUInt32(output, offset + 8, (uint)segment.AnimatedTranslationBitSize);
            AclBitPacking.WriteUInt32(output, offset + 12, (uint)(segmentOffsets[index] - 32));
            if (clip.HasStrippedKeyframes)
                AclBitPacking.WriteUInt32(output, offset + 16, segment.HardKeyframes);
        }
        for (int type = 0; type < typeCount; type++)
        {
            for (int trackIndex = 0; trackIndex < clip.NumBones; trackIndex++)
            {
                AclTransformStreams bone = firstSegment.BoneStreams[trackIndex];
                uint packedType = PackingDefault(bone, type) ? 0U : PackingConstant(bone, type) ? 1U : 2U;
                int offset = subTrackTypesOffset + type * packedEntryCount * 4 + (trackIndex / 16) * 4;
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(offset, 4));
                AclBitPacking.WriteUInt32(output, offset, entry | (packedType << ((15 - trackIndex % 16) * 2)));
            }
        }
        WritePackingConstants(output, constantOffset, firstSegment, constant, settings);
        WritePackingClipRanges(output, clipRangeOffset, clip, animated, settings);
        for (int index = 0; index < clip.NumSegments; index++)
        {
            AclSegmentContext segment = clip.Segments[index];
            WritePackingFormats(output, segmentOffsets[index], segment, animated, settings);
            if (segment.RangeDataSize != 0)
                WritePackingSegmentRanges(output, segmentRangeOffsets[index], segment, animated, settings);
            WritePackingAnimated(output, segmentAnimatedOffsets[index], segment, animated);
        }
        AclBitPacking.CompleteBuffer(output);
        return output;
    }

    private static void WritePackingConstants(byte[] output, int offset, AclSegmentContext segment, int[][] tracks, AclCompressionSettings settings)
    {
        for (int type = 0; type < tracks.Length; type++)
        {
            int[] indices = tracks[type];
            if (type == 0 && settings.RotationFormat != AclRotationFormat.QuatFull)
            {
                for (int group = 0; group < indices.Length; group += 4)
                {
                    int count = Math.Min(4, indices.Length - group);
                    for (int component = 0; component < 3; component++)
                    {
                        for (int lane = 0; lane < count; lane++)
                        {
                            ReadOnlySpan<byte> sample = segment.BoneStreams[indices[group + lane]].Rotations.GetRawSampleSpan(0);
                            sample.Slice(component * 4, 4).CopyTo(output.AsSpan(offset, 4));
                            offset += 4;
                        }
                    }
                }
            }
            else
            {
                int sampleSize = type == 0 ? 16 : 12;
                foreach (int index in indices)
                {
                    PackingStream(segment.BoneStreams[index], type).GetRawSampleSpan(0).Slice(0, sampleSize).CopyTo(output.AsSpan(offset, sampleSize));
                    offset += sampleSize;
                }
            }
        }
    }

    private static void WritePackingClipRanges(byte[] output, int offset, AclClipContext clip, int[][] tracks, AclCompressionSettings settings)
    {
        for (int type = 0; type < tracks.Length; type++)
        {
            if (!PackingVariable(settings, type))
                continue;
            int[] indices = tracks[type];
            for (int group = 0; group < indices.Length; group += 4)
            {
                int count = Math.Min(4, indices.Length - group);
                if (type == 0)
                {
                    for (int bound = 0; bound < 2; bound++)
                        for (int component = 0; component < 3; component++)
                            for (int lane = 0; lane < count; lane++)
                            {
                                AclTrackStreamRange range = PackingRange(clip.Ranges[indices[group + lane]], type);
                                Vector4 value = bound == 0 ? range.Min : range.Extent;
                                AclBitPacking.WriteFloat(output, offset, value[component]);
                                offset += 4;
                            }
                }
                else
                {
                    for (int lane = 0; lane < count; lane++)
                    {
                        AclTrackStreamRange range = PackingRange(clip.Ranges[indices[group + lane]], type);
                        AclBitPacking.PackVector3_96(range.Min, output.AsSpan(offset));
                        AclBitPacking.PackVector3_96(range.Extent, output.AsSpan(offset + 12));
                        offset += 24;
                    }
                }
            }
        }
    }

    private static void WritePackingFormats(byte[] output, int offset, AclSegmentContext segment, int[][] tracks, AclCompressionSettings settings)
    {
        for (int type = 0; type < tracks.Length; type++)
        {
            if (!PackingVariable(settings, type))
                continue;
            int start = offset;
            foreach (int index in tracks[type])
            {
                byte bitRate = PackingStream(segment.BoneStreams[index], type).BitRate;
                output[offset++] = bitRate == 24 ? (byte)31 : BitRateNumBits[bitRate];
            }
            if (type == 0)
                offset = start + AclBitPacking.Align(tracks[type].Length, 4);
        }
    }

    private static void WritePackingSegmentRanges(byte[] output, int offset, AclSegmentContext segment, int[][] tracks, AclCompressionSettings settings)
    {
        Span<byte> groupData = stackalloc byte[24];
        for (int type = 0; type < tracks.Length; type++)
        {
            if (!PackingVariable(settings, type))
                continue;
            int[] indices = tracks[type];
            for (int group = 0; group < indices.Length; group += 4)
            {
                groupData.Clear();
                int count = Math.Min(4, indices.Length - group);
                for (int lane = 0; lane < count; lane++)
                {
                    int index = indices[group + lane];
                    AclTrackStream stream = PackingStream(segment.BoneStreams[index], type);
                    if (stream.BitRate == 0)
                    {
                        ReadOnlySpan<byte> sample = stream.GetRawSampleSpan(0);
                        if (type == 0)
                        {
                            for (int component = 0; component < 3; component++)
                            {
                                groupData[lane + component * 8] = sample[component * 2 + 1];
                                groupData[lane + component * 8 + 4] = sample[component * 2];
                            }
                        }
                        else
                            sample.Slice(0, 6).CopyTo(groupData.Slice(lane * 6, 6));
                    }
                    else
                    {
                        AclTrackStreamRange range = PackingRange(segment.Ranges[index], type);
                        for (int bound = 0; bound < 2; bound++)
                        {
                            Vector4 value = bound == 0 ? range.Min : range.Extent;
                            for (int component = 0; component < 3; component++)
                            {
                                int destination = type == 0 ? lane + (bound * 3 + component) * 4 : lane * 6 + bound * 3 + component;
                                groupData[destination] = (byte)AclBitPacking.PackScalarUnsigned(value[component], 8);
                            }
                        }
                    }
                }
                int copySize = (type == 0 ? 4 : count) * 6;
                groupData.Slice(0, copySize).CopyTo(output.AsSpan(offset, copySize));
                offset += copySize;
            }
        }
    }

    private static void WritePackingAnimated(byte[] output, int offset, AclSegmentContext segment, int[][] tracks)
    {
        int bitOffset = offset * 8;
        for (int sampleIndex = 0; sampleIndex < segment.NumSamples; sampleIndex++)
        {
            if (segment.Clip.HasStrippedKeyframes && (segment.HardKeyframes & (1U << (31 - sampleIndex))) == 0)
                continue;
            for (int type = 0; type < tracks.Length; type++)
            {
                foreach (int index in tracks[type])
                {
                    AclTrackStream stream = PackingStream(segment.BoneStreams[index], type);
                    if (stream.BitRate == 0)
                        continue;
                    ReadOnlySpan<byte> sample = stream.GetRawSampleSpan(sampleIndex);
                    int sampleBits = PackingSampleBits(stream);
                    if (stream.IsBitRateVariable && stream.BitRate != 24)
                        AclBitPacking.CopyBits(output, bitOffset, sample, 0, sampleBits);
                    else
                    {
                        for (int component = 0; component < sampleBits / 32; component++)
                            AclBitPacking.WriteBits(output, bitOffset + component * 32,
                                BinaryPrimitives.ReadUInt32LittleEndian(sample.Slice(component * 4, 4)), 32);
                    }
                    bitOffset += sampleBits;
                }
            }
        }
    }
}
