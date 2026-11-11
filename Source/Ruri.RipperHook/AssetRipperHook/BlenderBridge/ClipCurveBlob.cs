using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using Ruri.RipperHook.Animation;
using Ruri.RipperHook.BlenderBridge.Conversion;
using Ruri.RipperHook.BlenderBridge.Statements;
using Ruri.RipperHook.Humanoid;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ruri.RipperHook.BlenderBridge;

/// <summary>One animation event of a clip, as the source serializes it: when it fires, the function it calls, its string, float and
/// integer parameters, the object it passes (stated as the asset reference the script records use: kind, key, class, name; null where
/// it passes none) and how the call is sent (<c>SendMessageOptions</c> as its serialized integer).</summary>
internal sealed record ClipEvent(float time, string functionName, string stringParameter, float floatParameter, int intParameter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? objectReference, int messageOptions);

public static class ClipCurveBlob
{
    /// <summary>One curve of the index. <c>preWrap</c> and <c>postWrap</c> are the curve's serialized pre- and post-infinity, the
    /// two integers the source stores on the curve (<c>m_PreInfinity</c>, <c>m_PostInfinity</c>), as they are: not converted to
    /// another enumeration, so the reader reads them in the source engine's own value domain. <c>weighted</c> says the curve's
    /// block carries, after the four arrays every curve has (times, values, in slopes, out slopes), three more: in weights and out
    /// weights, each as wide as the values, and weighted modes, one per key (0 none, 1 in, 2 out, 3 both) -- which a curve read
    /// from a clip has and no other producer writes. <c>className</c> is the class name of the native class a float curve drives,
    /// where it is not a script's.</summary>
    private sealed record CurveIndexEntry(string kind, string path, string? attr, int classId, int keys, long off,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? scriptAssembly = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? scriptNamespace = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? scriptClass = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? className = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? preWrap = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? postWrap = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool weighted = false);

    private sealed record ClipIndex(
        string name, float sampleRate, float startTime, float stopTime,
        bool keepPositionXZ, bool keepPositionY, bool keepOrientation,
        List<CurveIndexEntry> curves, bool loopTime = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ClipEvent>? events = null);

    private sealed record SolvedClipIndex(
        string name, float sampleRate, float startTime, float stopTime,
        bool keepPositionXZ, bool keepPositionY, bool keepOrientation,
        List<CurveIndexEntry> curves, string[] consumedAttributes);

    public static (List<(string Attribute, HermiteCurve Curve)> Channels, float SampleRate,
        bool KeepPositionXZ, bool KeepPositionY, bool KeepOrientation)
        ReadFloatChannels(string metaJson, byte[] payload)
    {
        ClipIndex meta = JsonSerializer.Deserialize<ClipIndex>(metaJson)
            ?? throw new InvalidOperationException("clip blob meta deserialized to null");
        ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(payload);
        List<(string, HermiteCurve)> channels = new();
        foreach (CurveIndexEntry entry in meta.curves)
        {
            if (entry.kind != "float" || string.IsNullOrEmpty(entry.attr) || entry.keys <= 0)
            {
                continue;
            }
            int keys = entry.keys;
            int cursor = checked((int)entry.off);
            float[] times = floats.Slice(cursor, keys).ToArray();
            float[] values = floats.Slice(cursor + keys, keys).ToArray();
            float[] inSlopes = floats.Slice(cursor + 2 * keys, keys).ToArray();
            float[] outSlopes = floats.Slice(cursor + 3 * keys, keys).ToArray();
            channels.Add((entry.attr, HermiteCurve.FromArrays(times, values, inSlopes, outSlopes)));
        }
        return (channels, meta.sampleRate, meta.keepPositionXZ, meta.keepPositionY, meta.keepOrientation);
    }

