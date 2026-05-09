// 与 AssetStudio (VibeStudio) 等价的 AnimationClip 处理器, 完整替换
// AssetRipper.Processing.AnimationClips.AnimationClipConverter.ProcessInner.
//
// 设计原则: 本类 *不感知任何具体游戏*. 它只接受标准 Unity 形态的数据 ——
// StreamedClip / DenseClip / ConstantClip 已被填好, 二进制 binding 路径走 m_ClipBindingConstant,
// CompressedRotationCurves 是合法的 PackedBitVector. 各游戏的特殊编码 (Mihoyo ACL,
// Endfield AKEF, ZZZ ACL2, LnD, etc.) 应当在游戏自己的反序列化 hook 里完成 *回填*
// (像 ExAstrisCommon_Hook.DenseClip_ReadRelease 把 ACL 字节合并进 m_SampleArray 一样),
// 让本处理器看到的就是 Unity 原生的曲线数据.
//
// 与 AR 原版的差异只有一处: AR 的 ProcessInner 不解码 m_CompressedRotationCurves
// (legacy clip), 我们补上, 时间字段按 AssetStudio 的累积差值正确解释.

using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Checksum;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.Processing.AnimationClips;
using AssetRipper.Processing.AnimationClips.Editor;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip;
using AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.GenericBinding;
using AssetRipper.SourceGenerated.Extensions.Enums.Keyframe.TangentMode;
using AssetRipper.SourceGenerated.Subclasses.AnimationClipBindingConstant;
using AssetRipper.SourceGenerated.Subclasses.Clip;
using AssetRipper.SourceGenerated.Subclasses.CompressedAnimationCurve;
using AssetRipper.SourceGenerated.Subclasses.ConstantClip;
using AssetRipper.SourceGenerated.Subclasses.DenseClip;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using AssetRipper.SourceGenerated.Subclasses.GenericBinding;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Quaternionf;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Single;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Vector3f;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Object;
using AssetRipper.SourceGenerated.Subclasses.PPtrCurve;
using AssetRipper.SourceGenerated.Subclasses.PPtrKeyframe;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.StreamedClip;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ruri.RipperHook.AnimationExport;

internal sealed class VibeStudioAnimationProcessor
{
    private const float DefaultFloatWeight = 1.0f / 3.0f;
    private const string UnknownPathPrefix = "path_";
    private const string MissedPropertyPrefix = "missed_";
    private const string ScriptPropertyPrefix = "script_";
    private const string TypeTreePropertyPrefix = "typetree_";

    private readonly IAnimationClip m_clip;
    private readonly PathChecksumCache m_checksumCache;
    private readonly CustomCurveResolver m_customCurveResolver;

    private readonly Dictionary<string, IVector3Curve> m_translations = new();
    private readonly Dictionary<string, IQuaternionCurve> m_rotations = new();
    private readonly Dictionary<string, IVector3Curve> m_scales = new();
    private readonly Dictionary<string, IVector3Curve> m_eulers = new();
    private readonly Dictionary<CurveData, IFloatCurve> m_floats = new();
    private readonly Dictionary<CurveData, IPPtrCurve> m_pptrs = new();
    private readonly Dictionary<int, IGenericBinding> m_bindingsCache = new();

    private VibeStudioAnimationProcessor(IAnimationClip clip, PathChecksumCache checksumCache)
    {
        m_clip = clip;
        m_checksumCache = checksumCache;
        m_customCurveResolver = new CustomCurveResolver(clip);
    }

    public static void Process(IAnimationClip clip, PathChecksumCache checksumCache)
    {
        var processor = new VibeStudioAnimationProcessor(clip, checksumCache);
        processor.ProcessInner();
    }

    private UnityVersion Version => m_clip.Collection.Version;
    private IAnimationClipBindingConstant ClipBindingConstant => m_clip.ClipBindingConstant_C74!;

