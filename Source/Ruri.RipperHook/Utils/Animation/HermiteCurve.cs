using AssetRipper.SourceGenerated.Subclasses.Keyframe_Single;

namespace Ruri.RipperHook.Animation;

public sealed class HermiteCurve
{
    private readonly float[] _times;
    private readonly float[] _values;
    private readonly float[] _inSlopes;
    private readonly float[] _outSlopes;

    public int KeyCount => _times.Length;
    public float LastTime => _times.Length == 0 ? 0f : _times[^1];

    private HermiteCurve(float[] times, float[] values, float[] inSlopes, float[] outSlopes)
    {
        _times = times;
        _values = values;
        _inSlopes = inSlopes;
        _outSlopes = outSlopes;
    }

    /// <summary>From already-materialized key arrays (a curve blob crossing the bridge); the
    /// arrays are taken over, sorted by time in place when needed.</summary>
    public static HermiteCurve FromArrays(float[] times, float[] values, float[] inSlopes, float[] outSlopes)
    {
        SortByTime(times, values, inSlopes, outSlopes);
        return new HermiteCurve(times, values, inSlopes, outSlopes);
    }

    public static HermiteCurve FromKeyframes(IReadOnlyList<IKeyframe_Single> keys)
    {
        int count = keys.Count;
        float[] times = new float[count];
        float[] values = new float[count];
        float[] inSlopes = new float[count];
        float[] outSlopes = new float[count];
        for (int i = 0; i < count; i++)
        {
            IKeyframe_Single key = keys[i];
            times[i] = key.Time;
            values[i] = key.Value;
            inSlopes[i] = key.InSlope;
            outSlopes[i] = key.OutSlope;
        }
        SortByTime(times, values, inSlopes, outSlopes);
        return new HermiteCurve(times, values, inSlopes, outSlopes);
    }

    private static void SortByTime(float[] times, float[] values, float[] inSlopes, float[] outSlopes)
    {
        bool sorted = true;
        for (int i = 1; i < times.Length; i++)
        {
            if (times[i] < times[i - 1])
            {
                sorted = false;
                break;
            }
        }
        if (sorted)
        {
            return;
        }
        int[] order = new int[times.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }
        float[] timesCopy = (float[])times.Clone();
        Array.Sort(timesCopy, order);
        Reorder(times, order);
        Reorder(values, order);
        Reorder(inSlopes, order);
        Reorder(outSlopes, order);
    }

    private static void Reorder(float[] array, int[] order)
    {
        float[] copy = (float[])array.Clone();
        for (int i = 0; i < order.Length; i++)
        {
            array[i] = copy[order[i]];
        }
    }

    public void SampleUniform(float[] destination, int column, int stride, int frameCount, float sampleRate)
    {
        int n = _times.Length;
        if (n == 0)
        {
            return;
        }
        if (n == 1)
        {
            float only = _values[0];
            for (int f = 0; f < frameCount; f++)
            {
                destination[f * stride + column] = only;
            }
            return;
        }

        float first = _times[0];
        float last = _times[n - 1];
        int segment = 0;
        for (int f = 0; f < frameCount; f++)
        {
            float t = f / sampleRate;
            float value;
            if (t <= first)
            {
                value = _values[0];
            }
            else if (t >= last)
            {
                value = _values[n - 1];
            }
            else
            {
                while (segment < n - 2 && _times[segment + 1] <= t)
                {
                    segment++;
                }
                float t0 = _times[segment];
                float dt = _times[segment + 1] - t0;
                if (dt <= 1e-9f)
                {
                    value = _values[segment];
                }
                else
                {
                    float u = (t - t0) / dt;
                    float u2 = u * u;
                    float u3 = u2 * u;
                    value = (2f * u3 - 3f * u2 + 1f) * _values[segment]
                        + (u3 - 2f * u2 + u) * (_outSlopes[segment] * dt)
                        + (-2f * u3 + 3f * u2) * _values[segment + 1]
                        + (u3 - u2) * (_inSlopes[segment + 1] * dt);
                }
            }
            destination[f * stride + column] = value;
        }
    }

    public float Evaluate(float t)
    {
        int n = _times.Length;
        if (n == 0)
        {
            return 0f;
        }
        if (t <= _times[0])
        {
            return _values[0];
        }
        if (t >= _times[n - 1])
        {
            return _values[n - 1];
        }
        int i = Array.BinarySearch(_times, t);
        if (i < 0)
        {
            i = ~i - 1;
        }
        i = Math.Clamp(i, 0, n - 2);
        float t0 = _times[i];
        float t1 = _times[i + 1];
        float dt = t1 - t0;
        if (dt <= 1e-9f)
        {
            return _values[i];
        }
        float u = (t - t0) / dt;
        float v0 = _values[i];
        float v1 = _values[i + 1];
        float m0 = _outSlopes[i] * dt;
        float m1 = _inSlopes[i + 1] * dt;
        float u2 = u * u;
        float u3 = u2 * u;
        float h00 = 2f * u3 - 3f * u2 + 1f;
        float h10 = u3 - 2f * u2 + u;
        float h01 = -2f * u3 + 3f * u2;
        float h11 = u3 - u2;
        return h00 * v0 + h10 * m0 + h01 * v1 + h11 * m1;
    }
}
