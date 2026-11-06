using System.Numerics;

namespace Ruri.ACL;

internal readonly record struct AclCacheIndices(int Rotation, int Translation, int Scale);

internal sealed class AclSingleTrackQuery(AclTrackBitRateDatabase database)
{
    public int TrackIndex;
    public AclTransformBitRates BitRates;
    public AclCacheIndices Indices;
    public void Build(int trackIndex, AclTransformBitRates bitRates)
    {
        TrackIndex = trackIndex;
        BitRates = bitRates;
        Indices = database.FindCacheEntries(trackIndex, bitRates);
    }
}

internal sealed class AclHierarchicalTrackQuery(AclTrackBitRateDatabase database, int numTransforms)
{
    public int TrackIndex;
    public AclTransformBitRates[] BitRates = [];
    public AclCacheIndices[] Indices = new AclCacheIndices[numTransforms];
    public void Build(int trackIndex, AclTransformBitRates[] bitRates, AclTransformStreams[] streams)
    {
        TrackIndex = trackIndex;
        BitRates = bitRates;
        for (int current = trackIndex; current != -1; current = streams[current].ParentBoneIndex)
            Indices[current] = database.FindCacheEntries(current, bitRates[current]);
    }
}

internal sealed class AclEveryTrackQuery(AclTrackBitRateDatabase database, int numTransforms)
{
    public AclTransformBitRates[] BitRates = [];
    public AclCacheIndices[] Indices = new AclCacheIndices[numTransforms];
    public void Build(AclTransformBitRates[] bitRates)
    {
        BitRates = bitRates;
        for (int index = 0; index < numTransforms; index++)
            Indices[index] = database.FindCacheEntries(index, bitRates[index]);
    }
}

internal sealed class AclTrackBitRateDatabase
{
    private AclTransformStreams[] streams;
    private readonly AclTransformStreams[] rawStreams;
    private readonly AclCompressionSettings settings;
    private readonly bool hasScale;
    private readonly int numTransforms;
    private readonly int entriesPerTransform;
    private readonly int trackSize;
    private readonly int bitsetSize;
    private uint generationId = 1;
    private readonly byte[] rates;
    private readonly uint[] generations;
    private readonly uint[] validity;
    private readonly Vector4[] data;

    public AclTrackBitRateDatabase(AclClipContext clip, AclClipContext rawClip, AclCompressionSettings settings)
    {
        streams = clip.Segments[0].BoneStreams;
        rawStreams = rawClip.Segments[0].BoneStreams;
        this.settings = settings;
        hasScale = rawClip.HasScale;
        numTransforms = clip.NumBones;
        entriesPerTransform = (hasScale ? 3 : 2) * 4;
        int numSamples = clip.Segments[0].NumSamples;
        int numCachedTracks = numTransforms * entriesPerTransform;
        trackSize = (numSamples + 3) & ~3;
        bitsetSize = (numSamples + 31) / 32;
        rates = new byte[numCachedTracks];
        Array.Fill(rates, (byte)255);
        generations = new uint[numCachedTracks];
        validity = new uint[bitsetSize * numCachedTracks];
        data = new Vector4[trackSize * numCachedTracks];
    }

    public void SetSegment(AclSegmentContext segment)
    {
        streams = segment.BoneStreams;
        Array.Fill(rates, (byte)255);
        Array.Clear(generations);
    }

    public AclCacheIndices FindCacheEntries(int trackIndex, AclTransformBitRates bitRates)
    {
        int offset = trackIndex * entriesPerTransform;
        int rotation = FindCacheEntry(offset, bitRates.Rotation);
        int translation = FindCacheEntry(offset + 4, bitRates.Translation);
        int scale = hasScale ? FindCacheEntry(offset + 8, bitRates.Scale) : -1;
        return new(rotation, translation, scale);
    }

    private int FindCacheEntry(int offset, byte bitRate)
    {
        if (bitRate == 255)
        {
            if (generations[offset] == 0)
            {
                Array.Clear(validity, bitsetSize * offset, bitsetSize);
                generations[offset] = generationId++;
            }
            return offset;
        }
        for (int index = 0; index < 4; index++)
            if (rates[offset + index] == bitRate)
                return offset + index;
        uint oldestGeneration = generations[offset];
        int oldest = 0;
        for (int index = 1; index < 4; index++)
        {
            if (generations[offset + index] < oldestGeneration)
            {
                oldestGeneration = generations[offset + index];
                oldest = index;
            }
        }
        int cacheIndex = offset + oldest;
        rates[cacheIndex] = bitRate;
        generations[cacheIndex] = generationId++;
        Array.Clear(validity, bitsetSize * cacheIndex, bitsetSize);
        return cacheIndex;
    }

