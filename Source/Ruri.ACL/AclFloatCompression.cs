namespace Ruri.ACL;

internal static partial class AclCompression
{
    internal static byte[] CompressFloats(AclFloatTrack[] tracks, float sampleRate, AclCompressionSettings settings)
    {
        int numTracks = tracks.Length;
        int numSamples = numTracks != 0 ? tracks[0].Samples.Length : 0;
        float[][] samples = new float[numTracks][];
        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            samples[trackIndex] = (float[])tracks[trackIndex].Samples.Clone();
            foreach (float sample in samples[trackIndex])
                if (!float.IsFinite(sample))
                    throw new ArgumentException("Some samples are not finite", nameof(tracks));
        }

        bool wrapping = false;
        if (settings.OptimizeLoops && numSamples > 1 && numTracks != 0)
        {
            wrapping = true;
            for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
            {
                float first = samples[trackIndex][0];
                float last = samples[trackIndex][numSamples - 1];
                if (MathF.Abs(first - last) > tracks[trackIndex].Precision)
                {
                    wrapping = false;
                    break;
                }
            }
            if (wrapping)
                numSamples--;
        }

        float[] minimum = new float[numTracks];
        float[] extent = new float[numTracks];
        bool[] constant = new bool[numTracks];
        byte[] bitRates = new byte[numTracks];
        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            float rangeMinimum = 1.0E10F;
            float rangeMaximum = -1.0E10F;
            for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
            {
                float sample = samples[trackIndex][sampleIndex];
                rangeMinimum = MathF.Min(rangeMinimum, sample);
                rangeMaximum = MathF.Max(rangeMaximum, sample);
            }
            minimum[trackIndex] = rangeMinimum;
            extent[trackIndex] = rangeMaximum - rangeMinimum;
            constant[trackIndex] = MathF.Abs(extent[trackIndex]) < tracks[trackIndex].Precision;
        }

        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            if (constant[trackIndex])
                continue;
            float rangeMinimum = minimum[trackIndex];
            float rangeExtent = extent[trackIndex];
            for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
            {
                float normalized = (samples[trackIndex][sampleIndex] - rangeMinimum) / rangeExtent;
                normalized = MathF.Min(normalized, 1.0F);
                samples[trackIndex][sampleIndex] = rangeExtent < 0.000000001F ? 0.0F : normalized;
            }
        }

        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            if (constant[trackIndex])
                continue;
            byte bestBitRate = 24;
            for (byte bitRate = 23; bitRate != 0; bitRate--)
            {
                float maximum = (1U << bitRate) - 1;
                float inverseMaximum = 1.0F / maximum;
                bool errorTooHigh = false;
                for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
                {
                    float packed = AclBitPacking.RoundSymmetric(samples[trackIndex][sampleIndex] * maximum);
                    float decayedNormalized = packed * inverseMaximum;
                    float decayedSample = decayedNormalized * extent[trackIndex] + minimum[trackIndex];
                    float delta = MathF.Abs(tracks[trackIndex].Samples[sampleIndex] - decayedSample);
                    if (!(delta <= tracks[trackIndex].Precision))
                    {
                        errorTooHigh = true;
                        break;
                    }
                }
                if (errorTooHigh)
                    break;
                bestBitRate = bitRate;
            }
            bitRates[trackIndex] = bestBitRate;
            if (bestBitRate == 24)
            {
                for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
                    samples[trackIndex][sampleIndex] = tracks[trackIndex].Samples[sampleIndex];
            }
            else
            {
                float maximum = (1U << bestBitRate) - 1;
                for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
                    samples[trackIndex][sampleIndex] = AclBitPacking.RoundSymmetric(samples[trackIndex][sampleIndex] * maximum);
            }
        }

        int constantSize = 0;
        int rangeSize = 0;
        int bitsPerFrame = 0;
        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            if (constant[trackIndex])
                constantSize += 4;
            else
            {
                bitsPerFrame += bitRates[trackIndex] == 24 ? 32 : bitRates[trackIndex];
                if (bitRates[trackIndex] != 24)
                    rangeSize += 8;
            }
        }
        int metadataOffset = 52;
        int constantOffset = AclBitPacking.Align(metadataOffset + numTracks, 4);
        int rangeOffset = constantOffset + constantSize;
        int animatedOffset = rangeOffset + rangeSize;
        int animatedBits = numSamples * bitsPerFrame;
        int animatedSize = (animatedBits + 7) / 8;
        byte[] output = new byte[animatedOffset + animatedSize + 15];
        AclBitPacking.WriteHeader(output, 0, numTracks, numSamples, sampleRate, wrapping ? 1U << 30 : 0U);
        AclBitPacking.WriteUInt32(output, 32, (uint)(numSamples != 0 ? bitsPerFrame : 0));
        AclBitPacking.WriteUInt32(output, 36, (uint)(metadataOffset - 32));
        AclBitPacking.WriteUInt32(output, 40, (uint)(constantOffset - 32));
        AclBitPacking.WriteUInt32(output, 44, (uint)(rangeOffset - 32));
        AclBitPacking.WriteUInt32(output, 48, (uint)(animatedOffset - 32));
        int constantCursor = constantOffset;
        int rangeCursor = rangeOffset;
        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            output[metadataOffset + trackIndex] = constant[trackIndex] ? (byte)0 : bitRates[trackIndex];
            if (constant[trackIndex])
            {
                AclBitPacking.WriteFloat(output, constantCursor, tracks[trackIndex].Samples[0]);
                constantCursor += 4;
            }
            else if (bitRates[trackIndex] != 24)
            {
                AclBitPacking.WriteFloat(output, rangeCursor, minimum[trackIndex]);
                AclBitPacking.WriteFloat(output, rangeCursor + 4, extent[trackIndex]);
                rangeCursor += 8;
            }
        }
        int bitCursor = animatedOffset * 8;
        for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
        {
            for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
            {
                if (constant[trackIndex])
                    continue;
                bool raw = bitRates[trackIndex] == 24;
                int bits = raw ? 32 : bitRates[trackIndex];
                uint value = raw ? BitConverter.SingleToUInt32Bits(tracks[trackIndex].Samples[sampleIndex])
                    : (uint)samples[trackIndex][sampleIndex];
                AclBitPacking.WriteBits(output, bitCursor, value, bits);
                bitCursor += bits;
            }
        }
        AclBitPacking.CompleteBuffer(output);
        return output;
    }
}
