using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
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