    private void ProcessInner()
    {
        // (a) Legacy compressed rotation curves: AR 自带 Unpack 拓展, 但其时间字段被当作绝对值;
        // AssetStudio 把它视为累积差值 (这是 Unity 的真实编码). 我们采用 AssetStudio 的累积写法.
        ProcessCompressedRotationCurves();

        // (b) Generic 路径 (muscle clip): 与 AR 原版完全一致.
        // 各游戏特殊 ACL 已经在它自己的反序列化 hook 里被回填进 dense/streamed/constant,
        // 走到这里时数据已经是标准 Unity 形态.
        if (!m_clip.Has_ClipBindingConstant_C74()) return;

        IClip clip = m_clip.MuscleClip_C74.Clip.Data;

        IReadOnlyList<StreamedFrame> streamedFrames = GenerateFramesFromStreamedClip(clip.StreamedClip);
        ProcessStreams(streamedFrames);

        int streamedCurveCount = clip.StreamedClip.CurveCount();
        ProcessDenses(clip.DenseClip, streamedCurveCount);

        if (clip.Has_ConstantClip())
        {
            int preConstantCurves = streamedCurveCount + (int)clip.DenseClip.CurveCount;
            float lastConstantTime = CalculateLastConstantTime(streamedFrames, m_clip.MuscleClip_C74.StopTime);
            ProcessConstant(clip.ConstantClip, preConstantCurves, lastConstantTime);
        }

        m_clip.MuscleClipInfo_C74.Initialize(m_clip.MuscleClip_C74);
    }

    // === Compressed Rotation Curves =========================================
    // AssetStudio 用累积差值解释 m_Times (Unity 真实编码就是 deltas);
    // AR 自带的 CompressedAnimationCurveExtensions.Unpack 把它当绝对值, 因此时间不正确.
    private void ProcessCompressedRotationCurves()
    {
        AccessListBase<CompressedAnimationCurve> compressed = m_clip.CompressedRotationCurves_C74;
        if (compressed.Count == 0) return;

        foreach (CompressedAnimationCurve src in compressed)
        {
            int[] timeDeltas = src.Times.UnpackInts();
            int numKeys = timeDeltas.Length;
            float[] times = new float[numKeys];
            int t = 0;
            for (int i = 0; i < numKeys; i++)
            {
                t += timeDeltas[i];
                times[i] = t * 0.01f;
            }
            Quaternion[] quats = src.Values.Unpack();
            float[] slopes = src.Slopes.Unpack();

            string path = src.Path;
            if (!m_rotations.TryGetValue(path, out IQuaternionCurve? destCurve))
            {
                destCurve = m_clip.RotationCurves_C74.AddNew();
                destCurve.SetValues(path);
                m_rotations.Add(path, destCurve);
            }
            destCurve.Curve.PreInfinity = src.PreInfinity;
            destCurve.Curve.PostInfinity = src.PostInfinity;
            destCurve.Curve.SetDefaultRotationOrderAndCurveLoopType();

            for (int i = 0, j = 4; i < numKeys; i++, j += 4)
            {
                Quaternion rotation = quats[i];
                Quaternion inSlope = j - 4 + 3 < slopes.Length
                    ? new Quaternion(slopes[j - 4], slopes[j - 3], slopes[j - 2], slopes[j - 1])
                    : default;
                Quaternion outSlope = j + 3 < slopes.Length
                    ? new Quaternion(slopes[j + 0], slopes[j + 1], slopes[j + 2], slopes[j + 3])
                    : default;

                IKeyframe_Quaternionf key = destCurve.Curve.Curve.AddNew();
                key.SetValues(Version, times[i], rotation, inSlope, outSlope, DefaultFloatWeight);
            }
        }
    }

