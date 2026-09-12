namespace Ruri.RipperHook.Conversion;

/// <summary>
/// How far a played value is from a sample: the straight-line distance for positions, scales
/// and floats, the rotation angle between two unit quaternions for rotations.
/// </summary>
public enum CurveMetric
{
    Euclidean,
    Angular,
}

/// <summary>
/// A sampled curve reduced to the keys that carry it: the sample index of every kept key and,
/// per component, the slope each key leaves with and the slope it arrives with -- the two free
/// tangents of a Unity keyframe, fitted per segment.
/// </summary>
public sealed class ReducedCurve
{
    public required int[] Frames { get; init; }

    public required float[][] InSlopes { get; init; }

    public required float[][] OutSlopes { get; init; }
}

/// <summary>
/// The keys a dense sampling needs to reproduce itself within a tolerance on the curve Unity
/// will play. Every segment between two kept keys is the cubic Hermite that fits its samples
/// best in the least-squares sense with both slopes free (a keyframe's out slope and the next
/// keyframe's in slope belong to that segment alone), so the noise of a single sample step
/// never reaches a tangent; a segment is split at the sample its fit misses by more than the
/// tolerance, until no sample is missed. The first and last samples always stay. With no
/// tolerance every sample stays and each segment is the straight line between its two samples.
/// A run of identical samples is a quantization plateau -- the source rounded a moving value
/// to one level for those frames -- so each of its samples is known only to within half the
/// smaller of the two jumps bounding the run, and the tolerance widens by that much there; a
/// sample nothing repeats keeps the bare tolerance.
/// </summary>
public static class CurveReducer
{
    private const double ChordPrior = 1e-6;

