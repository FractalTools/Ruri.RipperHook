using System.Numerics;
using AssetRipper.Export.Modules.Models;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Single;
using SharpGLTF.Scenes;

namespace Ruri.RipperHook.GlbExporter;

/// <summary>
/// Bakes a humanoid clip's muscle/root float curves into per-bone glTF rotation/translation
/// tracks, sampled at the clip's SampleRate. Sampling and composition are a 1:1 port of the
/// validated Blender-side baker (RuriRipperImporter/animation_builder.py:228-295 `_bake_muscles`):
/// non-hips bones get rest * delta on the prefab rest pose, the hips get RootT-MotionT plus
/// RootQ pre-multiplying their rest orientation, and the extracted root motion (MotionT/MotionQ)
/// is baked onto the animator root node so the composed total always equals RootT/RootQ.
/// </summary>
public static class HumanoidClipBaker
{
    /// <summary>Bake one clip. Returns the number of bone tracks written (0 = not a humanoid clip).</summary>
    public static int Bake(
        IAnimationClip clip,
        AvatarMuscleReferential referential,
        IReadOnlyDictionary<string, NodeBuilder> nodeByPath,
        IReadOnlyDictionary<string, UnityLocalTransform> restByPath,
        string trackName)
    {
        List<(string Attribute, HermiteCurve Curve)> channels = CollectMuscleChannels(clip);
        if (channels.Count == 0)
        {
            return 0;
        }

        float sampleRate = clip.SampleRate_C74 > 0f ? clip.SampleRate_C74 : 60f;
        float duration = 0f;
        foreach ((_, HermiteCurve curve) in channels)
        {
            duration = MathF.Max(duration, curve.LastTime);
        }
        int frameCount = Math.Max(1, (int)MathF.Round(duration * sampleRate) + 1);

        // 每通道每帧只求值一次,全体骨骼共享(animation_builder.py:244-248)。
        Dictionary<string, int> channelIndex = new(channels.Count, StringComparer.Ordinal);
        for (int i = 0; i < channels.Count; i++)
        {
            channelIndex[channels[i].Attribute] = i;
        }
        float[,] values = new float[frameCount, channels.Count];
        for (int c = 0; c < channels.Count; c++)
        {
            HermiteCurve curve = channels[c].Curve;
            for (int f = 0; f < frameCount; f++)
            {
                values[f, c] = curve.Evaluate(f / sampleRate);
            }
        }

        int trackCount = 0;
        foreach (MuscleBone bone in referential.DrivenBones)
        {
            if (!nodeByPath.TryGetValue(bone.Path, out NodeBuilder? node)
                || !restByPath.TryGetValue(bone.Path, out UnityLocalTransform rest))
            {
                continue;
            }

            if (bone.IsHips)
            {
                trackCount += BakeHips(node, rest, values, channelIndex, sampleRate, frameCount, trackName);
            }
            else
            {
                trackCount += BakeMuscleBone(bone, node, rest, values, channelIndex, sampleRate, frameCount, trackName);
            }
        }

        trackCount += BakeRootMotion(nodeByPath, values, channelIndex, sampleRate, frameCount, trackName);
        return trackCount;
    }

    private static int BakeMuscleBone(MuscleBone bone, NodeBuilder node, UnityLocalTransform rest,
        float[,] values, Dictionary<string, int> channelIndex, float sampleRate, int frameCount, string trackName)
    {
        // 该骨骼三轴肌肉都不在 clip 里就没有可烘的数据(保持 rest,不写轨道)。
        bool hasAnyMuscle = false;
        for (int dof = 0; dof < 3; dof++)
        {
            string? muscle = bone.GetDofMuscle(dof);
            if (muscle is not null && channelIndex.ContainsKey(muscle))
            {
                hasAnyMuscle = true;
                break;
            }
        }
        if (!hasAnyMuscle)
        {
            return 0;
        }

        SharpGLTF.Animations.CurveBuilder<Quaternion> rotation = node.UseRotation(trackName);
        for (int f = 0; f < frameCount; f++)
        {
            int frame = f;
            Quaternion delta = AvatarMuscleReferential.LocalDelta(bone,
                attribute => channelIndex.TryGetValue(attribute, out int c) ? values[frame, c] : null);
            // anim_quat = rest_quat * delta(animation_builder.py:287)。
            Quaternion animLocal = rest.Rotation * delta;
            rotation.SetPoint(f / sampleRate, GlbCoordinateConversion.ToGltfQuaternionConvert(animLocal));
        }
        return 1;
    }