    // === Port of AR's ProcessStreams ========================================
    private void ProcessStreams(IReadOnlyList<StreamedFrame> streamedFrames)
    {
        Span<float> curveValues = stackalloc float[4];
        Span<float> inSlopeValues = stackalloc float[4];
        Span<float> outSlopeValues = stackalloc float[4];
        bool useNegInfSlopes = m_clip.SupportsNegativeInfinitySlopes();

        if (streamedFrames.Count > 1)
        {
            streamedFrames[0].Time = 0f;
        }
        int frameCount = streamedFrames.Count - 1;
        for (int frameIdx = 0; frameIdx < frameCount; frameIdx++)
        {
            bool doSlopeCalc = frameIdx != frameCount - 1;
            StreamedFrame frame = streamedFrames[frameIdx];
            for (int curveIdx = 0; curveIdx < frame.Curves.Length;)
            {
                int curveID = frame.Curves[curveIdx].Index;
                IGenericBinding binding = GetBinding(curveID);
                string path = GetCurvePath(binding.Path);
                StreamedCurveKey curve;
                if (binding.IsTransform())
                {
                    int transformDim = binding.TransformType().GetDimension();
                    if (frameIdx == 0)
                    {
                        curveIdx += transformDim;
                        continue;
                    }
                    for (int offset = 0; offset < transformDim; offset++)
                    {
                        curve = frame.Curves[curveIdx];
                        if (doSlopeCalc)
                        {
                            if (TryGetNextFrame(streamedFrames, frameIdx, curveID, out StreamedFrame? nextFrame, out int nextCurveIdx))
                            {
                                StreamedCurveKey nextCurve = nextFrame.Curves[nextCurveIdx + offset];
                                curve.CalculateSlopes(frame.Time, nextFrame.Time, nextCurve, useNegInfSlopes);
                            }
                        }
                        curveValues[offset] = curve.Value;
                        inSlopeValues[offset] = curve.InSlope;
                        outSlopeValues[offset] = curve.OutSlope;
                        curveIdx++;
                    }
                    AddTransformCurveFromBuffer(frame.Time, binding, curveValues, 0, inSlopeValues, outSlopeValues, path);
                    continue;
                }
                curve = frame.Curves[curveIdx];
                if (!binding.IsPPtrCurve())
                {
                    if (frameIdx == 0)
                    {
                        curveIdx++;
                        continue;
                    }
                    if (doSlopeCalc)
                    {
                        if (TryGetNextFrame(streamedFrames, frameIdx, curveID, out StreamedFrame? nextFrame, out int nextCurveIdx))
                        {
                            StreamedCurveKey nextCurve = nextFrame.Curves[nextCurveIdx];
                            curve.CalculateSlopes(frame.Time, nextFrame.Time, nextCurve, useNegInfSlopes);
                        }
                    }
                }
                if (binding.CustomType == (byte)BindingCustomType.None)
                {
                    AddDefaultCurve(binding, path, frame.Time, curve.Value, curve.InSlope, curve.OutSlope);
                }
                else
                {
                    AddCustomCurve(binding, path, frame.Time, curve.Value, curve.InSlope, curve.OutSlope);
                }
                curveIdx++;
            }
        }
    }

