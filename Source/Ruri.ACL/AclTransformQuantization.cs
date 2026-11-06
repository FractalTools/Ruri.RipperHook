using System.Numerics;

namespace Ruri.ACL;

internal sealed class AclQuantizationContext
{
    public readonly AclClipContext Clip;
    public readonly AclClipContext RawClip;
    public readonly AclCompressionSettings Settings;
    public AclSegmentContext Segment = null!;
    public AclTransformStreams[] BoneStreams = [];
    public readonly AclTransformStreams[] RawBoneStreams;
    public readonly int NumBones;
    public int NumSamples;
    public int SegmentSampleStartIndex;
    public readonly float SampleRate;
    public readonly float ClipDuration;
    public readonly bool HasScale;
    public readonly AclTrackBitRateDatabase BitRateDatabase;
    public readonly AclSingleTrackQuery LocalQuery;
    public readonly AclHierarchicalTrackQuery ObjectQuery;
    public readonly AclEveryTrackQuery AllLocalQuery;
    public readonly AclRigidShellMetadata[] ShellMetadataPerTransform;
    public readonly AclTransform[] RawLocalPose;
    public readonly AclTransform[] LossyLocalPose;
    public readonly AclTransform[] RawLocalTransforms;
    public readonly AclTransform[] RawObjectTransforms;
    public readonly AclTransform[] LossyObjectPose;
    public AclTransform[]? LossyTransformsStart;
    public AclTransform[]? LossyTransformsEnd;
    public AclTransformBitRates[] BitRatePerBone;
    public readonly int[] ParentTransformIndices;
    public readonly int[] SelfTransformIndices;
    public readonly int[] ChainBoneIndices;
    public int NumBonesInChain;

    public AclQuantizationContext(AclClipContext clip, AclClipContext rawClip, AclCompressionSettings settings)
    {
        Clip = clip;
        RawClip = rawClip;
        Settings = settings;
        RawBoneStreams = rawClip.Segments[0].BoneStreams;
        NumBones = clip.NumBones;
        SampleRate = clip.SampleRate;
        ClipDuration = clip.Duration;
        HasScale = clip.HasScale;
        BitRateDatabase = new(clip, rawClip, settings);
        LocalQuery = new(BitRateDatabase);
        ObjectQuery = new(BitRateDatabase, NumBones);
        AllLocalQuery = new(BitRateDatabase, NumBones);
        ShellMetadataPerTransform = new AclRigidShellMetadata[NumBones];
        RawLocalPose = new AclTransform[NumBones];
        LossyLocalPose = new AclTransform[NumBones];
        int cachedTransforms = NumBones * clip.Segments[0].NumSamples;
        RawLocalTransforms = new AclTransform[cachedTransforms];
        RawObjectTransforms = new AclTransform[cachedTransforms];
        LossyObjectPose = new AclTransform[NumBones];
        BitRatePerBone = new AclTransformBitRates[NumBones];
        ParentTransformIndices = new int[NumBones];
        SelfTransformIndices = new int[NumBones];
        ChainBoneIndices = new int[NumBones];
        for (int index = 0; index < NumBones; index++)
        {
            ParentTransformIndices[index] = clip.Metadata[index].ParentIndex;
            SelfTransformIndices[index] = index;
        }
    }

    public void SetSegment(AclSegmentContext segment)
    {
        Segment = segment;
        BoneStreams = segment.BoneStreams;
        NumSamples = segment.NumSamples;
        SegmentSampleStartIndex = segment.ClipSampleOffset;
        BitRateDatabase.SetSegment(segment);
        AclCompression.ComputeSegmentShellDistances(segment, ShellMetadataPerTransform);
        for (int sampleIndex = 0; sampleIndex < NumSamples; sampleIndex++)
        {
            float sampleTime = MathF.Min((segment.ClipSampleOffset + sampleIndex) / SampleRate, ClipDuration);
            AclCompression.SampleStreams(RawBoneStreams, NumBones, sampleTime, RawLocalPose);
            RawLocalPose.CopyTo(RawLocalTransforms, sampleIndex * NumBones);
            AclMath.LocalToObjectSpace(RawLocalPose, ParentTransformIndices, SelfTransformIndices, RawObjectTransforms.AsSpan(sampleIndex * NumBones, NumBones), HasScale);
        }
    }
}