    public static (string MetaJson, byte[] Curves) BuildSolved(SolvedHumanoidPose pose,
        string[] consumedAttributes, bool keepPositionXZ, bool keepPositionY, bool keepOrientation)
    {
        int frameCount = pose.FrameCount;
        List<CurveIndexEntry> index = new();
        long totalFloats = 0;

        void Count(string kind, int dimensions, string path)
        {
            index.Add(new CurveIndexEntry(kind, path, null, 0, frameCount, totalFloats));
            totalFloats += frameCount + 3L * frameCount * dimensions;
        }

        foreach ((string path, _) in pose.BoneRotations)
        {
            Count("rot", 4, path);
        }
        if (pose.HipsPositions is { } hips)
        {
            Count("pos", 3, hips.Path);
        }
        if (pose.Motion is not null)
        {
            Count("pos", 3, string.Empty);
            Count("rot", 4, string.Empty);
        }

        float[] payload = new float[totalFloats];
        int cursor = 0;

        void WriteTimes()
        {
            for (int f = 0; f < frameCount; f++)
            {
                payload[cursor + f] = f / pose.SampleRate;
            }
            cursor += frameCount;
        }

        void WriteRotations(System.Numerics.Quaternion[] rotations)
        {
            WriteTimes();
            for (int f = 0; f < frameCount; f++)
            {
                payload[cursor + f * 4 + 0] = rotations[f].X;
                payload[cursor + f * 4 + 1] = rotations[f].Y;
                payload[cursor + f * 4 + 2] = rotations[f].Z;
                payload[cursor + f * 4 + 3] = rotations[f].W;
            }
            cursor += frameCount * 12;        }

        void WritePositions(System.Numerics.Vector3[] positions)
        {
            WriteTimes();
            for (int f = 0; f < frameCount; f++)
            {
                payload[cursor + f * 3 + 0] = positions[f].X;
                payload[cursor + f * 3 + 1] = positions[f].Y;
                payload[cursor + f * 3 + 2] = positions[f].Z;
            }
            cursor += frameCount * 9;
        }

        foreach ((_, System.Numerics.Quaternion[] rotations) in pose.BoneRotations)
        {
            WriteRotations(rotations);
        }
        if (pose.HipsPositions is { } hipsOut)
        {
            WritePositions(hipsOut.Positions);
        }
        if (pose.Motion is { } motion)
        {
            WritePositions(motion.Positions);
            WriteRotations(motion.Rotations);
        }

        if (cursor != totalFloats)
        {
            throw new InvalidOperationException($"solved blob desync: wrote {cursor}, indexed {totalFloats}");
        }

        SolvedClipIndex meta = new("solved", pose.SampleRate, 0f, 0f,
            keepPositionXZ, keepPositionY, keepOrientation, index, consumedAttributes);
        byte[] bytes = new byte[totalFloats * sizeof(float)];
        Buffer.BlockCopy(payload, 0, bytes, 0, bytes.Length);
        return (JsonSerializer.Serialize(meta), bytes);
    }

    /// <summary>
    /// Curves already reduced, as the blob a host reads. Same layout as every other blob here --
    /// per curve, times then values then both tangents, each value key-major across components --
    /// so the reader cannot tell which producer wrote it.
    /// </summary>
    public static (string MetaJson, byte[] Curves) Build(string name, float sampleRate, int frameCount,
        IReadOnlyList<ClipBuilder.ReducedChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        List<CurveIndexEntry> index = new(channels.Count);
        long totalFloats = 0;
        foreach (ClipBuilder.ReducedChannel channel in channels)
        {
            int keys = channel.Frames.Length;
            index.Add(new CurveIndexEntry(channel.Kind, channel.Path,
                channel.Attribute.Length == 0 ? null : channel.Attribute, channel.ClassId, keys, totalFloats));
            totalFloats += keys + 3L * keys * channel.Values.Length;
        }
        float[] payload = new float[totalFloats];
        int cursor = 0;
        foreach (ClipBuilder.ReducedChannel channel in channels)
        {
            int keys = channel.Frames.Length;
            int dimensions = channel.Values.Length;
            for (int key = 0; key < keys; key++)
            {
                payload[cursor + key] = channel.Frames[key] / sampleRate;
            }
            cursor += keys;
            foreach (float[][] component in new[] { channel.Values, channel.InSlopes, channel.OutSlopes })
            {
                for (int key = 0; key < keys; key++)
                {
                    for (int axis = 0; axis < dimensions; axis++)
                    {
                        payload[cursor + key * dimensions + axis] = component[axis][key];
                    }
                }
                cursor += keys * dimensions;
            }
        }
        if (cursor != totalFloats)
        {
            throw new InvalidOperationException($"clip blob desync: wrote {cursor}, indexed {totalFloats}");
        }
        ClipIndex meta = new(name, sampleRate, 0f, frameCount > 1 ? (frameCount - 1) / sampleRate : 0f,
            true, true, true, index);
        byte[] bytes = new byte[totalFloats * sizeof(float)];
        Buffer.BlockCopy(payload, 0, bytes, 0, bytes.Length);
        return (JsonSerializer.Serialize(meta), bytes);
    }

