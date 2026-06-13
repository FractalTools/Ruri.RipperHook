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
using Ruri.FModelHook.UnityExport.Engine;
// Namespace import brings the ToTangent(...) extension into scope; the alias
// disambiguates the enum type from its same-named namespace.
using AssetRipper.SourceGenerated.Extensions.Enums.Keyframe.TangentMode;
using TangentModeKeyframe = AssetRipper.SourceGenerated.Extensions.Enums.Keyframe.TangentMode.TangentMode;

namespace Ruri.FModelHook.UnityExport.Mappings;

// UAnimSequence -> AnimationClip (legacy generic clip). CUE4Parse decodes the
// ACL-compressed tracks for us (UAnimSequence.ConvertAnims), giving per-bone
// rotation/position/scale keyframes; we transcribe each into a legacy transform
// curve keyed by the bone's hierarchy path. Marked Legacy so it drives a
// transform hierarchy directly with no Avatar/muscle clip.
//
// Values are kept in raw Unreal axes, consistent with the mesh export; a uniform
// UE->Unity basis change is a clean follow-up.
public static class AnimationMappings
{
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

        if (clip.Has_Legacy_C74())
            clip.Legacy_C74 = true;
        clip.SampleRate_C74 = fps;

        string[] bonePaths = BuildBonePaths(animSet.Skeleton);
        int trackCount = Math.Min(sequence.Tracks.Count, bonePaths.Length);

        for (int bone = 0; bone < trackCount; bone++)
        {
            CAnimTrack track = sequence.Tracks[bone];
            if (track is null || !track.HasKeys())
                continue;

            string path = bonePaths[bone];
            AddPositionCurve(clip, path, track, clipLength);
            AddRotationCurve(clip, path, track, clipLength);
            AddScaleCurve(clip, path, track, clipLength);
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

    private static void AddPositionCurve(IAnimationClip clip, string path, CAnimTrack track, float clipLength)
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
            key.Time = TimeForKey(track.KeyPosTime, track.KeyTime, k, keys.Length, clipLength);
        }
    }

    private static void AddScaleCurve(IAnimationClip clip, string path, CAnimTrack track, float clipLength)
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
            key.Time = TimeForKey(track.KeyScaleTime, track.KeyTime, k, keys.Length, clipLength);
        }
    }

    private static void AddRotationCurve(IAnimationClip clip, string path, CAnimTrack track, float clipLength)
    {
        FQuat[] keys = track.KeyQuat;
        if (keys.Length == 0)
            return;

        IQuaternionCurve curve = clip.RotationCurves_C74.AddNew();
        curve.SetValues(path);
        for (int k = 0; k < keys.Length; k++)
        {
            IKeyframe_Quaternionf key = curve.Curve.Curve.AddNew();
            key.Value.SetValues(keys[k].X, keys[k].Y, keys[k].Z, keys[k].W);
            key.InSlope.SetValues(0f, 0f, 0f, 0f);
            key.OutSlope.SetValues(0f, 0f, 0f, 0f);
            key.TangentMode = TangentModeKeyframe.FreeSmooth.ToTangent(clip.Collection.Version);
            key.WeightedMode = (int)WeightedMode.None;
            key.Time = TimeForKey(track.KeyQuatTime, track.KeyTime, k, keys.Length, clipLength);
        }
    }

    private static void FillVector3Tangents(IKeyframe_Vector3f key, AssetRipper.Primitives.UnityVersion version)
    {
        key.InSlope.SetValues(0f, 0f, 0f);
        key.OutSlope.SetValues(0f, 0f, 0f);
        key.TangentMode = TangentModeKeyframe.FreeSmooth.ToTangent(version);
        key.WeightedMode = (int)WeightedMode.None;
    }

    // Prefer an explicit per-channel time array, then the shared time array, then
    // an even spread across the clip duration. CUE4Parse stores key times in frame
    // units; the even-spread fallback yields the same uniform sampling its own
    // reader assumes when no time array is present.
    private static float TimeForKey(float[] channelTime, float[] sharedTime, int keyIndex, int keyCount, float clipLength)
    {
        if (channelTime.Length > keyIndex)
            return channelTime[keyIndex];
        if (sharedTime.Length > keyIndex)
            return sharedTime[keyIndex];
        if (keyCount <= 1)
            return 0f;
        return keyIndex / (float)(keyCount - 1) * clipLength;
    }
}