internal static partial class AclCompression
{
    internal static Vector4 ConvertRotation(Vector4 rotation, AclRotationFormat from, AclRotationFormat to) => to == AclRotationFormat.QuatFull ? rotation : AclMath.ToVector(AclMath.EnsurePositiveW(AclMath.ToQuaternion(rotation)));

    private static AclRotationTrackStream QuantizeFixedRotationStream(AclRotationTrackStream rawStream, AclRotationFormat format)
    {
        int sampleSize = format == AclRotationFormat.QuatFull ? 16 : 12;
        var quantized = new AclRotationTrackStream(rawStream.NumSamples, sampleSize, rawStream.SampleRate, format);
        for (int sampleIndex = 0; sampleIndex < rawStream.NumSamples; sampleIndex++)
        {
            Vector4 value = rawStream.GetRawSample(sampleIndex);
            if (format == AclRotationFormat.QuatFull)
                AclBitPacking.PackVector4_128(value, quantized.GetRawSampleSpan(sampleIndex));
            else
                AclBitPacking.PackVector3_96(value, quantized.GetRawSampleSpan(sampleIndex));
        }
        return quantized;
    }

    private static AclRotationTrackStream QuantizeVariableRotationStream(AclQuantizationContext context, AclTransformStreams rawTrack, AclTransformStreams lossyTrack, byte bitRate)
    {
        int numSamples = bitRate == 0 ? 1 : lossyTrack.Rotations.NumSamples;
        var quantized = new AclRotationTrackStream(numSamples, 16, lossyTrack.Rotations.SampleRate, AclRotationFormat.QuatDropWVariable, bitRate);
        if (bitRate == 0)
            AclBitPacking.PackVector3U48(lossyTrack.ConstantRotation, quantized.GetRawSampleSpan(0));
        else
        {
            int numBits = BitRateNumBits[bitRate];
            for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
            {
                if (bitRate == 24)
                {
                    Vector4 value = ConvertRotation(rawTrack.Rotations.GetRawSample(context.SegmentSampleStartIndex + sampleIndex), AclRotationFormat.QuatFull, AclRotationFormat.QuatDropWVariable);
                    AclBitPacking.PackVector3_96(value, quantized.GetRawSampleSpan(sampleIndex));
                }
                else
                    AclBitPacking.PackVector3UXX(lossyTrack.Rotations.GetRawSample(sampleIndex), numBits, quantized.GetRawSampleSpan(sampleIndex));
            }
        }
        return quantized;
    }

