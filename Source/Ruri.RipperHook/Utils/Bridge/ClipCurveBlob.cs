using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using Ruri.RipperHook.Animation;
using Ruri.RipperHook.Humanoid;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ruri.RipperHook.Bridge;

/// <summary>
/// Serializes an exported AnimationClip's editor-format curves into one float32 payload plus a
/// small JSON index, consumed Blender-side as zero-parse numpy views (RuriRipperImporter's
/// clip_curves.ClipCurves.from_blob). The curves already exist as typed arrays right here at
/// export time; round-tripping them through 80+MB of YAML text that CPython then re-parses was
/// the entire animation-import bottleneck (measured: 15.5s YAML parse for one battle clip whose
/// numeric payload is ~20MB). This hands the same numbers across the pythonnet boundary in two
/// objects: one string, one byte[].
///
/// Payload layout, float32 little-endian, per curve in index order:
///   times[K] · values[K*D] · inSlopes[K*D] · outSlopes[K*D]
/// with D = 4 (rotation) / 3 (position/scale/euler) / 1 (float). The JSON index carries clip
/// scalars (name/sampleRate/start/stop/root-motion keep flags) and per-curve
/// {kind, path, attr, classId, keys, off} with off in FLOATS from the payload start.
/// Weights and tangent modes are deliberately absent -- the Blender-side Hermite evaluator
/// consumes exactly time/value/inSlope/outSlope and nothing else.
/// </summary>
internal static class ClipCurveBlob
{
    private sealed record CurveIndexEntry(string kind, string path, string? attr, int classId, int keys, long off);

    private sealed record ClipIndex(
        string name, float sampleRate, float startTime, float stopTime,
        bool keepPositionXZ, bool keepPositionY, bool keepOrientation,
        List<CurveIndexEntry> curves);

    /// <summary>A solved humanoid pose's blob index: the same shape a host's from_blob reader
    /// already consumes, plus the float attributes the solve consumed (which the host drops from
    /// its own copy of the clip, mirroring the in-place converter's DropConsumedFloatCurves).</summary>
    private sealed record SolvedClipIndex(
        string name, float sampleRate, float startTime, float stopTime,
        bool keepPositionXZ, bool keepPositionY, bool keepOrientation,
        List<CurveIndexEntry> curves, string[] consumedAttributes);

    /// <summary>
    /// The reverse of <see cref="Build"/> for the float channels only: what a host sends ACROSS
    /// the bridge for a humanoid solve (same payload layout, kind "float" entries). Non-float
    /// entries are ignored, so a caller may hand a whole clip blob or a pre-filtered one.
    /// </summary>
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

    /// <summary>
    /// A solved pose as the standard curve blob (dense per-frame keys, zero slopes -- the same
    /// keys the in-place converter writes), read back by the host's existing from_blob path.
    /// </summary>
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
            cursor += frameCount * 12; // values + zeroed in/out slope spans
        }

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

    public static (string MetaJson, byte[] Curves) Build(IAnimationClip clip)
    {
        List<CurveIndexEntry> index = new();
        long totalFloats = 0;

        void Count(string kind, int keys, int dimensions, string path, string? attr = null, int classId = 0)
        {
            index.Add(new CurveIndexEntry(kind, path, attr, classId, keys, totalFloats));
            totalFloats += keys + 3L * keys * dimensions;
        }

        foreach (IVector3Curve curve in clip.PositionCurves_C74)
        {
            Count("pos", curve.Curve.Curve.Count, 3, curve.Path.String);
        }
        foreach (IQuaternionCurve curve in clip.RotationCurves_C74)
        {
            Count("rot", curve.Curve.Curve.Count, 4, curve.Path.String);
        }
        foreach (IVector3Curve curve in clip.ScaleCurves_C74)
        {
            Count("scale", curve.Curve.Curve.Count, 3, curve.Path.String);
        }
        foreach (IVector3Curve curve in clip.EulerCurves_C74)
        {
            Count("euler", curve.Curve.Curve.Count, 3, curve.Path.String);
        }
        foreach (IFloatCurve curve in clip.FloatCurves_C74)
        {
            Count("float", curve.Curve.Curve.Count, 1, curve.Path.String, curve.Attribute.String, curve.ClassID);
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

        // The index above appended per curve-list in pos/rot/scale/euler/float order and the
        // writers ran in the same order, so cursor must land exactly on totalFloats.
        if (cursor != totalFloats)
        {
            throw new InvalidOperationException($"clip curve payload desync: wrote {cursor}, indexed {totalFloats}");
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
            index);

        byte[] bytes = new byte[totalFloats * sizeof(float)];
        Buffer.BlockCopy(payload, 0, bytes, 0, bytes.Length);
        return (JsonSerializer.Serialize(meta), bytes);
    }

    private static int WriteVector3Keys(IVector3Curve curve, float[] payload, int cursor)
    {
        var keys = curve.Curve.Curve;
        int count = keys.Count;
        int times = cursor;
        int values = times + count;
        int inSlopes = values + count * 3;
        int outSlopes = inSlopes + count * 3;
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
        }
        return outSlopes + count * 3;
    }

    private static int WriteQuaternionKeys(IQuaternionCurve curve, float[] payload, int cursor)
    {
        var keys = curve.Curve.Curve;
        int count = keys.Count;
        int times = cursor;
        int values = times + count;
        int inSlopes = values + count * 4;
        int outSlopes = inSlopes + count * 4;
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
        }
        return outSlopes + count * 4;
    }

    private static int WriteFloatKeys(IFloatCurve curve, float[] payload, int cursor)
    {
        var keys = curve.Curve.Curve;
        int count = keys.Count;
        int times = cursor;
        int values = times + count;
        int inSlopes = values + count;
        int outSlopes = inSlopes + count;
        for (int i = 0; i < count; i++)
        {
            var key = keys[i];
            payload[times + i] = key.Time;
            payload[values + i] = key.Value;
            payload[inSlopes + i] = key.InSlope;
            payload[outSlopes + i] = key.OutSlope;
        }
        return outSlopes + count;
    }
}
