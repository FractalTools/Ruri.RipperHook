using AssetRipper.SourceGenerated;
using System.Numerics;

namespace Ruri.RipperHook.Conversion;

/// <summary>
/// One animated node: its transform path inside the rig and the sampled local transform per
/// frame, already in the host's basis. A stream left null is not animated on that node.
/// </summary>
public sealed class ClipTrack
{
    public required string Path { get; init; }

    public Vector3[]? Positions { get; init; }

    public Quaternion[]? Rotations { get; init; }

    public Vector3[]? Scales { get; init; }
}

/// <summary>One sampled scalar channel: a blend shape weight, a custom property.</summary>
public sealed class ClipFloatTrack
{
    public required string Path { get; init; }

    public required string Attribute { get; init; }

    public required ClassIDType TargetClass { get; init; }

    public required float[] Values { get; init; }
}

/// <summary>
/// Sampled tracks reduced to the keys they need: for each curve, the frames that reproduce its
/// samples within a tolerance (see <see cref="CurveReducer"/>) with the tangents fitted to them,
/// every curve reduced on its own core, rotations made unit length (a decoded quaternion's
/// length carries nothing, and one off by a fraction reads as a rotation to any angular metric)
/// and kept on one hemisphere so no frame takes the long way round.
/// </summary>
public static class ClipBuilder
{
    private enum CurveKind
    {
        Rotation,
        Position,
        Scale,
        Float,
    }

    private readonly record struct CurveJob(CurveKind Kind, ClipTrack? Track, ClipFloatTrack? FloatTrack);

    /// <summary>
    /// One curve after reduction, in nobody's engine: which frames it keeps, and per component
    /// the value and the two tangents at each of them. ``Kind`` is the curve's own word for what
    /// it drives -- rot, pos, scale, float -- and ``Values[component][key]`` is laid out the way
    /// every consumer reads it, one array per component.
    /// </summary>
    public sealed record ReducedChannel(string Kind, string Path, string Attribute, int ClassId,
        int[] Frames, float[][] Values, float[][] InSlopes, float[][] OutSlopes);

    public const string RotationKind = "rot";
    public const string PositionKind = "pos";
    public const string ScaleKind = "scale";
    public const string FloatKind = "float";