    private static void QuantizeVectorStream(AclQuantizationContext context, AclTransformStreams lossyTrack, AclTransformStreams rawTrack, byte bitRate, bool scale)
    {
        bool isDefault = scale ? lossyTrack.IsScaleDefault : lossyTrack.IsTranslationDefault;
        if (isDefault)
            return;
        AclTrackStream stream = scale ? lossyTrack.Scales : lossyTrack.Translations;
        AclTrackStream rawStream = scale ? rawTrack.Scales : rawTrack.Translations;
        AclVectorFormat format = scale ? context.Settings.ScaleFormat : context.Settings.TranslationFormat;
        bool constant = scale ? lossyTrack.IsScaleConstant : lossyTrack.IsTranslationConstant;
        bool variable = format == AclVectorFormat.Vector3Variable && !constant;
        int numSamples = variable && bitRate == 0 ? 1 : stream.NumSamples;
        AclTrackStream quantized = scale
            ? new AclScaleTrackStream(numSamples, variable ? 16 : 12, stream.SampleRate, variable ? AclVectorFormat.Vector3Variable : AclVectorFormat.Vector3Full, variable ? bitRate : (byte)255)
            : new AclTranslationTrackStream(numSamples, variable ? 16 : 12, stream.SampleRate, variable ? AclVectorFormat.Vector3Variable : AclVectorFormat.Vector3Full, variable ? bitRate : (byte)255);
        if (variable && bitRate == 0)
            AclBitPacking.PackVector3U48(scale ? lossyTrack.ConstantScale : lossyTrack.ConstantTranslation, quantized.GetRawSampleSpan(0));
        else
        {
            for (int sampleIndex = 0; sampleIndex < numSamples; sampleIndex++)
            {
                Vector4 value = variable && bitRate == 24 ? rawStream.GetRawSample(context.SegmentSampleStartIndex + sampleIndex) : stream.GetRawSample(sampleIndex);
                if (variable && bitRate != 24)
                    AclBitPacking.PackVector3UXX(value, BitRateNumBits[bitRate], quantized.GetRawSampleSpan(sampleIndex));
                else
                    AclBitPacking.PackVector3_96(value, quantized.GetRawSampleSpan(sampleIndex));
            }
        }
        if (scale)
            lossyTrack.Scales = (AclScaleTrackStream)quantized;
        else
            lossyTrack.Translations = (AclTranslationTrackStream)quantized;
    }

    private static void QuantizeAllStreams(AclQuantizationContext context)
    {
        for (int index = 0; index < context.NumBones; index++)
        {
            AclTransformStreams lossyTrack = context.BoneStreams[index];
            AclTransformStreams rawTrack = context.RawBoneStreams[index];
            AclTransformBitRates bitRates = context.BitRatePerBone[index];
            if (!lossyTrack.IsRotationDefault)
            {
                if (context.Settings.RotationFormat == AclRotationFormat.QuatDropWVariable)
                    lossyTrack.Rotations = lossyTrack.IsRotationConstant ? QuantizeFixedRotationStream(lossyTrack.Rotations, AclRotationFormat.QuatDropWFull) : QuantizeVariableRotationStream(context, rawTrack, lossyTrack, bitRates.Rotation);
                else
                    lossyTrack.Rotations = QuantizeFixedRotationStream(lossyTrack.Rotations, context.Settings.RotationFormat);
            }
            QuantizeVectorStream(context, lossyTrack, rawTrack, bitRates.Translation, false);
            if (context.HasScale)
                QuantizeVectorStream(context, lossyTrack, rawTrack, bitRates.Scale, true);
        }
    }

    private static float CalculateMaxErrorAtBitRateLocal(AclQuantizationContext context, int targetBoneIndex, bool untilErrorTooHigh)
    {
        AclRigidShellMetadata shell = context.ShellMetadataPerTransform[targetBoneIndex];
        context.LocalQuery.Build(targetBoneIndex, context.BitRatePerBone[targetBoneIndex]);
        float sampleIndexFloat = context.SegmentSampleStartIndex;
        float maxError = 0.0f;
        for (int sampleIndex = 0; sampleIndex < context.NumSamples; sampleIndex++)
        {
            float sampleTime = MathF.Min(sampleIndexFloat / context.SampleRate, context.ClipDuration);
            context.BitRateDatabase.Sample(context.LocalQuery, sampleTime, context.LossyLocalPose);
            float error = AclMath.CalculateError(context.RawLocalTransforms[sampleIndex * context.NumBones + targetBoneIndex], context.LossyLocalPose[targetBoneIndex], shell.LocalShellDistance, context.HasScale);
            maxError = MathF.Max(maxError, error);
            if (untilErrorTooHigh && error >= shell.Precision)
                break;
            sampleIndexFloat += 1.0f;
        }
        return maxError;
    }

