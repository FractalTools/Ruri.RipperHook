using AssetRipper.Assets.Generics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Quaternionf;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Vector3f;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using Ruri.FModelHook.Game.SBUE.UnityExport.Engine;
// Namespace import brings the ToTangent(...) extension into scope; the alias
// disambiguates the enum type from its same-named namespace.
using AssetRipper.SourceGenerated.Extensions.Enums.Keyframe.TangentMode;
using TangentModeKeyframe = AssetRipper.SourceGenerated.Extensions.Enums.Keyframe.TangentMode.TangentMode;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Mappings;

// UAnimSequence -> AnimationClip (legacy generic clip). CUE4Parse decodes the
// ACL-compressed tracks for us (UAnimSequence.ConvertAnims), giving per-bone
// rotation/position/scale keyframes; we transcribe each into a legacy transform
// curve keyed by the bone's hierarchy path. Marked Legacy so it drives a
// transform hierarchy directly with no Avatar/muscle clip.
//
// Values are kept in raw Unreal axes, consistent with the mesh export; a uniform
// UE->Unity basis change is a clean follow-up.
//
// Unit conventions worth re-stating, because AR's clip post-processor runs even
// when ClipBindingConstant is empty and Unity's animation runtime is strict
// about them:
//   * Unity AnimationClip keyframe Time is in SECONDS.
//   * CUE4Parse's CAnimTrack KeyTime / KeyPosTime / KeyQuatTime / KeyScaleTime
//     arrays store FRAME INDICES (see AnimConverter.ReadTimeArray + the
//     CAnimTrack.GetBoneTransform / UEAnim sampling that treats `frame` as an
//     int in [0, NumFrames-1]). Every frame index has to be divided by
//     FramesPerSecond before it lands in a Keyframe.Time.
//   * CAnimSequence.AnimEndTime is already seconds (mirrors
//     UAnimSequence.SequenceLength).
public static class AnimationMappings
{
    // Matches AnimationClipConverter.DefaultFloatWeight (1/3). Unity stores the
    // default Vector3/Quaternion weighted-tangent weight as (1/3, 1/3, 1/3[, 1/3])
    // — leaving these at the field default of 0 produces degenerate weighted
    // tangents the moment WeightedMode is anything other than None.
    private const float DefaultFloatWeight = 1.0f / 3.0f;

    public static void Register()
    {
        MapperRegistry.Map<UAnimSequence, IAnimationClip>(collection => collection.CreateAnimationClip())
            .Set(t => t.Name, s => new Utf8String(s.Name))
            .After(Build);
    }

    private static void Build(UAnimSequence source, IAnimationClip clip, ConversionContext context)
    {
        CAnimSet animSet = source.ConvertAnims();
        if (animSet.Sequences.Count == 0)
            return;

        CAnimSequence sequence = animSet.Sequences[0];
        float fps = sequence.FramesPerSecond > 0f ? sequence.FramesPerSecond : 30f;
        float clipLength = sequence.AnimEndTime > 0f ? sequence.AnimEndTime : Math.Max(0f, (sequence.NumFrames - 1) / fps);

        // Legacy vs newer AnimationType — older layouts expose the bool field, newer
        // ones drop it in favour of an int AnimationType enum (see
        // AnimationClipExtensions.GetLegacy()). The NativeEnums-side AnimationType
        // is the one used by the Unity runtime; the same-named Enums-side type is
        // SpriteSheet-shaped and only has WholeSheet/SingleRow members. We mirror
        // both writes so the clip reads back as Legacy on every Unity version AR
        // supports.
        if (clip.Has_Legacy_C74())
            clip.Legacy_C74 = true;
        if (clip.Has_AnimationType_C74())
            clip.AnimationType_C74 = (int)AssetRipper.SourceGenerated.NativeEnums.Global.AnimationType.Legacy;
        // Compressed_C74 / Bounds_C74 / WrapMode_C74 / Events_C74 have no Has_*
        // guards — the fields are always present in C74's layout. Compressed must
        // be false: AR's pre-2018.3 cache reader hits the Has_Compressed branch
        // and tries to walk CompressedRotationCurves when this is true.
        clip.Compressed_C74 = false;
        if (clip.Has_UseHighQualityCurve_C74())
            clip.UseHighQualityCurve_C74 = false;
        clip.SampleRate_C74 = fps;
        // WrapMode_C74 defaults to 0 (= WrapMode.Default) which is what Unity
        // serializes when an .anim is created in the editor and never customised;
        // make the write explicit so the field never picks up stray bits from a
        // future codegen change. The enum lives in NativeEnums (Enums-side WrapMode
        // is the texture-side TextureWrapMode shape).
        clip.WrapMode_C74 = (int)AssetRipper.SourceGenerated.NativeEnums.Global.WrapMode.Default;

        // Make AR's clip post-processor's MuscleClipInfo.Initialize copy a
        // meaningful StopTime instead of the zero-default. ClipBindingConstant is
        // left empty, so ProcessStreams/ProcessDenses/ProcessConstant all walk an
        // empty binding list and no-op on our hand-built Position/Rotation/Scale
        // curves. See AssetRipper.Processing.AnimationClips.AnimationClipConverter.
        if (clip.Has_MuscleClip_C74())
        {
            clip.MuscleClip_C74.StartTime = 0f;
            clip.MuscleClip_C74.StopTime = clipLength;
        }

        string[] bonePaths = BuildBonePaths(animSet.Skeleton);
        int trackCount = Math.Min(sequence.Tracks.Count, bonePaths.Length);

        for (int bone = 0; bone < trackCount; bone++)
        {
            CAnimTrack track = sequence.Tracks[bone];
            if (track is null || !track.HasKeys())
                continue;

            string path = bonePaths[bone];
            AddPositionCurve(clip, path, track, fps, clipLength);
            AddRotationCurve(clip, path, track, fps, clipLength);
            AddScaleCurve(clip, path, track, fps, clipLength);
        }
    }

