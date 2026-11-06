using System.Numerics;

namespace Ruri.ACL;

internal static partial class AclCompression
{
    internal static AclClipContext InitializeClipContext(AclTransformTrack[] tracks, float sampleRate, AclCompressionSettings settings)
    {
        int numTransforms = tracks.Length;
        int numSamples = numTransforms != 0 ? tracks[0].Samples.Length : 0;
        var context = new AclClipContext
        {
            Metadata = new AclTransformMetadata[numTransforms],
            SortedTransformsParentFirst = new int[numTransforms],
            NumSegments = 1,
            NumBones = numTransforms,
            NumSamplesAllocated = numSamples,
            NumSamples = numSamples,
            SampleRate = sampleRate,
            Duration = numSamples > 1 ? (numSamples - 1) / sampleRate : 0.0f,
            HasScale = true,
            IsInitialized = true
        };
        var segment = new AclSegmentContext
        {
            Clip = context,
            BoneStreams = new AclTransformStreams[numTransforms],
            NumSamplesAllocated = numSamples,
            NumSamples = numSamples,
            NumBones = numTransforms
        };
        context.Segments = [segment];
        bool valid = true;
        for (int transformIndex = 0; transformIndex < numTransforms; ++transformIndex)
        {
            AclTransformTrack track = tracks[transformIndex];
            var stream = new AclTransformStreams
            {
                Segment = segment,
                BoneIndex = transformIndex,
                ParentBoneIndex = track.ParentIndex,
                OutputIndex = transformIndex,
                Rotations = new AclRotationTrackStream(numSamples, 16, sampleRate),
                Translations = new AclTranslationTrackStream(numSamples, 16, sampleRate),
                Scales = new AclScaleTrackStream(numSamples, 16, sampleRate)
            };
            segment.BoneStreams[transformIndex] = stream;
            for (int sampleIndex = 0; sampleIndex < numSamples; ++sampleIndex)
            {
                AclTransform value = track.Samples[sampleIndex];
                Quaternion rotation = settings.RotationFormat != AclRotationFormat.QuatFull || MathF.Abs(value.Rotation.LengthSquared() - 1.0f) > 0.00001f ? AclMath.Normalize(value.Rotation) : value.Rotation;
                valid &= float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W);
                valid &= IsFinite3(value.Translation) && IsFinite3(value.Scale);
                stream.Rotations.SetRawSample(sampleIndex, new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W));
                stream.Translations.SetRawSample(sampleIndex, new Vector4(value.Translation, 0.0f));
                stream.Scales.SetRawSample(sampleIndex, new Vector4(value.Scale, 0.0f));
            }
            context.Metadata[transformIndex] = new AclTransformMetadata
            {
                ParentIndex = track.ParentIndex,
                Precision = track.Precision,
                ShellDistance = track.ShellDistance
            };
            context.SortedTransformsParentFirst[transformIndex] = transformIndex;
        }
        if (!valid)
            throw new ArgumentException("Some samples are not finite", nameof(tracks));

        if (numTransforms != 0)
        {
            bool[] leaves = new bool[numTransforms];
            Array.Fill(leaves, true);
            for (int transformIndex = 0; transformIndex < numTransforms; ++transformIndex)
                if (context.Metadata[transformIndex].ParentIndex != -1)
                    leaves[context.Metadata[transformIndex].ParentIndex] = false;
            int words = (numTransforms + 31) / 32;
            context.NumLeafTransforms = leaves.Count(value => value);
            context.LeafTransformChains = new uint[context.NumLeafTransforms * words];
            int leafIndex = 0;
            for (int transformIndex = 0; transformIndex < numTransforms; ++transformIndex)
            {
                if (!leaves[transformIndex])
                    continue;
                int offset = leafIndex * words;
                int chainIndex = transformIndex;
                while (chainIndex != -1)
                {
                    context.LeafTransformChains[offset + (chainIndex >> 5)] |= 1u << (31 - (chainIndex & 31));
                    ref AclTransformMetadata metadata = ref context.Metadata[chainIndex];
                    if (metadata.TransformChain == null)
                    {
                        metadata.TransformChain = context.LeafTransformChains;
                        metadata.TransformChainOffset = offset;
                    }
                    chainIndex = metadata.ParentIndex;
                }
                ++leafIndex;
            }
            Array.Sort(context.SortedTransformsParentFirst, (left, right) =>
            {
                int leftParent = context.Metadata[left].ParentIndex;
                int rightParent = context.Metadata[right].ParentIndex;
                return leftParent != rightParent ? leftParent.CompareTo(rightParent) : left.CompareTo(right);
            });
        }
        return context;
    }

    internal static AclRigidShellMetadata[] ComputeClipShellDistances(AclClipContext clip)
    {
        if (clip.NumBones == 0 || clip.NumSamples == 0)
            return [];
        var shells = new AclRigidShellMetadata[clip.NumBones];
        for (int transformIndex = 0; transformIndex < clip.NumBones; ++transformIndex)
        {
            shells[transformIndex].LocalShellDistance = clip.Metadata[transformIndex].ShellDistance;
            shells[transformIndex].Precision = clip.Metadata[transformIndex].Precision;
        }
        AclSegmentContext segment = clip.Segments[0];
        for (int index = clip.NumBones - 1; index >= 0; --index)
        {
            int transformIndex = clip.SortedTransformsParentFirst[index];
            ref AclRigidShellMetadata shell = ref shells[transformIndex];
            AclTransformStreams stream = segment.BoneStreams[transformIndex];
            float parentDistance = 0.0f;
            for (int sampleIndex = 0; sampleIndex < clip.NumSamples; ++sampleIndex)
            {
                AclTransform value = GetStreamTransform(stream, sampleIndex);
                float x = AclMath.TransformPoint(new Vector3(shell.LocalShellDistance, 0.0f, 0.0f), value).Length();
                float y = AclMath.TransformPoint(new Vector3(0.0f, shell.LocalShellDistance, 0.0f), value).Length();
                float z = AclMath.TransformPoint(new Vector3(0.0f, 0.0f, shell.LocalShellDistance), value).Length();
                parentDistance = Math.Max(parentDistance, Math.Max(Math.Max(x, y), z));
            }
            shell.ParentShellDistance = parentDistance;
            ref AclTransformMetadata metadata = ref clip.Metadata[transformIndex];
            if (shell.LocalShellDistance != metadata.ShellDistance)
                shell.ParentShellDistance += metadata.Precision;
            if (metadata.ParentIndex != -1 && shell.ParentShellDistance > shells[metadata.ParentIndex].LocalShellDistance)
            {
                shells[metadata.ParentIndex].LocalShellDistance = shell.ParentShellDistance;
                shells[metadata.ParentIndex].Precision = shell.Precision;
            }
        }
        return shells;
    }

    internal static void ComputeSegmentShellDistances(AclSegmentContext segment, AclRigidShellMetadata[] output)
    {
        AclClipContext clip = segment.Clip;
        if (segment.NumBones == 0 || segment.NumSamples == 0)
            return;
        for (int transformIndex = 0; transformIndex < clip.NumBones; ++transformIndex)
        {
            output[transformIndex].LocalShellDistance = clip.Metadata[transformIndex].ShellDistance;
            output[transformIndex].Precision = clip.Metadata[transformIndex].Precision;
            output[transformIndex].ParentShellDistance = 0.0f;
        }
        for (int index = clip.NumBones - 1; index >= 0; --index)
        {
            int transformIndex = clip.SortedTransformsParentFirst[index];
            ref AclRigidShellMetadata shell = ref output[transformIndex];
            AclTransformStreams stream = segment.BoneStreams[transformIndex];
            float parentDistance = 0.0f;
            for (int sampleIndex = 0; sampleIndex < segment.NumSamples; ++sampleIndex)
            {
                AclTransform value = GetStreamTransform(stream, sampleIndex);
                float x = AclMath.TransformPoint(new Vector3(shell.LocalShellDistance, 0.0f, 0.0f), value).Length();
                float y = AclMath.TransformPoint(new Vector3(0.0f, shell.LocalShellDistance, 0.0f), value).Length();
                float z = AclMath.TransformPoint(new Vector3(0.0f, 0.0f, shell.LocalShellDistance), value).Length();
                parentDistance = Math.Max(parentDistance, Math.Max(Math.Max(x, y), z));
            }
            shell.ParentShellDistance = parentDistance;
            ref AclTransformMetadata metadata = ref clip.Metadata[transformIndex];
            if (shell.LocalShellDistance != metadata.ShellDistance)
                shell.ParentShellDistance += metadata.Precision;
            if (metadata.ParentIndex != -1 && shell.ParentShellDistance > output[metadata.ParentIndex].LocalShellDistance)
            {
                output[metadata.ParentIndex].LocalShellDistance = shell.ParentShellDistance;
                output[metadata.ParentIndex].Precision = shell.Precision;
            }
        }
    }

    internal static void OptimizeLooping(AclClipContext context, AclCompressionSettings settings)
    {
        if (!settings.OptimizeLoops || context.WrapOptimized)
            return;
        if (settings.RotationFormat == AclRotationFormat.QuatFull && settings.TranslationFormat == AclVectorFormat.Vector3Full && settings.ScaleFormat == AclVectorFormat.Vector3Full)
            return;
        if (context.NumSamples <= 1 || context.NumBones == 0)
            return;
        AclSegmentContext segment = context.Segments[0];
        for (int transformIndex = 0; transformIndex < context.NumBones; ++transformIndex)
        {
            AclTransformStreams stream = segment.BoneStreams[transformIndex];
            AclRigidShellMetadata shell = context.ClipShellMetadata[transformIndex];
            float error = AclMath.CalculateError(GetStreamTransform(stream, 0), GetStreamTransform(stream, context.NumSamples - 1), shell.LocalShellDistance, context.HasScale);
            if (error > shell.Precision)
                return;
        }
        --context.NumSamples;
        context.WrapOptimized = true;
        --segment.NumSamples;
        foreach (AclTransformStreams stream in segment.BoneStreams)
        {
            stream.Rotations.StripLastSample();
            stream.Translations.StripLastSample();
            if (context.HasScale)
                stream.Scales.StripLastSample();
        }
    }

    internal static void ConvertRotationStreams(AclClipContext clip, AclRotationFormat format)
    {
        AclRotationFormat highPrecisionFormat = format == AclRotationFormat.QuatFull ? AclRotationFormat.QuatFull : AclRotationFormat.QuatDropWFull;
        foreach (AclSegmentContext segment in clip.Segments)
        foreach (AclTransformStreams stream in segment.BoneStreams)
        {
            AclRotationTrackStream old = stream.Rotations;
            var converted = new AclRotationTrackStream(old.NumSamples, 16, old.SampleRate, highPrecisionFormat);
            for (int sampleIndex = 0; sampleIndex < old.NumSamples; ++sampleIndex)
            {
                Vector4 value = old.GetRawSample(sampleIndex);
                if (highPrecisionFormat == AclRotationFormat.QuatDropWFull)
                    value = AclMath.ToVector(AclMath.EnsurePositiveW(AclMath.ToQuaternion(value)));
                converted.SetRawSample(sampleIndex, value);
            }
            stream.Rotations = converted;
        }
    }

    internal static void ExtractClipBoneRanges(AclClipContext clip) => clip.Ranges = ExtractBoneRanges(clip.Segments[0]);

    private static AclTransformRange[] ExtractBoneRanges(AclSegmentContext segment)
    {
        var result = new AclTransformRange[segment.NumBones];
        for (int transformIndex = 0; transformIndex < segment.NumBones; ++transformIndex)
        {
            AclTransformStreams stream = segment.BoneStreams[transformIndex];
            result[transformIndex].Rotation = CalculateTrackRange(stream.Rotations, true);
            result[transformIndex].Translation = CalculateTrackRange(stream.Translations, false);
            if (segment.Clip.HasScale)
                result[transformIndex].Scale = CalculateTrackRange(stream.Scales, false);
        }
        return result;
    }

    private static AclTrackStreamRange CalculateTrackRange(AclTrackStream stream, bool isVector4)
    {
        if (stream.NumSamples == 0)
            return AclTrackStreamRange.FromMinMax(Vector4.Zero, Vector4.Zero);
        Vector4 minimum = new(1e10f);
        Vector4 maximum = new(-1e10f);
        for (int sampleIndex = 0; sampleIndex < stream.NumSamples; ++sampleIndex)
        {
            Vector4 value = stream.GetRawSample(sampleIndex);
            minimum = Vector4.Min(minimum, value);
            maximum = Vector4.Max(maximum, value);
        }
        if (!isVector4)
        {
            minimum.W = 0.0f;
            maximum.W = 0.0f;
        }
        return AclTrackStreamRange.FromMinMax(minimum, maximum);
    }

    internal static void CompactConstantStreams(AclClipContext clip, AclCompressionSettings settings)
    {
        AclSegmentContext segment = clip.Segments[0];
        int defaultScales = 0;
        for (int transformIndex = 0; transformIndex < clip.NumBones; ++transformIndex)
        {
            AclTransformStreams stream = segment.BoneStreams[transformIndex];
            ref AclTransformRange range = ref clip.Ranges[transformIndex];
            bool rotationConstant = clip.NumSamples == 0 || (settings.RotationFormat == AclRotationFormat.QuatFull ? range.Rotation.IsConstant(0.0f) : AreSamplesConstant(clip, transformIndex, clip.NumSamples != 0 ? stream.Rotations.GetSample(0).AsVector4() : Quaternion.Identity.AsVector4(), AclAnimationTrackType.Rotation));
            if (rotationConstant)
            {
                Vector4 rotation = clip.NumSamples != 0 ? stream.Rotations.GetRawSample(0) : Quaternion.Identity.AsVector4();
                bool isDefault = clip.NumSamples == 0 || (settings.RotationFormat == AclRotationFormat.QuatFull ? rotation == Quaternion.Identity.AsVector4() : AreSamplesConstant(clip, transformIndex, Quaternion.Identity.AsVector4(), AclAnimationTrackType.Rotation));
                if (isDefault)
                    rotation = Quaternion.Identity.AsVector4();
                var compact = new AclRotationTrackStream(1, 16, clip.SampleRate, stream.Rotations.RotationFormat);
                compact.SetRawSample(0, rotation);
                stream.Rotations = compact;
                stream.IsRotationConstant = true;
                stream.IsRotationDefault = isDefault;
                range.Rotation = AclTrackStreamRange.FromMinExtent(rotation, Vector4.Zero);
            }
            bool translationConstant = clip.NumSamples == 0 || (settings.TranslationFormat == AclVectorFormat.Vector3Full ? range.Translation.IsConstant(0.0f) : AreSamplesConstant(clip, transformIndex, stream.Translations.GetSample(0), AclAnimationTrackType.Translation));
            if (translationConstant)
            {
                Vector4 translation = clip.NumSamples != 0 ? stream.Translations.GetRawSample(0) : Vector4.Zero;
                bool isDefault = clip.NumSamples == 0 || (settings.TranslationFormat == AclVectorFormat.Vector3Full ? translation == Vector4.Zero : AreSamplesConstant(clip, transformIndex, Vector4.Zero, AclAnimationTrackType.Translation));
                if (isDefault)
                    translation = Vector4.Zero;
                var compact = new AclTranslationTrackStream(1, 16, clip.SampleRate, stream.Translations.VectorFormat);
                compact.SetRawSample(0, translation);
                stream.Translations = compact;
                stream.IsTranslationConstant = true;
                stream.IsTranslationDefault = isDefault;
                translation.W = 0.0f;
                range.Translation = AclTrackStreamRange.FromMinExtent(translation, Vector4.Zero);
            }
            bool scaleConstant = clip.NumSamples == 0 || !clip.HasScale || (settings.ScaleFormat == AclVectorFormat.Vector3Full ? range.Scale.IsConstant(0.0f) : AreSamplesConstant(clip, transformIndex, stream.Scales.GetSample(0), AclAnimationTrackType.Scale));
            if (scaleConstant)
            {
                Vector4 scale = clip.HasScale && clip.NumSamples != 0 ? stream.Scales.GetRawSample(0) : new Vector4(Vector3.One, 0.0f);
                bool isDefault = clip.NumSamples == 0 || !clip.HasScale || (settings.ScaleFormat == AclVectorFormat.Vector3Full ? scale == new Vector4(Vector3.One, 0.0f) : AreSamplesConstant(clip, transformIndex, new Vector4(Vector3.One, 0.0f), AclAnimationTrackType.Scale));
                if (isDefault)
                    scale = new Vector4(Vector3.One, 0.0f);
                var compact = new AclScaleTrackStream(1, 16, clip.SampleRate, stream.Scales.VectorFormat);
                compact.SetRawSample(0, scale);
                stream.Scales = compact;
                stream.IsScaleConstant = true;
                stream.IsScaleDefault = isDefault;
                scale.W = 0.0f;
                range.Scale = AclTrackStreamRange.FromMinExtent(scale, Vector4.Zero);
                if (isDefault)
                    ++defaultScales;
            }
        }
        clip.HasScale = defaultScales != clip.NumBones;
    }

    private static bool AreSamplesConstant(AclClipContext clip, int transformIndex, Vector4 reference, AclAnimationTrackType type)
    {
        AclTransformStreams stream = clip.Segments[0].BoneStreams[transformIndex];
        AclRigidShellMetadata shell = clip.ClipShellMetadata[transformIndex];
        for (int sampleIndex = 0; sampleIndex < clip.NumSamples; ++sampleIndex)
        {
            AclTransform raw = GetStreamTransform(stream, sampleIndex);
            AclTransform lossy = raw;
            switch (type)
            {
                case AclAnimationTrackType.Rotation:
                    lossy.Rotation = new Quaternion(reference.X, reference.Y, reference.Z, reference.W);
                    break;
                case AclAnimationTrackType.Translation:
                    lossy.Translation = new Vector3(reference.X, reference.Y, reference.Z);
                    break;
                case AclAnimationTrackType.Scale:
                    lossy.Scale = new Vector3(reference.X, reference.Y, reference.Z);
                    break;
            }
            if (AclMath.CalculateError(raw, lossy, shell.LocalShellDistance, clip.HasScale) > shell.Precision)
                return false;
        }
        return true;
    }

    internal static void NormalizeClipStreams(AclClipContext clip, AclCompressionSettings settings)
    {
        AclSegmentContext segment = clip.Segments[0];
        bool rotation = settings.RotationFormat == AclRotationFormat.QuatDropWVariable;
        bool translation = settings.TranslationFormat == AclVectorFormat.Vector3Variable;
        bool scale = clip.HasScale && settings.ScaleFormat == AclVectorFormat.Vector3Variable;
        NormalizeStreams(segment, clip.Ranges, rotation, translation, scale);
        clip.AreRotationsNormalized = rotation;
        clip.AreTranslationsNormalized = translation;
        clip.AreScalesNormalized = scale;
    }

    private static void NormalizeStreams(AclSegmentContext segment, AclTransformRange[] ranges, bool rotations, bool translations, bool scales)
    {
        for (int transformIndex = 0; transformIndex < segment.NumBones; ++transformIndex)
        {
            AclTransformStreams stream = segment.BoneStreams[transformIndex];
            if (rotations && !stream.IsRotationConstant)
                NormalizeStream(stream.Rotations, ranges[transformIndex].Rotation);
            if (translations && !stream.IsTranslationConstant)
                NormalizeStream(stream.Translations, ranges[transformIndex].Translation);
            if (scales && !stream.IsScaleConstant)
                NormalizeStream(stream.Scales, ranges[transformIndex].Scale);
        }
    }

    private static void NormalizeStream(AclTrackStream stream, AclTrackStreamRange range)
    {
        for (int sampleIndex = 0; sampleIndex < stream.NumSamples; ++sampleIndex)
            stream.SetRawSample(sampleIndex, NormalizeSample(stream.GetRawSample(sampleIndex), range));
    }

    internal static Vector4 NormalizeSample(Vector4 sample, AclTrackStreamRange range)
    {
        Vector4 normalized = Vector4.Min((sample - range.Min) / range.Extent, Vector4.One);
        if (range.Extent.X < 0.000000001f) normalized.X = 0.0f;
        if (range.Extent.Y < 0.000000001f) normalized.Y = 0.0f;
        if (range.Extent.Z < 0.000000001f) normalized.Z = 0.0f;
        if (range.Extent.W < 0.000000001f) normalized.W = 0.0f;
        return normalized;
    }

    internal static void SegmentStreams(AclClipContext clip)
    {
        const int idealNumSamples = 16;
        const int maxNumSamples = 31;
        if (clip.NumSamples <= maxNumSamples)
            return;
        int estimated = (clip.NumSamples + idealNumSamples - 1) / idealNumSamples;
        int[] counts = new int[estimated];
        Array.Fill(counts, idealNumSamples);
        int last = estimated - 1;
        counts[last] = idealNumSamples - (estimated * idealNumSamples - clip.NumSamples);
        int numSegments = estimated;
        if (last * (maxNumSamples - idealNumSamples) >= counts[last])
        {
            while (counts[last] != 0)
                for (int segmentIndex = 0; segmentIndex < last && counts[last] != 0; ++segmentIndex)
                {
                    ++counts[segmentIndex];
                    --counts[last];
                }
            --numSegments;
        }
        AclSegmentContext original = clip.Segments[0];
        clip.Segments = new AclSegmentContext[numSegments];
        clip.NumSegments = numSegments;
        int offset = 0;
        for (int segmentIndex = 0; segmentIndex < numSegments; ++segmentIndex)
        {
            int count = counts[segmentIndex];
            var segment = new AclSegmentContext
            {
                Clip = clip,
                BoneStreams = new AclTransformStreams[clip.NumBones],
                NumBones = clip.NumBones,
                NumSamplesAllocated = count,
                NumSamples = count,
                ClipSampleOffset = offset,
                SegmentIndex = segmentIndex
            };
            clip.Segments[segmentIndex] = segment;
            for (int transformIndex = 0; transformIndex < clip.NumBones; ++transformIndex)
            {
                AclTransformStreams clipStream = original.BoneStreams[transformIndex];
                var stream = new AclTransformStreams
                {
                    Segment = segment,
                    BoneIndex = transformIndex,
                    ParentBoneIndex = clipStream.ParentBoneIndex,
                    OutputIndex = clipStream.OutputIndex,
                    DefaultValue = clipStream.DefaultValue
                };
                if (clipStream.IsRotationConstant)
                    stream.Rotations = clipStream.Rotations.Duplicate();
                else
                {
                    AclRotationTrackStream source = clipStream.Rotations;
                    stream.Rotations = new AclRotationTrackStream(count, source.SampleSize, source.SampleRate, source.RotationFormat, source.BitRate);
                    source.Samples.AsSpan(offset * source.SampleSize, count * source.SampleSize).CopyTo(stream.Rotations.Samples);
                }
                if (clipStream.IsTranslationConstant)
                    stream.Translations = clipStream.Translations.Duplicate();
                else
                {
                    AclTranslationTrackStream source = clipStream.Translations;
                    stream.Translations = new AclTranslationTrackStream(count, source.SampleSize, source.SampleRate, source.VectorFormat, source.BitRate);
                    source.Samples.AsSpan(offset * source.SampleSize, count * source.SampleSize).CopyTo(stream.Translations.Samples);
                }
                if (clipStream.IsScaleConstant)
                    stream.Scales = clipStream.Scales.Duplicate();
                else
                {
                    AclScaleTrackStream source = clipStream.Scales;
                    stream.Scales = new AclScaleTrackStream(count, source.SampleSize, source.SampleRate, source.VectorFormat, source.BitRate);
                    source.Samples.AsSpan(offset * source.SampleSize, count * source.SampleSize).CopyTo(stream.Scales.Samples);
                }
                stream.IsRotationConstant = clipStream.IsRotationConstant;
                stream.IsRotationDefault = clipStream.IsRotationDefault;
                stream.IsTranslationConstant = clipStream.IsTranslationConstant;
                stream.IsTranslationDefault = clipStream.IsTranslationDefault;
                stream.IsScaleConstant = clipStream.IsScaleConstant;
                stream.IsScaleDefault = clipStream.IsScaleDefault;
                stream.ConstantRotation = stream.Rotations.GetRawSample(0);
                stream.ConstantTranslation = stream.Translations.GetRawSample(0);
                stream.ConstantScale = stream.Scales.GetRawSample(0);
                segment.BoneStreams[transformIndex] = stream;
            }
            offset += count;
        }
    }

    internal static void ExtractSegmentBoneRanges(AclClipContext clip)
    {
        foreach (AclSegmentContext segment in clip.Segments)
        {
            segment.Ranges = ExtractBoneRanges(segment);
            for (int transformIndex = 0; transformIndex < segment.NumBones; ++transformIndex)
            {
                AclTransformStreams stream = segment.BoneStreams[transformIndex];
                ref AclTransformRange range = ref segment.Ranges[transformIndex];
                if (!stream.IsRotationConstant && clip.AreRotationsNormalized)
                    range.Rotation = FixupSegmentRange(range.Rotation);
                if (!stream.IsTranslationConstant && clip.AreTranslationsNormalized)
                    range.Translation = FixupSegmentRange(range.Translation);
                if (!stream.IsScaleConstant && clip.AreScalesNormalized)
                    range.Scale = FixupSegmentRange(range.Scale);
            }
        }
    }

    private static AclTrackStreamRange FixupSegmentRange(AclTrackStreamRange range)
    {
        Vector4 scaledMinimum = range.Min * 255.0f;
        Vector4 minimum0 = Vector4.Clamp(new Vector4(MathF.Floor(scaledMinimum.X), MathF.Floor(scaledMinimum.Y), MathF.Floor(scaledMinimum.Z), MathF.Floor(scaledMinimum.W)), Vector4.Zero, new Vector4(255.0f));
        Vector4 minimum1 = Vector4.Max(minimum0 - Vector4.One, Vector4.Zero);
        Vector4 paddedMinimum0 = minimum0 * (1.0f / 255.0f);
        Vector4 paddedMinimum1 = minimum1 * (1.0f / 255.0f);
        Vector4 paddedMinimum = new(
            paddedMinimum0.X <= range.Min.X ? paddedMinimum0.X : paddedMinimum1.X,
            paddedMinimum0.Y <= range.Min.Y ? paddedMinimum0.Y : paddedMinimum1.Y,
            paddedMinimum0.Z <= range.Min.Z ? paddedMinimum0.Z : paddedMinimum1.Z,
            paddedMinimum0.W <= range.Min.W ? paddedMinimum0.W : paddedMinimum1.W);
        Vector4 scaledExtent = (range.Max - paddedMinimum) * 255.0f;
        Vector4 extent0 = Vector4.Clamp(new Vector4(MathF.Ceiling(scaledExtent.X), MathF.Ceiling(scaledExtent.Y), MathF.Ceiling(scaledExtent.Z), MathF.Ceiling(scaledExtent.W)), Vector4.Zero, new Vector4(255.0f));
        Vector4 extent1 = Vector4.Min(extent0 + Vector4.One, new Vector4(255.0f));
        Vector4 paddedExtent0 = extent0 * (1.0f / 255.0f);
        Vector4 paddedExtent1 = extent1 * (1.0f / 255.0f);
        Vector4 paddedExtent = new(
            paddedExtent0.X >= range.Max.X ? paddedExtent0.X : paddedExtent1.X,
            paddedExtent0.Y >= range.Max.Y ? paddedExtent0.Y : paddedExtent1.Y,
            paddedExtent0.Z >= range.Max.Z ? paddedExtent0.Z : paddedExtent1.Z,
            paddedExtent0.W >= range.Max.W ? paddedExtent0.W : paddedExtent1.W);
        return AclTrackStreamRange.FromMinExtent(paddedMinimum, paddedExtent);
    }

    internal static void NormalizeSegmentStreams(AclClipContext clip, AclCompressionSettings settings)
    {
        bool rotations = settings.RotationFormat == AclRotationFormat.QuatDropWVariable;
        bool translations = settings.TranslationFormat == AclVectorFormat.Vector3Variable;
        bool scales = clip.HasScale && settings.ScaleFormat == AclVectorFormat.Vector3Variable;
        foreach (AclSegmentContext segment in clip.Segments)
        {
            NormalizeStreams(segment, segment.Ranges, rotations, translations, scales);
            segment.AreRotationsNormalized = rotations;
            segment.AreTranslationsNormalized = translations;
            segment.AreScalesNormalized = scales;
            int rangeSize = 0;
            int rotationCount = 0;
            foreach (AclTransformStreams stream in segment.BoneStreams)
            {
                if (stream.IsStrippedFromOutput)
                    continue;
                if (rotations && !stream.IsRotationConstant)
                {
                    rangeSize += 6;
                    ++rotationCount;
                }
                if (translations && !stream.IsTranslationConstant)
                    rangeSize += 6;
                if (scales && !stream.IsScaleConstant)
                    rangeSize += 6;
            }
            int partial = rotationCount % 4;
            if (partial != 0)
                rangeSize += (4 - partial) * 6;
            segment.RangeDataSize = rangeSize;
        }
    }

    private static bool IsFinite3(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static AclTransform GetStreamTransform(AclTransformStreams stream, int sampleIndex)
    {
        Vector4 translation = stream.Translations.GetSampleClamped(sampleIndex);
        Vector4 scale = stream.Scales.GetSampleClamped(sampleIndex);
        return new AclTransform
        {
            Rotation = stream.Rotations.GetSampleClamped(sampleIndex),
            Translation = new Vector3(translation.X, translation.Y, translation.Z),
            Scale = new Vector3(scale.X, scale.Y, scale.Z)
        };
    }
}