    private static float CalculateMaxErrorAtBitRateObject(AclQuantizationContext context, int targetBoneIndex, bool untilErrorTooHigh)
    {
        AclRigidShellMetadata shell = context.ShellMetadataPerTransform[targetBoneIndex];
        context.ObjectQuery.Build(targetBoneIndex, context.BitRatePerBone, context.BoneStreams);
        float sampleIndexFloat = context.SegmentSampleStartIndex;
        float maxError = 0.0f;
        for (int sampleIndex = 0; sampleIndex < context.NumSamples; sampleIndex++)
        {
            float sampleTime = MathF.Min(sampleIndexFloat / context.SampleRate, context.ClipDuration);
            context.BitRateDatabase.Sample(context.ObjectQuery, sampleTime, context.LossyLocalPose);
            AclMath.LocalToObjectSpace(context.LossyLocalPose, context.ParentTransformIndices, context.ChainBoneIndices.AsSpan(0, context.NumBonesInChain), context.LossyObjectPose, context.HasScale);
            float error = AclMath.CalculateError(context.RawObjectTransforms[sampleIndex * context.NumBones + targetBoneIndex], context.LossyObjectPose[targetBoneIndex], shell.LocalShellDistance, context.HasScale);
            maxError = MathF.Max(maxError, error);
            if (untilErrorTooHigh && error >= shell.Precision)
                break;
            sampleIndexFloat += 1.0f;
        }
        return maxError;
    }

    private static bool IsPermutationBitRateAllowed(byte original, byte candidate) => original == 1 ? candidate != 0 : original != 255 || candidate == 0;

    private static void CalculateLocalSpaceBitRates(AclQuantizationContext context)
    {
        byte[,] permutations = context.HasScale ? AclBitRatePermutations.WithScale : AclBitRatePermutations.NoScale;
        for (int boneIndex = 0; boneIndex < context.NumBones; boneIndex++)
        {
            float errorThreshold = context.ShellMetadataPerTransform[boneIndex].Precision;
            AclTransformBitRates initial = context.BitRatePerBone[boneIndex];
            if (initial.Rotation == 255 && initial.Translation == 255 && initial.Scale == 255)
                continue;
            AclTransformBitRates bestBitRates = initial;
            float bestError = 1.0e10f;
            int previousSize = -1;
            bool goodEnough = false;
            for (int index = 0; index < permutations.GetLength(0); index++)
            {
                byte rotation = permutations[index, 0];
                byte translation = permutations[index, 1];
                byte scale = context.HasScale ? permutations[index, 2] : (byte)0;
                if (!IsPermutationBitRateAllowed(initial.Rotation, rotation) || !IsPermutationBitRateAllowed(initial.Translation, translation) || (context.HasScale && !IsPermutationBitRateAllowed(initial.Scale, scale)))
                    continue;
                int size = BitRateNumBits[rotation] + BitRateNumBits[translation] + (context.HasScale ? BitRateNumBits[scale] : 0);
                if (size != previousSize && goodEnough)
                    break;
                previousSize = size;
                context.BitRatePerBone[boneIndex] = new() { Rotation = initial.Rotation != 255 ? rotation : (byte)255, Translation = initial.Translation != 255 ? translation : (byte)255, Scale = context.HasScale && initial.Scale != 255 ? scale : initial.Scale };
                float error = CalculateMaxErrorAtBitRateLocal(context, boneIndex, true);
                if (error < bestError)
                {
                    bestError = error;
                    bestBitRates = context.BitRatePerBone[boneIndex];
                    goodEnough = error < errorThreshold;
                }
            }
            context.BitRatePerBone[boneIndex] = bestBitRates;
        }
    }

    private static byte IncrementAndClampBitRate(byte bitRate, int increment) => bitRate >= 24 ? bitRate : (byte)Math.Min(bitRate + increment, 24);