    /// <summary>The clip's curves as the blob a host reads, and its animation events in the index. <paramref name="keyOf"/> names the
    /// object an event passes (the key the statement states it by); without it an event states no object.</summary>
    public static (string MetaJson, byte[] Curves) Build(IAnimationClip clip, Func<IUnityObjectBase, string>? keyOf = null)
    {
        List<CurveIndexEntry> index = new();
        long totalFloats = 0;

        void Count(string kind, int keys, int dimensions, string path, string? attr = null, int classId = 0, IMonoScript? script = null,
            string? className = null, int? preWrap = null, int? postWrap = null, bool weighted = false)
        {
            index.Add(new CurveIndexEntry(kind, path, attr, classId, keys, totalFloats,
                script?.GetAssemblyNameFixed(), script?.Namespace.String, script?.ClassName_R.String, className, preWrap, postWrap, weighted));
            totalFloats += keys + 3L * keys * dimensions + (weighted ? (2L * dimensions + 1L) * keys : 0L);
        }

        foreach (IVector3Curve curve in clip.PositionCurves_C74)
        {
            Count("pos", curve.Curve.Curve.Count, 3, curve.Path.String, preWrap: curve.Curve.PreInfinity,
                postWrap: curve.Curve.PostInfinity, weighted: true);
        }
        foreach (IQuaternionCurve curve in clip.RotationCurves_C74)
        {
            Count("rot", curve.Curve.Curve.Count, 4, curve.Path.String, preWrap: curve.Curve.PreInfinity,
                postWrap: curve.Curve.PostInfinity, weighted: true);
        }
        foreach (IVector3Curve curve in clip.ScaleCurves_C74)
        {
            Count("scale", curve.Curve.Curve.Count, 3, curve.Path.String, preWrap: curve.Curve.PreInfinity,
                postWrap: curve.Curve.PostInfinity, weighted: true);
        }
        foreach (IVector3Curve curve in clip.EulerCurves_C74)
        {
            Count("euler", curve.Curve.Curve.Count, 3, curve.Path.String, preWrap: curve.Curve.PreInfinity,
                postWrap: curve.Curve.PostInfinity, weighted: true);
        }
        foreach (IFloatCurve curve in clip.FloatCurves_C74)
        {
            Count("float", curve.Curve.Curve.Count, 1, curve.Path.String, curve.Attribute.String, curve.ClassID,
                curve.Script.TryGetAsset(clip.Collection, out IMonoScript? script) ? script : null,
                NativeClassName(curve.ClassID), curve.Curve.PreInfinity, curve.Curve.PostInfinity, weighted: true);
        }

        float[] payload = new float[totalFloats];
        int cursor = 0;

        foreach (IVector3Curve curve in clip.PositionCurves_C74)
        {
            cursor = WriteVector3Keys(curve, payload, cursor);
        }
        foreach (IQuaternionCurve curve in clip.RotationCurves_C74)
        {
            cursor = WriteQuaternionKeys(curve, payload, cursor);
        }
        foreach (IVector3Curve curve in clip.ScaleCurves_C74)
        {
            cursor = WriteVector3Keys(curve, payload, cursor);
        }
        foreach (IVector3Curve curve in clip.EulerCurves_C74)
        {
            cursor = WriteVector3Keys(curve, payload, cursor);
        }
        foreach (IFloatCurve curve in clip.FloatCurves_C74)
        {
            cursor = WriteFloatKeys(curve, payload, cursor);
        }

        if (cursor != totalFloats)
        {
            throw new InvalidOperationException($"clip curve payload desync: wrote {cursor}, indexed {totalFloats}");
        }

        List<ClipEvent>? events = null;
        foreach (var animationEvent in clip.Events_C74)
        {
            (events ??= []).Add(new ClipEvent(animationEvent.Time, animationEvent.FunctionName.String, animationEvent.Data.String,
                animationEvent.FloatParameter, animationEvent.IntParameter,
                EventObject(animationEvent.ObjectReferenceParameter, clip.Collection, keyOf), animationEvent.MessageOptions));
        }

        var muscleClip = clip.MuscleClip_C74;
        ClipIndex meta = new(
            clip.Name,
            clip.SampleRate_C74,
            muscleClip?.StartTime ?? 0f,
            muscleClip?.StopTime ?? 0f,
            muscleClip?.KeepOriginalPositionXZ ?? true,
            muscleClip?.KeepOriginalPositionY ?? true,
            muscleClip?.KeepOriginalOrientation ?? true,
            index,
            muscleClip?.LoopTime ?? false,
            events);

        byte[] bytes = new byte[totalFloats * sizeof(float)];
        Buffer.BlockCopy(payload, 0, bytes, 0, bytes.Length);
        return (JsonSerializer.Serialize(meta), bytes);
    }