    private void ProcessDenses(IDenseClip dense, int preDenseCurves)
    {
        ReadOnlySpan<float> slopeValues = stackalloc float[4];

        int sampleCount = dense.SampleArray.Count;
        if (sampleCount == 0) return;

        float[] rented = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            dense.SampleArray.CopyTo(rented);
            ReadOnlySpan<float> curveValues = new(rented, 0, sampleCount);

            for (int frameIndex = 0; frameIndex < dense.FrameCount; frameIndex++)
            {
                float time = frameIndex / dense.SampleRate + dense.BeginTime;
                int frameOffset = frameIndex * (int)dense.CurveCount;
                for (int curveIndex = 0; curveIndex < dense.CurveCount;)
                {
                    int globalIndex = preDenseCurves + curveIndex;
                    IGenericBinding binding = GetBinding(globalIndex);
                    string path = GetCurvePath(binding.Path);
                    int framePosition = frameOffset + curveIndex;
                    if (binding.IsTransform())
                    {
                        AddTransformCurveFromBuffer(time, binding, curveValues, framePosition, slopeValues, slopeValues, path);
                        curveIndex += binding.TransformType().GetDimension();
                    }
                    else if (binding.CustomType == (byte)BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, dense.SampleArray[framePosition]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(binding, path, time, dense.SampleArray[framePosition]);
                        curveIndex++;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private void ProcessConstant(IConstantClip constant, int preConstantCurves, float lastFrame)
    {
        int dataCount = constant.Data.Count;
        if (dataCount == 0) return;

        float[] rented = ArrayPool<float>.Shared.Rent(dataCount);
        try
        {
            constant.Data.CopyTo(rented);
            ReadOnlySpan<float> curveValues = new(rented, 0, dataCount);
            ReadOnlySpan<float> slopeValues = stackalloc float[4];

            float time = 0f;
            int is1or2Frames = time == lastFrame ? 1 : 2;
            for (int i = 0; i < is1or2Frames; i++, time += lastFrame)
            {
                for (int curveIndex = 0; curveIndex < dataCount;)
                {
                    int globalIndex = preConstantCurves + curveIndex;
                    IGenericBinding binding = GetBinding(globalIndex);
                    string path = GetCurvePath(binding.Path);
                    if (binding.IsTransform())
                    {
                        AddTransformCurveFromBuffer(time, binding, curveValues, curveIndex, slopeValues, slopeValues, path);
                        curveIndex += binding.TransformType().GetDimension();
                    }
                    else if (binding.CustomType == (byte)BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, constant.Data[curveIndex]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(binding, path, time, constant.Data[curveIndex]);
                        curveIndex++;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    // === Curve emission helpers (port of AR's AnimationClipConverter) =======

    private void AddCustomCurve(IGenericBinding binding, string path, float time, float value, float inTangent = 0, float outTangent = 0)
    {
        switch ((BindingCustomType)binding.CustomType)
        {
            case BindingCustomType.AnimatorMuscle:
                AddAnimatorMuscleCurve(binding, time, value, inTangent, outTangent);
                break;

            default:
                string attribute = m_customCurveResolver.ToAttributeName((BindingCustomType)binding.CustomType, binding.Attribute, path);
                CurveData curve = new(path, attribute, binding.GetClassID(), binding.Script.TryGetAsset(m_clip.Collection));
                if (binding.IsPPtrCurve())
                    AddPPtrKeyframe(curve, time, (int)value);
                else
                    AddFloatKeyframe(curve, time, value, inTangent, outTangent);
                break;
        }
    }

    private void AddTransformCurveFromBuffer(float time, IGenericBinding binding,
        ReadOnlySpan<float> curveValues, int offset,
        ReadOnlySpan<float> inSlopeValues, ReadOnlySpan<float> outSlopeValues, string path)
    {
        switch (binding.TransformType())
        {
            case TransformType.Translation:
                {
                    if (!m_translations.TryGetValue(path, out IVector3Curve? curve))
                    {
                        curve = m_clip.PositionCurves_C74.AddNew();
                        curve.SetValues(path);
                        m_translations.Add(path, curve);
                    }
                    IKeyframe_Vector3f key = curve.Curve.Curve.AddNew();
                    key.Value.SetValues(curveValues[offset + 0], curveValues[offset + 1], curveValues[offset + 2]);
                    key.InSlope.SetValues(inSlopeValues[0], inSlopeValues[1], inSlopeValues[2]);
                    key.OutSlope.SetValues(outSlopeValues[0], outSlopeValues[1], outSlopeValues[2]);
                    key.Time = time;
                    key.TangentMode = TangentMode.FreeFree.ToTangent(Version);
                    key.WeightedMode = (int)WeightedMode.None;
                    key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                    key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                }
                break;

            case TransformType.Rotation:
                {
                    if (!m_rotations.TryGetValue(path, out IQuaternionCurve? curve))
                    {
                        curve = m_clip.RotationCurves_C74.AddNew();
                        curve.SetValues(path);
                        m_rotations.Add(path, curve);
                    }
                    IKeyframe_Quaternionf key = curve.Curve.Curve.AddNew();
                    key.Value.SetValues(curveValues[offset + 0], curveValues[offset + 1], curveValues[offset + 2], curveValues[offset + 3]);
                    key.InSlope.SetValues(inSlopeValues[0], inSlopeValues[1], inSlopeValues[2], inSlopeValues[3]);
                    key.OutSlope.SetValues(outSlopeValues[0], outSlopeValues[1], outSlopeValues[2], outSlopeValues[3]);
                    key.Time = time;
                    key.TangentMode = TangentMode.FreeFree.ToTangent(Version);
                    key.WeightedMode = (int)WeightedMode.None;
                    key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                    key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                }
                break;

            case TransformType.Scaling:
                {
                    if (!m_scales.TryGetValue(path, out IVector3Curve? curve))
                    {
                        curve = m_clip.ScaleCurves_C74.AddNew();
                        curve.SetValues(path);
                        m_scales.Add(path, curve);
                    }
                    IKeyframe_Vector3f key = curve.Curve.Curve.AddNew();
                    key.Value.SetValues(curveValues[offset + 0], curveValues[offset + 1], curveValues[offset + 2]);
                    key.InSlope.SetValues(inSlopeValues[0], inSlopeValues[1], inSlopeValues[2]);
                    key.OutSlope.SetValues(outSlopeValues[0], outSlopeValues[1], outSlopeValues[2]);
                    key.Time = time;
                    key.TangentMode = TangentMode.FreeFree.ToTangent(Version);
                    key.WeightedMode = (int)WeightedMode.None;
                    key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                    key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                }
                break;

            case TransformType.EulerRotation:
                {
                    if (!m_eulers.TryGetValue(path, out IVector3Curve? curve))
                    {
                        if (!m_clip.Has_EulerCurves_C74()) break;
                        curve = m_clip.EulerCurves_C74.AddNew();
                        curve.SetValues(path, (RotationOrder)binding.CustomType);
                        m_eulers.Add(path, curve);
                    }
                    IKeyframe_Vector3f key = curve.Curve.Curve.AddNew();
                    key.Value.SetValues(curveValues[offset + 0], curveValues[offset + 1], curveValues[offset + 2]);
                    key.InSlope.SetValues(inSlopeValues[0], inSlopeValues[1], inSlopeValues[2]);
                    key.OutSlope.SetValues(outSlopeValues[0], outSlopeValues[1], outSlopeValues[2]);
                    key.Time = time;
                    key.TangentMode = TangentMode.FreeFree.ToTangent(Version);
                    key.WeightedMode = (int)WeightedMode.None;
                    key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                    key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
                }
                break;

            default:
                throw new NotImplementedException(binding.TransformType().ToString());
        }
    }

    private void AddDefaultCurve(IGenericBinding binding, string path, float time, float value, float inTangent = 0, float outTangent = 0)
    {
        switch (binding.GetClassID())
        {
            case ClassIDType.GameObject:
                AddGameObjectCurve(binding, path, time, value, inTangent, outTangent);
                break;

            case ClassIDType.MonoBehaviour:
                AddScriptCurve(binding, path, time, value, inTangent, outTangent);
                break;

            default:
                AddEngineCurve(binding, path, time, value, inTangent, outTangent);
                break;
        }
    }

    private void AddGameObjectCurve(IGenericBinding binding, string path, float time, float value, float inTangent, float outTangent)
    {
        if (GameObject.TryGetPath(binding.Attribute, out string? propertyName))
        {
            CurveData curve = new(path, propertyName, ClassIDType.GameObject);
            AddFloatKeyframe(curve, time, value, inTangent, outTangent);
        }
        else
        {
            CurveData curve = new(path, GetReversedPath(MissedPropertyPrefix, binding.Attribute), ClassIDType.GameObject);
            AddFloatKeyframe(curve, time, value, inTangent, outTangent);
        }
    }

    private void AddScriptCurve(IGenericBinding binding, string path, float time, float value, float inTangent, float outTangent)
    {
        if (binding.Script.TryGetAsset(m_clip.Collection) is IMonoScript script)
        {
            m_checksumCache.Add(script);
        }

        if (!m_checksumCache.TryGetPath(binding.Attribute, out string? propertyName))
        {
            propertyName = GetReversedPath(ScriptPropertyPrefix, binding.Attribute);
        }

        CurveData curve = new(path, propertyName, ClassIDType.MonoBehaviour, binding.Script.TryGetAsset(m_clip.Collection));

        if (binding.IsPPtrCurve())
            AddPPtrKeyframe(curve, time, (int)value);
        else
            AddFloatKeyframe(curve, time, value, inTangent, outTangent);
    }

    private void AddEngineCurve(IGenericBinding binding, string path, float time, float value, float inTangent, float outTangent)
    {
        if (!FieldHashes.TryGetPath(binding.GetClassID(), binding.Attribute, out string? propertyName))
        {
            propertyName = GetReversedPath(TypeTreePropertyPrefix, binding.Attribute);
        }

        CurveData curve = new(path, propertyName, binding.GetClassID());

        if (binding.IsPPtrCurve())
            AddPPtrKeyframe(curve, time, (int)value);
        else
            AddFloatKeyframe(curve, time, value, inTangent, outTangent);
    }

    private void AddAnimatorMuscleCurve(IGenericBinding binding, float time, float value, float inTangent, float outTangent)
    {
        string attributeString = HumanoidMuscleTypeExtensions.ToAttributeString(binding.GetHumanoidMuscle(Version));
        CurveData curve = new(string.Empty, attributeString, ClassIDType.Animator);
        AddFloatKeyframe(curve, time, value, inTangent, outTangent);
    }

    private void AddFloatKeyframe(in CurveData curveData, float time, float value, float inTangent, float outTangent)
    {
        if (!m_floats.TryGetValue(curveData, out IFloatCurve? curve))
        {
            curve = m_clip.FloatCurves_C74.AddNew();
            curve.Path = curveData.Path;
            curve.Attribute = curveData.Attribute;
            curve.ClassID = (int)curveData.ClassID;
            curve.Script.SetAsset(m_clip.Collection, curveData.Script as IMonoScript);
            curve.Curve.SetDefaultRotationOrderAndCurveLoopType();
            m_floats.Add(curveData, curve);
        }

        IKeyframe_Single floatKey = curve.Curve.Curve.AddNew();
        floatKey.SetValues(Version, time, value, inTangent, outTangent, DefaultFloatWeight);
    }

    private void AddPPtrKeyframe(in CurveData curveData, float time, int index)
    {
        if (!m_pptrs.TryGetValue(curveData, out IPPtrCurve? curve))
        {
            if (!m_clip.Has_PPtrCurves_C74()) return;
            curve = m_clip.PPtrCurves_C74.AddNew();
            curve.Path = curveData.Path;
            curve.Attribute = curveData.Attribute;
            curve.ClassID = (int)curveData.ClassID;
            curve.Script.SetAsset(m_clip.Collection, curveData.Script as IMonoScript);
            curve.Flags = (int)SourceGenerated.NativeEnums.Global.EditorCurveBindingFlags.PPtr;
            m_pptrs.Add(curveData, curve);
        }

        IPPtr_Object? value = (index < 0 || index >= ClipBindingConstant.PptrCurveMapping.Count)
            ? null
            : ClipBindingConstant.PptrCurveMapping[index];
        IPPtrKeyframe key = curve.Curve.AddNew();
        key.Time = time;
        key.Value.CopyValues(value, new PPtrConverter(m_clip));
    }

    // === Binding lookup =====================================================
    private IGenericBinding GetBinding(int index)
    {
        if (m_bindingsCache.TryGetValue(index, out IGenericBinding? cached))
            return cached;

        int curves = 0;
        AccessListBase<IGenericBinding> bindings = ClipBindingConstant.GenericBindings;
        for (int i = 0; i < bindings.Count; i++)
        {
            IGenericBinding binding = bindings[i];
            if (binding.GetClassID() == ClassIDType.Transform)
                curves += binding.TransformType().GetDimension();
            else
                curves += 1;
            if (curves > index)
            {
                m_bindingsCache[index] = binding;
                if (binding.IsTransform() && binding.TransformType().GetDimension() < 1)
                    throw new IndexOutOfRangeException("Transform AnimationCurve can't have Dimension less than 1.");
                return binding;
            }
        }
        throw new ArgumentException($"Binding with index {index} hasn't been found", nameof(index));
    }

    private static bool TryGetNextFrame(IReadOnlyList<StreamedFrame> streamedFrames, int currentFrame, int curveID,
        out StreamedFrame? nextFrame, out int curveIdx)
    {
        for (int frameIndex = currentFrame + 1; frameIndex < streamedFrames.Count; frameIndex++)
        {
            nextFrame = streamedFrames[frameIndex];
            for (curveIdx = 0; curveIdx < nextFrame.Curves.Length; curveIdx++)
            {
                if (nextFrame.Curves[curveIdx].Index == curveID)
                    return true;
            }
        }
        nextFrame = null;
        curveIdx = -1;
        return false;
    }

    private string GetCurvePath(uint hash)
    {
        if (m_checksumCache.TryGetPath(hash, out string? path))
            return path;
        return GetReversedPath(UnknownPathPrefix, hash);
    }

    private static string GetReversedPath(string prefix, uint hash)
    {
        return Crc32Algorithm.ReverseAscii(hash, $"{prefix}0x{hash:X}_");
    }

    public IReadOnlyList<StreamedFrame> GenerateFramesFromStreamedClip(IStreamedClip clip)
    {
        List<StreamedFrame> frames = new();
        if (clip.Data.Count == 0) return frames;

        Span<byte> buffer = new byte[clip.Data.Count * sizeof(uint)];
        AssetCollection collection = m_clip.Collection;
        CopyDataToBuffer(clip, collection, buffer);

        EndianSpanReader reader = new(buffer, collection.EndianType);
        while (reader.Position < reader.Length)
        {
            StreamedFrame frame = new();
            frame.Read(ref reader, collection.Version);
            frames.Add(frame);
        }
        return frames;

        static bool CpuEndiannessMatchesCollection(AssetCollection collection)
        {
            return (BitConverter.IsLittleEndian && collection.EndianType is EndianType.LittleEndian)
                || (!BitConverter.IsLittleEndian && collection.EndianType is EndianType.BigEndian);
        }

        static void CopyDataToBuffer(IStreamedClip clip, AssetCollection collection, Span<byte> buffer)
        {
            if (CpuEndiannessMatchesCollection(collection))
            {
                Span<uint> span = MemoryMarshal.Cast<byte, uint>(buffer);
                clip.Data.CopyTo(span);
            }
            else
            {
                for (int i = 0; i < clip.Data.Count; i++)
                {
                    if (BitConverter.IsLittleEndian)
                        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i * sizeof(uint)), clip.Data[i]);
                    else
                        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(i * sizeof(uint)), clip.Data[i]);
                }
            }
        }
    }

    private float CalculateLastConstantTime(IReadOnlyList<StreamedFrame> streamedFrames, float stopTime)
    {
        if (stopTime == 0f || streamedFrames.Count <= 1) return stopTime;

        float sampleRate = m_clip.SampleRate_C74;
        int lastFrame = (int)float.Round(stopTime * sampleRate);
        StreamedFrame lastStreamedFrame = streamedFrames[streamedFrames.Count - 2];
        int lastSFFrame = lastStreamedFrame.Time > 0 ? (int)float.Round(lastStreamedFrame.Time * sampleRate) : 0;
        if (lastFrame - lastSFFrame == 1)
        {
            foreach (StreamedCurveKey curve in lastStreamedFrame.Curves)
            {
                IGenericBinding binding = GetBinding(curve.Index);
                if (binding.IsPPtrCurve())
                    return lastStreamedFrame.Time > 0 ? lastStreamedFrame.Time : 0f;
            }
        }
        return stopTime;
    }

    private readonly record struct CurveData(string Path, string Attribute, ClassIDType ClassID, IUnityObjectBase? Script = null);
}