    private static int BakeHips(NodeBuilder node, UnityLocalTransform rest,
        float[,] values, Dictionary<string, int> channelIndex, float sampleRate, int frameCount, string trackName)
    {
        if (!channelIndex.ContainsKey("RootT.x") && !channelIndex.ContainsKey("RootT.y")
            && !channelIndex.ContainsKey("RootT.z"))
        {
            return 0;
        }

        SharpGLTF.Animations.CurveBuilder<Vector3> translation = node.UseTranslation(trackName);
        SharpGLTF.Animations.CurveBuilder<Quaternion> rotation = node.UseRotation(trackName);
        for (int f = 0; f < frameCount; f++)
        {
            int frame = f;
            (Vector3 position, Quaternion bodyRotation) = AvatarMuscleReferential.BodyTransform(
                attribute => channelIndex.TryGetValue(attribute, out int c) ? values[frame, c] : null)
                ?? (rest.Position, Quaternion.Identity);
            float time = f / sampleRate;
            translation.SetPoint(time, GlbCoordinateConversion.ToGltfVector3Convert(position));
            // body 旋转在动画根坐标系里,前乘 hips 的 rest 朝向(animation_builder.py:268-279)。
            rotation.SetPoint(time, GlbCoordinateConversion.ToGltfQuaternionConvert(bodyRotation * rest.Rotation));
        }
        return 1;
    }

    /// <summary>
    /// MotionT/MotionQ = 从身体轨迹中抽取的根运动,烘到 animator 根节点(路径 "")上;
    /// hips 侧存的是 RootT-MotionT,二者复合恒等于完整的 RootT 轨迹。
    /// </summary>
    private static int BakeRootMotion(IReadOnlyDictionary<string, NodeBuilder> nodeByPath,
        float[,] values, Dictionary<string, int> channelIndex, float sampleRate, int frameCount, string trackName)
    {
        bool hasMotionT = channelIndex.ContainsKey("MotionT.x") || channelIndex.ContainsKey("MotionT.y")
            || channelIndex.ContainsKey("MotionT.z");
        bool hasMotionQ = channelIndex.ContainsKey("MotionQ.w");
        if ((!hasMotionT && !hasMotionQ) || !nodeByPath.TryGetValue(string.Empty, out NodeBuilder? root))
        {
            return 0;
        }

        SharpGLTF.Animations.CurveBuilder<Vector3>? translation = hasMotionT ? root.UseTranslation(trackName) : null;
        SharpGLTF.Animations.CurveBuilder<Quaternion>? rotation = hasMotionQ ? root.UseRotation(trackName) : null;
        for (int f = 0; f < frameCount; f++)
        {
            float time = f / sampleRate;
            if (translation is not null)
            {
                Vector3 motion = new(Sample(values, channelIndex, "MotionT.x", f),
                    Sample(values, channelIndex, "MotionT.y", f),
                    Sample(values, channelIndex, "MotionT.z", f));
                translation.SetPoint(time, GlbCoordinateConversion.ToGltfVector3Convert(motion));
            }
            if (rotation is not null)
            {
                Quaternion motionQ = new(Sample(values, channelIndex, "MotionQ.x", f),
                    Sample(values, channelIndex, "MotionQ.y", f),
                    Sample(values, channelIndex, "MotionQ.z", f),
                    Sample(values, channelIndex, "MotionQ.w", f));
                if (motionQ.LengthSquared() > 1e-12f)
                {
                    rotation.SetPoint(time, GlbCoordinateConversion.ToGltfQuaternionConvert(Quaternion.Normalize(motionQ)));
                }
            }
        }
        return (translation is null ? 0 : 1) + (rotation is null ? 0 : 1);
    }

    private static float Sample(float[,] values, Dictionary<string, int> channelIndex, string attribute, int frame)
    {
        return channelIndex.TryGetValue(attribute, out int c) ? values[frame, c] : 0f;
    }

    private static List<(string, HermiteCurve)> CollectMuscleChannels(IAnimationClip clip)
    {
        List<(string, HermiteCurve)> channels = new();
        foreach (IFloatCurve floatCurve in clip.FloatCurves_C74)
        {
            string attribute = floatCurve.Attribute.String;
            if (!AvatarMuscleReferential.IsMuscleAttribute(attribute)
                && !AvatarMuscleReferential.IsRootAttribute(attribute))
            {
                continue;
            }
            HermiteCurve curve = HermiteCurve.FromKeyframes(floatCurve.Curve.Curve);
            if (curve.KeyCount > 0)
            {
                channels.Add((attribute, curve));
            }
        }
        return channels;
    }
}

/// <summary>
/// A single scalar Unity AnimationCurve channel with cubic-Hermite evaluation
/// (1:1 with RuriRipperImporter/animation_builder.py:29-76 `_HermiteCurve`): keys sorted by time,
/// clamped outside the key range, slopes scaled by the segment length.
/// </summary>
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
        // Unity 序列化的 key 本应有序;仅在乱序时重排一次,保证求值二分成立。
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

/// <summary>Unity 本地 TRS(prefab rest 姿势),烘焙的组合基准。</summary>
public readonly record struct UnityLocalTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);