    /// <summary>The object an animation event passes, as the asset reference the script records use, or null where it passes none (or
    /// no key naming is given).</summary>
    private static JsonObject? EventObject(IPPtr pointer, AssetCollection collection, Func<IUnityObjectBase, string>? keyOf)
    {
        if (keyOf is null || pointer.PathID == 0 || collection.TryGetAsset(pointer.FileID, pointer.PathID) is not { } target)
        {
            return null;
        }
        return new JsonObject
        {
            [ScriptRecordMembers.Kind] = ScriptRecordMembers.AssetReference,
            [ScriptRecordMembers.Key] = keyOf(target),
            [ScriptRecordMembers.Class] = UnityScripts.ClassNameOf(target),
            [ScriptRecordMembers.Name] = UnityScripts.NameOf(target),
        };
    }

    /// <summary>The weight a key states when its version of the engine states none: the third the engine fills a new key with.</summary>
    private const float DefaultWeight = 1f / 3f;

    /// <summary>A vector curve's block: times, values, in slopes, out slopes, then in weights and out weights (three per key each) and
    /// weighted modes (one per key).</summary>
    private static int WriteVector3Keys(IVector3Curve curve, float[] payload, int cursor)
    {
        var keys = curve.Curve.Curve;
        int count = keys.Count;
        int times = cursor;
        int values = times + count;
        int inSlopes = values + count * 3;
        int outSlopes = inSlopes + count * 3;
        int inWeights = outSlopes + count * 3;
        int outWeights = inWeights + count * 3;
        int weightedModes = outWeights + count * 3;
        for (int i = 0; i < count; i++)
        {
            var key = keys[i];
            payload[times + i] = key.Time;
            payload[values + i * 3 + 0] = key.Value.X;
            payload[values + i * 3 + 1] = key.Value.Y;
            payload[values + i * 3 + 2] = key.Value.Z;
            payload[inSlopes + i * 3 + 0] = key.InSlope.X;
            payload[inSlopes + i * 3 + 1] = key.InSlope.Y;
            payload[inSlopes + i * 3 + 2] = key.InSlope.Z;
            payload[outSlopes + i * 3 + 0] = key.OutSlope.X;
            payload[outSlopes + i * 3 + 1] = key.OutSlope.Y;
            payload[outSlopes + i * 3 + 2] = key.OutSlope.Z;
            payload[inWeights + i * 3 + 0] = key.InWeight?.X ?? DefaultWeight;
            payload[inWeights + i * 3 + 1] = key.InWeight?.Y ?? DefaultWeight;
            payload[inWeights + i * 3 + 2] = key.InWeight?.Z ?? DefaultWeight;
            payload[outWeights + i * 3 + 0] = key.OutWeight?.X ?? DefaultWeight;
            payload[outWeights + i * 3 + 1] = key.OutWeight?.Y ?? DefaultWeight;
            payload[outWeights + i * 3 + 2] = key.OutWeight?.Z ?? DefaultWeight;
            payload[weightedModes + i] = key.WeightedMode;
        }
        return weightedModes + count;
    }