    private bool IsValid(int cacheIndex, int sampleIndex) => (validity[cacheIndex * bitsetSize + (sampleIndex >> 5)] & (1u << (31 - (sampleIndex & 31)))) != 0;
    private void SetValid(int cacheIndex, int sampleIndex) => validity[cacheIndex * bitsetSize + (sampleIndex >> 5)] |= 1u << (31 - (sampleIndex & 31));

    private Quaternion SampleRotation(int trackIndex, int key, byte bitRate, int cacheIndex)
    {
        AclTransformStreams track = streams[trackIndex];
        if (track.IsRotationDefault)
            return track.DefaultValue.Rotation;
        int sampleIndex = track.IsRotationConstant ? 0 : key;
        if (IsValid(cacheIndex, sampleIndex))
            return AclMath.ToQuaternion(data[cacheIndex * trackSize + sampleIndex]);
        Quaternion rotation;
        bool variable = settings.RotationFormat == AclRotationFormat.QuatDropWVariable;
        if (track.IsRotationConstant)
            rotation = variable ? AclCompression.GetRotationSample(rawStreams[trackIndex], 0) : AclCompression.GetRotationSample(rawStreams[trackIndex], 0, settings.RotationFormat);
        else
            rotation = variable ? AclCompression.GetRotationSample(track, rawStreams[trackIndex], sampleIndex, bitRate) : AclCompression.GetRotationSample(track, sampleIndex, settings.RotationFormat);
        rotation = AclMath.Normalize(rotation);
        data[cacheIndex * trackSize + sampleIndex] = AclMath.ToVector(rotation);
        SetValid(cacheIndex, sampleIndex);
        return rotation;
    }

    private Vector3 SampleVector(int trackIndex, int key, byte bitRate, int cacheIndex, bool scale)
    {
        AclTransformStreams track = streams[trackIndex];
        if (scale ? track.IsScaleDefault : track.IsTranslationDefault)
            return scale ? track.DefaultValue.Scale : track.DefaultValue.Translation;
        bool constant = scale ? track.IsScaleConstant : track.IsTranslationConstant;
        int sampleIndex = constant ? 0 : key;
        if (IsValid(cacheIndex, sampleIndex))
            return AclMath.ToVector3(data[cacheIndex * trackSize + sampleIndex]);
        AclVectorFormat format = scale ? settings.ScaleFormat : settings.TranslationFormat;
        bool variable = format == AclVectorFormat.Vector3Variable;
        Vector4 value;
        if (constant)
            value = scale ? AclCompression.GetScaleSample(rawStreams[trackIndex], 0, AclVectorFormat.Vector3Full) : AclCompression.GetTranslationSample(rawStreams[trackIndex], 0, AclVectorFormat.Vector3Full);
        else if (variable)
            value = scale ? AclCompression.GetScaleSample(track, rawStreams[trackIndex], sampleIndex, bitRate) : AclCompression.GetTranslationSample(track, rawStreams[trackIndex], sampleIndex, bitRate);
        else
            value = scale ? AclCompression.GetScaleSample(track, sampleIndex, format) : AclCompression.GetTranslationSample(track, sampleIndex, format);
        data[cacheIndex * trackSize + sampleIndex] = value;
        SetValid(cacheIndex, sampleIndex);
        return AclMath.ToVector3(value);
    }

    private AclTransform SampleTransform(int trackIndex, int key, AclTransformBitRates bitRates, AclCacheIndices indices) => new()
    {
        Rotation = SampleRotation(trackIndex, key, bitRates.Rotation, indices.Rotation),
        Translation = SampleVector(trackIndex, key, bitRates.Translation, indices.Translation, false),
        Scale = SampleVector(trackIndex, key, bitRates.Scale, indices.Scale, true),
    };

    public void Sample(AclSingleTrackQuery query, float sampleTime, Span<AclTransform> output)
    {
        int key = AclCompression.GetUniformSampleKey(streams[0].Segment, sampleTime);
        output[query.TrackIndex] = SampleTransform(query.TrackIndex, key, query.BitRates, query.Indices);
    }

    public void Sample(AclHierarchicalTrackQuery query, float sampleTime, Span<AclTransform> output)
    {
        int key = AclCompression.GetUniformSampleKey(streams[0].Segment, sampleTime);
        for (int current = query.TrackIndex; current != -1; current = streams[current].ParentBoneIndex)
            output[current] = SampleTransform(current, key, query.BitRates[current], query.Indices[current]);
    }

    public void Sample(AclEveryTrackQuery query, float sampleTime, Span<AclTransform> output)
    {
        int key = AclCompression.GetUniformSampleKey(streams[0].Segment, sampleTime);
        for (int index = 0; index < numTransforms; index++)
            output[index] = SampleTransform(index, key, query.BitRates[index], query.Indices[index]);
    }
}