    // Bone hierarchy path per bone ("root/pelvis/spine_01"), matching how an
    // imported skeleton's transforms are addressed by an animation curve.
    private static string[] BuildBonePaths(USkeleton skeleton)
    {
        FMeshBoneInfo[] bones = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        string[] paths = new string[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            string name = bones[i].Name.Text;
            int parent = bones[i].ParentIndex;
            paths[i] = parent >= 0 && parent < i ? $"{paths[parent]}/{name}" : name;
        }
        return paths;
    }

    private static void AddPositionCurve(IAnimationClip clip, string path, CAnimTrack track, float fps, float clipLength)
    {
        FVector[] keys = track.KeyPos;
        if (keys.Length == 0)
            return;

        IVector3Curve curve = clip.PositionCurves_C74.AddNew();
        curve.SetValues(path);
        for (int k = 0; k < keys.Length; k++)
        {
            IKeyframe_Vector3f key = curve.Curve.Curve.AddNew();
            key.Value.SetValues(keys[k].X, keys[k].Y, keys[k].Z);
            FillVector3Tangents(key, clip.Collection.Version);
            key.Time = TimeForKey(track.KeyPosTime, track.KeyTime, k, keys.Length, fps, clipLength);
        }
    }

    private static void AddScaleCurve(IAnimationClip clip, string path, CAnimTrack track, float fps, float clipLength)
    {
        FVector[] keys = track.KeyScale;
        if (keys.Length == 0)
            return;

        IVector3Curve curve = clip.ScaleCurves_C74.AddNew();
        curve.SetValues(path);
        for (int k = 0; k < keys.Length; k++)
        {
            IKeyframe_Vector3f key = curve.Curve.Curve.AddNew();
            key.Value.SetValues(keys[k].X, keys[k].Y, keys[k].Z);
            FillVector3Tangents(key, clip.Collection.Version);
            key.Time = TimeForKey(track.KeyScaleTime, track.KeyTime, k, keys.Length, fps, clipLength);
        }
    }

    private static void AddRotationCurve(IAnimationClip clip, string path, CAnimTrack track, float fps, float clipLength)
    {
        FQuat[] keys = track.KeyQuat;
        if (keys.Length == 0)
            return;

        IQuaternionCurve curve = clip.RotationCurves_C74.AddNew();
        curve.SetValues(path);
        AssetRipper.Primitives.UnityVersion version = clip.Collection.Version;
        for (int k = 0; k < keys.Length; k++)
        {
            IKeyframe_Quaternionf key = curve.Curve.Curve.AddNew();
            key.Value.SetValues(keys[k].X, keys[k].Y, keys[k].Z, keys[k].W);
            key.InSlope.SetValues(0f, 0f, 0f, 0f);
            key.OutSlope.SetValues(0f, 0f, 0f, 0f);
            key.TangentMode = TangentModeKeyframe.FreeSmooth.ToTangent(version);
            key.WeightedMode = (int)WeightedMode.None;
            // Match AR's AnimationClipConverter.AddTransformCurve rotation path: the
            // weight subfields are null on Unity versions that have no weighted
            // tangent support, hence the null-conditional `?.SetValues`.
            key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
            key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
            key.Time = TimeForKey(track.KeyQuatTime, track.KeyTime, k, keys.Length, fps, clipLength);
        }
    }

    private static void FillVector3Tangents(IKeyframe_Vector3f key, AssetRipper.Primitives.UnityVersion version)
    {
        key.InSlope.SetValues(0f, 0f, 0f);
        key.OutSlope.SetValues(0f, 0f, 0f);
        key.TangentMode = TangentModeKeyframe.FreeSmooth.ToTangent(version);
        key.WeightedMode = (int)WeightedMode.None;
        // Mirror AR's translation/scale curve path (DefaultFloatWeight = 1/3); the
        // weight subfields are null on Unity versions where weighted tangents are
        // not a thing, which is why AR also dispatches through `?.SetValues`.
        key.InWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
        key.OutWeight?.SetValues(DefaultFloatWeight, DefaultFloatWeight, DefaultFloatWeight);
    }

    // Prefer an explicit per-channel time array, then the shared time array, then
    // an even spread across the clip duration. CUE4Parse stores key times as
    // FRAME INDICES (see CAnimConverter.ReadTimeArray + the integer `frame` math
    // in CAnimTrack.GetBoneTransform), so the channel/shared lookups must divide
    // by FramesPerSecond to land in Unity's seconds-typed Keyframe.Time. The
    // even-spread fallback already runs in seconds via clipLength.
    private static float TimeForKey(float[] channelTime, float[] sharedTime, int keyIndex, int keyCount, float fps, float clipLength)
    {
        if (channelTime.Length > keyIndex)
            return fps > 0f ? channelTime[keyIndex] / fps : channelTime[keyIndex];
        if (sharedTime.Length > keyIndex)
            return fps > 0f ? sharedTime[keyIndex] / fps : sharedTime[keyIndex];
        if (keyCount <= 1)
            return 0f;
        return keyIndex / (float)(keyCount - 1) * clipLength;
    }
}