    private static float IncreaseBoneBitRate(AclQuantizationContext context, int boneIndex, int numIncrements, float oldError, out AclTransformBitRates bestBitRates)
    {
        AclTransformBitRates initial = context.BitRatePerBone[boneIndex];
        bestBitRates = initial;
        float bestError = oldError;
        int numScaleIncrements = context.HasScale ? numIncrements : 0;
        for (int rotationIncrement = 0; rotationIncrement <= numIncrements; rotationIncrement++)
        {
            byte rotation = IncrementAndClampBitRate(initial.Rotation, rotationIncrement);
            for (int translationIncrement = 0; translationIncrement <= numIncrements; translationIncrement++)
            {
                byte translation = IncrementAndClampBitRate(initial.Translation, translationIncrement);
                for (int scaleIncrement = 0; scaleIncrement <= numScaleIncrements; scaleIncrement++)
                {
                    byte scale = IncrementAndClampBitRate(initial.Scale, scaleIncrement);
                    if (rotationIncrement + translationIncrement + scaleIncrement != numIncrements)
                    {
                        if (scale >= 24)
                            break;
                        continue;
                    }
                    context.BitRatePerBone[boneIndex] = new() { Rotation = rotation, Translation = translation, Scale = scale };
                    float error = CalculateMaxErrorAtBitRateObject(context, boneIndex, true);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestBitRates = context.BitRatePerBone[boneIndex];
                    }
                    context.BitRatePerBone[boneIndex] = initial;
                    if (scale >= 24)
                        break;
                }
                if (translation >= 24)
                    break;
            }
            if (rotation >= 24)
                break;
        }
        return bestError;
    }

    private static bool NextPermutation(Span<byte> values)
    {
        int first = values.Length - 2;
        while (first >= 0 && values[first] >= values[first + 1])
            first--;
        if (first < 0)
        {
            values.Reverse();
            return false;
        }
        int last = values.Length - 1;
        while (values[last] <= values[first])
            last--;
        (values[first], values[last]) = (values[last], values[first]);
        values[(first + 1)..].Reverse();
        return true;
    }

    private static float CalculateBonePermutationError(AclQuantizationContext context, AclTransformBitRates[] permutationBitRates, Span<byte> permutation, int boneIndex, AclTransformBitRates[] bestBitRates, float oldError)
    {
        float threshold = context.ShellMetadataPerTransform[boneIndex].Precision;
        float bestError = oldError;
        do
        {
            context.BitRatePerBone.CopyTo(permutationBitRates, 0);
            bool valid = false;
            for (int link = 0; link < context.NumBonesInChain; link++)
            {
                if (permutation[link] == 0)
                    continue;
                int chainBone = context.ChainBoneIndices[link];
                IncreaseBoneBitRate(context, chainBone, permutation[link], oldError, out AclTransformBitRates chainBest);
                valid |= !chainBest.Equals(permutationBitRates[chainBone]);
                permutationBitRates[chainBone] = chainBest;
            }
            if (!valid)
                continue;
            (context.BitRatePerBone, permutationBitRates) = (permutationBitRates, context.BitRatePerBone);
            float error = CalculateMaxErrorAtBitRateObject(context, boneIndex, true);
            (context.BitRatePerBone, permutationBitRates) = (permutationBitRates, context.BitRatePerBone);
            if (error < bestError)
            {
                bestError = error;
                permutationBitRates.CopyTo(bestBitRates, 0);
                if (error < threshold)
                    break;
            }
        } while (NextPermutation(permutation[..context.NumBonesInChain]));
        return bestError;
    }

    private static void InitializeBoneBitRates(AclQuantizationContext context)
    {
        AclSegmentContext segment = context.Segment;
        for (int index = 0; index < context.NumBones; index++)
        {
            AclTransformStreams track = segment.BoneStreams[index];
            context.BitRatePerBone[index] = new()
            {
                Rotation = context.Settings.RotationFormat == AclRotationFormat.QuatDropWVariable && !track.IsRotationConstant ? segment.AreRotationsNormalized ? (byte)0 : (byte)1 : (byte)255,
                Translation = context.Settings.TranslationFormat == AclVectorFormat.Vector3Variable && !track.IsTranslationConstant ? segment.AreTranslationsNormalized ? (byte)0 : (byte)1 : (byte)255,
                Scale = context.HasScale && context.Settings.ScaleFormat == AclVectorFormat.Vector3Variable && !track.IsScaleConstant ? segment.AreScalesNormalized ? (byte)0 : (byte)1 : (byte)255,
            };
        }
    }

    private static bool TryBonePermutation(AclQuantizationContext context, AclTransformBitRates[] permutationRates, byte[] permutation, int boneIndex, AclTransformBitRates[] bestPermutationRates, AclTransformBitRates[] bestRates, float originalError, ref float bestError, ref float error)
    {
        error = CalculateBonePermutationError(context, permutationRates, permutation, boneIndex, bestPermutationRates, originalError);
        if (error < bestError)
        {
            bestError = error;
            bestPermutationRates.CopyTo(bestRates, 0);
            return error < context.ShellMetadataPerTransform[boneIndex].Precision;
        }
        return false;
    }

    private static void FindOptimalBitRates(AclQuantizationContext context)
    {
        InitializeBoneBitRates(context);
        CalculateLocalSpaceBitRates(context);
        int numBones = context.NumBones;
        byte[] permutation = new byte[numBones];
        var permutationRates = new AclTransformBitRates[numBones];
        var bestPermutationRates = new AclTransformBitRates[numBones];
        var bestRates = (AclTransformBitRates[])context.BitRatePerBone.Clone();
        foreach (int boneIndex in context.RawClip.SortedTransformsParentFirst)
        {
            float threshold = context.ShellMetadataPerTransform[boneIndex].Precision;
            int chainCount = 0;
            foreach (int chainBone in context.Clip.GetBoneChain(boneIndex))
                context.ChainBoneIndices[chainCount++] = chainBone;
            context.NumBonesInChain = chainCount;
            float error = CalculateMaxErrorAtBitRateObject(context, boneIndex, true);
            if (error < threshold)
                continue;
            float initialError = error;
            while (error >= threshold)
            {
                float originalError = error;
                float bestError = error;
                Array.Clear(permutation);
                permutation[chainCount - 1] = 1;
                if (TryBonePermutation(context, permutationRates, permutation, boneIndex, bestPermutationRates, bestRates, originalError, ref bestError, ref error))
                    break;
                if ((byte)context.Settings.Level >= (byte)AclCompressionLevel.High)
                {
                    Array.Clear(permutation);
                    permutation[chainCount - 1] = 2;
                    if (TryBonePermutation(context, permutationRates, permutation, boneIndex, bestPermutationRates, bestRates, originalError, ref bestError, ref error))
                        break;
                    if (chainCount > 1)
                    {
                        Array.Clear(permutation);
                        permutation[chainCount - 2] = 1;
                        permutation[chainCount - 1] = 1;
                        if (TryBonePermutation(context, permutationRates, permutation, boneIndex, bestPermutationRates, bestRates, originalError, ref bestError, ref error))
                            break;
                    }
                }
                if ((byte)context.Settings.Level >= (byte)AclCompressionLevel.Highest)
                {
                    Array.Clear(permutation);
                    permutation[chainCount - 1] = 3;
                    if (TryBonePermutation(context, permutationRates, permutation, boneIndex, bestPermutationRates, bestRates, originalError, ref bestError, ref error))
                        break;
                    if (chainCount > 1)
                    {
                        Array.Clear(permutation);
                        permutation[chainCount - 2] = 2;
                        permutation[chainCount - 1] = 1;
                        if (TryBonePermutation(context, permutationRates, permutation, boneIndex, bestPermutationRates, bestRates, originalError, ref bestError, ref error))
                            break;
                        if (chainCount > 2)
                        {
                            Array.Clear(permutation);
                            permutation[chainCount - 3] = 1;
                            permutation[chainCount - 2] = 1;
                            permutation[chainCount - 1] = 1;
                            if (TryBonePermutation(context, permutationRates, permutation, boneIndex, bestPermutationRates, bestRates, originalError, ref bestError, ref error))
                                break;
                        }
                    }
                }
                if (bestError >= originalError)
                    break;
                error = bestError;
                if (error < originalError)
                    bestRates.CopyTo(context.BitRatePerBone, 0);
            }
            if (error < initialError)
                bestRates.CopyTo(context.BitRatePerBone, 0);
            error = CalculateMaxErrorAtBitRateObject(context, boneIndex, false);
            while (error >= threshold)
            {
                int numMaxedOut = 0;
                for (int link = chainCount - 1; link >= 0; link--)
                {
                    int chainBone = context.ChainBoneIndices[link];
                    AclTransformBitRates bestBoneRates = context.BitRatePerBone[chainBone];
                    float bestRateError = error;
                    while (error >= threshold)
                    {
                        ref AclTransformBitRates rates = ref context.BitRatePerBone[chainBone];
                        int smallest = rates.Translation < rates.Rotation ? 1 : 0;
                        if (rates.Scale < (smallest == 0 ? rates.Rotation : rates.Translation))
                            smallest = 2;
                        byte smallestRate = smallest == 0 ? rates.Rotation : smallest == 1 ? rates.Translation : rates.Scale;
                        if (smallestRate >= 24)
                        {
                            numMaxedOut++;
                            break;
                        }
                        if (rates.Rotation == rates.Translation && rates.Translation < 24 && rates.Scale >= 24)
                            rates.Translation++;
                        else if (smallest == 0)
                            rates.Rotation++;
                        else if (smallest == 1)
                            rates.Translation++;
                        else
                            rates.Scale++;
                        error = CalculateMaxErrorAtBitRateObject(context, boneIndex, false);
                        if (error < bestRateError)
                        {
                            bestBoneRates = context.BitRatePerBone[chainBone];
                            bestRateError = error;
                        }
                    }
                    context.BitRatePerBone[chainBone] = bestBoneRates;
                    error = bestRateError;
                    if (error < threshold)
                        break;
                }
                if (numMaxedOut == chainCount)
                    break;
            }
            if (error >= threshold && context.Settings.RotationFormat == AclRotationFormat.QuatFull)
            {
                for (int link = chainCount - 1; link >= 0; link--)
                {
                    ref AclTransformBitRates rates = ref context.BitRatePerBone[context.ChainBoneIndices[link]];
                    rates.Rotation = Math.Max(rates.Rotation, (byte)24);
                    rates.Translation = Math.Max(rates.Translation, (byte)24);
                    rates.Scale = Math.Max(rates.Scale, (byte)24);
                    error = CalculateMaxErrorAtBitRateObject(context, boneIndex, false);
                    if (error < threshold)
                        break;
                }
            }
        }
    }

    internal static void QuantizeStreams(AclClipContext clip, AclCompressionSettings settings, AclClipContext rawClip)
    {
        if (clip.NumBones == 0 || clip.NumSamples == 0)
            return;
        bool anyVariable = settings.RotationFormat == AclRotationFormat.QuatDropWVariable || settings.TranslationFormat == AclVectorFormat.Vector3Variable || settings.ScaleFormat == AclVectorFormat.Vector3Variable;
        var context = new AclQuantizationContext(clip, rawClip, settings);
        foreach (AclSegmentContext segment in clip.Segments)
        {
            context.SetSegment(segment);
            if (anyVariable)
                FindOptimalBitRates(context);
            if (settings.IsKeyframeStrippingEnabled)
                FindContributingError(context);
            QuantizeAllStreams(context);
        }
        if (settings.IsKeyframeStrippingEnabled)
            SortContributingError(clip);
    }
}