    public static ReducedCurve Reduce(float[][] components, float sampleRate, float? tolerance, CurveMetric metric)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Length == 0)
        {
            throw new ArgumentException("A curve needs at least one component.", nameof(components));
        }
        if (sampleRate <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "A curve needs a positive sample rate.");
        }
        int count = components[0].Length;
        foreach (float[] component in components)
        {
            if (component.Length != count)
            {
                throw new ArgumentException("Every component of a curve has one value per sample.", nameof(components));
            }
        }
        int[] frames = tolerance is null ? All(count) : Keep(components, count, sampleRate, Limits(components, count, tolerance.Value, metric), metric);
        return Fit(components, frames, sampleRate);
    }

    private static int[] All(int count)
    {
        int[] all = new int[count];
        for (int index = 0; index < count; index++)
        {
            all[index] = index;
        }
        return all;
    }

    /// <summary>
    /// The miss each sample tolerates, in the metric's own terms: the squared distance for the
    /// Euclidean metric, the squared cosine of half the angle for the angular one (a quaternion
    /// component uncertainty of d is an angle of about 2d).
    /// </summary>
    private static double[] Limits(float[][] components, int count, float tolerance, CurveMetric metric)
    {
        double[] band = new double[count];
        foreach (float[] values in components)
        {
            int first = 0;
            while (first < count)
            {
                int last = first;
                while (last + 1 < count && values[last + 1] == values[first])
                {
                    last++;
                }
                if (last > first)
                {
                    double into = first > 0 ? Math.Abs(values[first] - values[first - 1]) : double.PositiveInfinity;
                    double outOf = last + 1 < count ? Math.Abs(values[last + 1] - values[last]) : double.PositiveInfinity;
                    double half = Math.Min(into, outOf) * 0.5;
                    if (double.IsFinite(half))
                    {
                        for (int sample = first; sample <= last; sample++)
                        {
                            band[sample] += half * half;
                        }
                    }
                }
                first = last + 1;
            }
        }
        double[] limits = new double[count];
        for (int sample = 0; sample < count; sample++)
        {
            double allowed = tolerance + (metric == CurveMetric.Angular ? 2d : 1d) * Math.Sqrt(band[sample]);
            limits[sample] = metric == CurveMetric.Angular
                ? Math.Cos(allowed * 0.5) * Math.Cos(allowed * 0.5)
                : allowed * allowed;
        }
        return limits;
    }

    private static int[] Keep(float[][] components, int count, float sampleRate, double[] limits, CurveMetric metric)
    {
        if (count <= 2)
        {
            return All(count);
        }
        Span<double> outSlopes = stackalloc double[components.Length];
        Span<double> inSlopes = stackalloc double[components.Length];
        Span<double> scratch = stackalloc double[components.Length * 2];
        List<int> kept = new() { 0, count - 1 };
        Stack<(int First, int Last)> pending = new();
        pending.Push((0, count - 1));
        while (pending.Count > 0)
        {
            (int first, int last) = pending.Pop();
            if (last - first < 3)
            {
                continue;
            }
            FitSegment(components, first, last, sampleRate, outSlopes, inSlopes, scratch);
            int worst = Worst(components, first, last, sampleRate, outSlopes, inSlopes, metric, limits);
            if (worst < 0)
            {
                continue;
            }
            kept.Add(worst);
            pending.Push((first, worst));
            pending.Push((worst, last));
        }
        kept.Sort();
        return kept.ToArray();
    }

    private static ReducedCurve Fit(float[][] components, int[] frames, float sampleRate)
    {
        int keys = frames.Length;
        float[][] inSlopes = new float[components.Length][];
        float[][] outSlopes = new float[components.Length][];
        for (int component = 0; component < components.Length; component++)
        {
            inSlopes[component] = new float[keys];
            outSlopes[component] = new float[keys];
        }
        Span<double> outgoing = stackalloc double[components.Length];
        Span<double> incoming = stackalloc double[components.Length];
        Span<double> scratch = stackalloc double[components.Length * 2];
        for (int key = 0; key + 1 < keys; key++)
        {
            FitSegment(components, frames[key], frames[key + 1], sampleRate, outgoing, incoming, scratch);
            for (int component = 0; component < components.Length; component++)
            {
                outSlopes[component][key] = (float)outgoing[component];
                inSlopes[component][key + 1] = (float)incoming[component];
            }
        }
        if (keys > 1)
        {
            for (int component = 0; component < components.Length; component++)
            {
                inSlopes[component][0] = outSlopes[component][0];
                outSlopes[component][keys - 1] = inSlopes[component][keys - 1];
            }
        }
        return new ReducedCurve { Frames = frames, InSlopes = inSlopes, OutSlopes = outSlopes };
    }

    /// <summary>
    /// The out slope of key <paramref name="first"/> and the in slope of key <paramref name="last"/>
    /// that fit the samples between them best, per component. The normal equations share their
    /// basis sums across components and are pulled toward the straight line between the two keys
    /// by a weight a million times smaller than the fit itself, so a segment with a single
    /// interior sample, which any slopes reproduce, takes the straightest of them.
    /// </summary>
    private static void FitSegment(float[][] components, int first, int last, float sampleRate,
        Span<double> outSlopes, Span<double> inSlopes, Span<double> scratch)
    {
        int steps = last - first;
        double span = steps / (double)sampleRate;
        Span<double> outgoing = scratch[..components.Length];
        Span<double> incoming = scratch[components.Length..];
        outgoing.Clear();
        incoming.Clear();
        double outOut = 0d;
        double outIn = 0d;
        double inIn = 0d;
        for (int sample = first + 1; sample < last; sample++)
        {
            double fraction = (sample - first) / (double)steps;
            double squared = fraction * fraction;
            double cubed = squared * fraction;
            double h00 = 2d * cubed - 3d * squared + 1d;
            double h01 = 1d - h00;
            double h10 = (cubed - 2d * squared + fraction) * span;
            double h11 = (cubed - squared) * span;
            outOut += h10 * h10;
            outIn += h10 * h11;
            inIn += h11 * h11;
            for (int component = 0; component < components.Length; component++)
            {
                float[] values = components[component];
                double residual = values[sample] - h00 * values[first] - h01 * values[last];
                outgoing[component] += h10 * residual;
                incoming[component] += h11 * residual;
            }
        }
        double prior = ChordPrior * (outOut + inIn);
        double determinant = (outOut + prior) * (inIn + prior) - outIn * outIn;
        for (int component = 0; component < components.Length; component++)
        {
            float[] values = components[component];
            double chord = (values[last] - values[first]) / span;
            if (determinant <= 0d)
            {
                outSlopes[component] = chord;
                inSlopes[component] = chord;
                continue;
            }
            double outgoingSum = outgoing[component] + prior * chord;
            double incomingSum = incoming[component] + prior * chord;
            outSlopes[component] = ((inIn + prior) * outgoingSum - outIn * incomingSum) / determinant;
            inSlopes[component] = ((outOut + prior) * incomingSum - outIn * outgoingSum) / determinant;
        }
    }

    /// <summary>The sample the fitted segment misses by the most beyond its limit, or -1 when every sample is within its limit.</summary>
    private static int Worst(float[][] components, int first, int last, float sampleRate,
        ReadOnlySpan<double> outSlopes, ReadOnlySpan<double> inSlopes, CurveMetric metric, double[] limits)
    {
        int steps = last - first;
        double span = steps / (double)sampleRate;
        int worst = -1;
        double worstMiss = 0d;
        for (int sample = first + 1; sample < last; sample++)
        {
            double fraction = (sample - first) / (double)steps;
            double squared = fraction * fraction;
            double cubed = squared * fraction;
            double h00 = 2d * cubed - 3d * squared + 1d;
            double h01 = 1d - h00;
            double h10 = (cubed - 2d * squared + fraction) * span;
            double h11 = (cubed - squared) * span;
            double miss;
            if (metric == CurveMetric.Angular)
            {
                double dot = 0d;
                double norm = 0d;
                for (int component = 0; component < components.Length; component++)
                {
                    float[] values = components[component];
                    double played = h00 * values[first] + h01 * values[last] + h10 * outSlopes[component] + h11 * inSlopes[component];
                    dot += played * values[sample];
                    norm += played * played;
                }
                miss = limits[sample] - (norm > 0d ? dot * dot / norm : 0d);
            }
            else
            {
                double distance = 0d;
                for (int component = 0; component < components.Length; component++)
                {
                    float[] values = components[component];
                    double played = h00 * values[first] + h01 * values[last] + h10 * outSlopes[component] + h11 * inSlopes[component];
                    double difference = played - values[sample];
                    distance += difference * difference;
                }
                miss = distance - limits[sample];
            }
            if (miss > worstMiss)
            {
                worstMiss = miss;
                worst = sample;
            }
        }
        return worst;
    }
}