    /// <summary>
    /// Every track reduced to the keys it needs, each curve on its own core. This is the whole
    /// of what a clip IS once it is decoded; what a consumer does with the curves is its own.
    /// </summary>
    public static List<ReducedChannel> Reduce(float sampleRate, int frameCount,
        IReadOnlyList<ClipTrack> tracks, IReadOnlyList<ClipFloatTrack> floatTracks, ClipTolerance? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(floatTracks);
        if (sampleRate <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "A clip needs a positive sample rate.");
        }
        if (frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "A clip needs at least one frame.");
        }
        List<CurveJob> jobs = Jobs(frameCount, tracks, floatTracks);
        ReducedCurve[] reduced = new ReducedCurve[jobs.Count];
        Parallel.For(0, jobs.Count, index => reduced[index] = Reduce(jobs[index], sampleRate, tolerance));
        List<ReducedChannel> channels = new(jobs.Count);
        for (int index = 0; index < jobs.Count; index++)
        {
            CurveJob job = jobs[index];
            ReducedCurve curve = reduced[index];
            float[][] samples = job.Kind switch
            {
                CurveKind.Rotation => Components(job.Track!.Rotations!),
                CurveKind.Position => Components(job.Track!.Positions!),
                CurveKind.Scale => Components(job.Track!.Scales!),
                _ => [job.FloatTrack!.Values],
            };
            float[][] values = new float[samples.Length][];
            for (int component = 0; component < samples.Length; component++)
            {
                values[component] = new float[curve.Frames.Length];
                for (int key = 0; key < curve.Frames.Length; key++)
                {
                    values[component][key] = samples[component][curve.Frames[key]];
                }
            }
            channels.Add(new ReducedChannel(
                job.Kind switch
                {
                    CurveKind.Rotation => RotationKind,
                    CurveKind.Position => PositionKind,
                    CurveKind.Scale => ScaleKind,
                    _ => FloatKind,
                },
                job.Track?.Path ?? job.FloatTrack!.Path,
                job.FloatTrack?.Attribute ?? string.Empty,
                job.FloatTrack is null ? 0 : (int)job.FloatTrack.TargetClass,
                curve.Frames, values, curve.InSlopes, curve.OutSlopes));
        }
        return channels;
    }

    /// <summary>The curves a set of tracks asks for, each validated against the frame count.</summary>
    private static List<CurveJob> Jobs(int frameCount, IReadOnlyList<ClipTrack> tracks, IReadOnlyList<ClipFloatTrack> floatTracks)
    {
        List<CurveJob> jobs = new();
        foreach (ClipTrack track in tracks)
        {
            if (track.Rotations is not null)
            {
                Validate(track.Rotations.Length, frameCount, track.Path);
                jobs.Add(new CurveJob(CurveKind.Rotation, track, null));
            }
            if (track.Positions is not null)
            {
                Validate(track.Positions.Length, frameCount, track.Path);
                jobs.Add(new CurveJob(CurveKind.Position, track, null));
            }
            if (track.Scales is not null)
            {
                Validate(track.Scales.Length, frameCount, track.Path);
                jobs.Add(new CurveJob(CurveKind.Scale, track, null));
            }
        }
        foreach (ClipFloatTrack track in floatTracks)
        {
            Validate(track.Values.Length, frameCount, track.Path + "/" + track.Attribute);
            jobs.Add(new CurveJob(CurveKind.Float, null, track));
        }
        return jobs;
    }

    private static void Validate(int length, int frameCount, string path)
    {
        if (length != frameCount)
        {
            throw new ArgumentException($"[ClipBuilder] '{path}' has {length} samples for {frameCount} frames.");
        }
    }

    private static ReducedCurve Reduce(CurveJob job, float sampleRate, ClipTolerance? tolerance)
    {
        switch (job.Kind)
        {
            case CurveKind.Rotation:
                Quaternion[] rotations = job.Track!.Rotations!;
                Unit(rotations);
                return CurveReducer.Reduce(Components(rotations), sampleRate, tolerance?.RotationRadians, CurveMetric.Angular);
            case CurveKind.Position:
                return CurveReducer.Reduce(Components(job.Track!.Positions!), sampleRate, tolerance?.Position, CurveMetric.Euclidean);
            case CurveKind.Scale:
                return CurveReducer.Reduce(Components(job.Track!.Scales!), sampleRate, tolerance?.Scale, CurveMetric.Euclidean);
            default:
                return CurveReducer.Reduce([job.FloatTrack!.Values], sampleRate, tolerance?.Float, CurveMetric.Euclidean);
        }
    }

    private static float[][] Components(Quaternion[] values)
    {
        float[] x = new float[values.Length];
        float[] y = new float[values.Length];
        float[] z = new float[values.Length];
        float[] w = new float[values.Length];
        for (int frame = 0; frame < values.Length; frame++)
        {
            Quaternion value = values[frame];
            x[frame] = value.X;
            y[frame] = value.Y;
            z[frame] = value.Z;
            w[frame] = value.W;
        }
        return [x, y, z, w];
    }

    private static float[][] Components(Vector3[] values)
    {
        float[] x = new float[values.Length];
        float[] y = new float[values.Length];
        float[] z = new float[values.Length];
        for (int frame = 0; frame < values.Length; frame++)
        {
            Vector3 value = values[frame];
            x[frame] = value.X;
            y[frame] = value.Y;
            z[frame] = value.Z;
        }
        return [x, y, z];
    }

    private static void Unit(Quaternion[] rotations)
    {
        for (int frame = 0; frame < rotations.Length; frame++)
        {
            Quaternion rotation = Quaternion.Normalize(rotations[frame]);
            rotations[frame] = frame > 0 && Quaternion.Dot(rotation, rotations[frame - 1]) < 0f ? -rotation : rotation;
        }
    }
}