    /// <summary>A quaternion curve's block, laid out as a vector curve's with four components.</summary>
    private static int WriteQuaternionKeys(IQuaternionCurve curve, float[] payload, int cursor)
    {
        var keys = curve.Curve.Curve;
        int count = keys.Count;
        int times = cursor;
        int values = times + count;
        int inSlopes = values + count * 4;
        int outSlopes = inSlopes + count * 4;
        int inWeights = outSlopes + count * 4;
        int outWeights = inWeights + count * 4;
        int weightedModes = outWeights + count * 4;
        for (int i = 0; i < count; i++)
        {
            var key = keys[i];
            payload[times + i] = key.Time;
            payload[values + i * 4 + 0] = key.Value.X;
            payload[values + i * 4 + 1] = key.Value.Y;
            payload[values + i * 4 + 2] = key.Value.Z;
            payload[values + i * 4 + 3] = key.Value.W;
            payload[inSlopes + i * 4 + 0] = key.InSlope.X;
            payload[inSlopes + i * 4 + 1] = key.InSlope.Y;
            payload[inSlopes + i * 4 + 2] = key.InSlope.Z;
            payload[inSlopes + i * 4 + 3] = key.InSlope.W;
            payload[outSlopes + i * 4 + 0] = key.OutSlope.X;
            payload[outSlopes + i * 4 + 1] = key.OutSlope.Y;
            payload[outSlopes + i * 4 + 2] = key.OutSlope.Z;
            payload[outSlopes + i * 4 + 3] = key.OutSlope.W;
            payload[inWeights + i * 4 + 0] = key.InWeight?.X ?? DefaultWeight;
            payload[inWeights + i * 4 + 1] = key.InWeight?.Y ?? DefaultWeight;
            payload[inWeights + i * 4 + 2] = key.InWeight?.Z ?? DefaultWeight;
            payload[inWeights + i * 4 + 3] = key.InWeight?.W ?? DefaultWeight;
            payload[outWeights + i * 4 + 0] = key.OutWeight?.X ?? DefaultWeight;
            payload[outWeights + i * 4 + 1] = key.OutWeight?.Y ?? DefaultWeight;
            payload[outWeights + i * 4 + 2] = key.OutWeight?.Z ?? DefaultWeight;
            payload[outWeights + i * 4 + 3] = key.OutWeight?.W ?? DefaultWeight;
            payload[weightedModes + i] = key.WeightedMode;
        }
        return weightedModes + count;
    }

    /// <summary>The class name of the native class a float curve drives, or null for a script's class (the script states itself) and
    /// for an id no class has.</summary>
    private static string? NativeClassName(int classId) =>
        classId != (int)AssetRipper.SourceGenerated.ClassIDType.MonoBehaviour
        && Enum.IsDefined(typeof(AssetRipper.SourceGenerated.ClassIDType), classId)
            ? ((AssetRipper.SourceGenerated.ClassIDType)classId).ToString()
            : null;

    /// <summary>A float curve's block: times, values, in slopes, out slopes, then in weights, out weights and weighted modes, one value
    /// per key each.</summary>
    private static int WriteFloatKeys(IFloatCurve curve, float[] payload, int cursor)
    {
        var keys = curve.Curve.Curve;
        int count = keys.Count;
        int times = cursor;
        int values = times + count;
        int inSlopes = values + count;
        int outSlopes = inSlopes + count;
        int inWeights = outSlopes + count;
        int outWeights = inWeights + count;
        int weightedModes = outWeights + count;
        for (int i = 0; i < count; i++)
        {
            var key = keys[i];
            payload[times + i] = key.Time;
            payload[values + i] = key.Value;
            payload[inSlopes + i] = key.InSlope;
            payload[outSlopes + i] = key.OutSlope;
            payload[inWeights + i] = key.InWeight;
            payload[outWeights + i] = key.OutWeight;
            payload[weightedModes + i] = key.WeightedMode;
        }
        return weightedModes + count;
    }
}
