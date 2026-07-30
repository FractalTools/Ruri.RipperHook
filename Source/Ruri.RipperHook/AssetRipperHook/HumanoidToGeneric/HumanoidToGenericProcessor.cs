using AssetRipper.Assets;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Extensions;
using Ruri.RipperHook.Humanoid;

namespace Ruri.RipperHook.AR;

public sealed class HumanoidToGenericProcessor : IAssetProcessor
{
    public static bool SolveUnreferencedClipsWithAnyAvatarInScope { get; set; }

    public void Process(GameData gameData)
    {
        List<IAvatar> avatars = new();
        List<(IAnimator Animator, IAvatar Avatar)> rigs = new();
        List<IAnimationClip> clips = new();
        foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
        {
            switch (asset)
            {
                case IAvatar avatarAsset:
                    avatars.Add(avatarAsset);
                    break;
                case IAnimator animator when animator.AvatarP is { } avatar:
                    rigs.Add((animator, avatar));
                    break;
                case IAnimationClip clip:
                    clips.Add(clip);
                    break;
            }
        }

        if (avatars.Count == 0 || clips.Count == 0)
        {
            return;
        }

        Dictionary<IAvatar, AvatarMuscleReferential?> referentialByAvatar = new();
        int converted = 0;
        int curves = 0;

        foreach (IAnimationClip clip in clips)
        {
            AvatarMuscleReferential? referential = null;
            foreach ((IAnimator animator, IAvatar avatar) in rigs)
            {
                if (animator.ContainsAnimationClip(clip))
                {
                    referential = GetReferential(referentialByAvatar, avatar);
                    break;
                }
            }
            if (referential is null && SolveUnreferencedClipsWithAnyAvatarInScope)
            {
                referential = FirstHumanoid(referentialByAvatar, avatars);
            }
            if (referential is null)
            {
                continue;
            }

            int written;
            try
            {
                written = HumanoidClipGenericizer.Convert(clip, referential);
            }
            catch (Exception exception)
            {
                Logger.Warning(LogCategory.Processing,
                    $"[Humanoid] '{clip.GetBestName()}' could not be resolved to generic curves " +
                    $"({exception.GetType().Name}: {exception.Message}) -- left as muscle data.");
                continue;
            }
            if (written > 0)
            {
                converted++;
                curves += written;
            }
        }

        if (converted > 0)
        {
            Logger.Info(LogCategory.Processing,
                $"[Humanoid] resolved {converted} muscle clip(s) into {curves} generic curve(s)");
        }
    }

    private static AvatarMuscleReferential? GetReferential(
        Dictionary<IAvatar, AvatarMuscleReferential?> cache, IAvatar avatar)
    {
        if (cache.TryGetValue(avatar, out AvatarMuscleReferential? cached))
        {
            return cached;
        }
        AvatarMuscleReferential? referential = null;
        try
        {
            referential = AvatarMuscleReferential.TryCreate(avatar);
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Processing,
                $"[Humanoid] avatar '{avatar.GetBestName()}' yielded no muscle referential " +
                $"({exception.GetType().Name}: {exception.Message}).");
        }
        cache[avatar] = referential;
        return referential;
    }

    private static AvatarMuscleReferential? FirstHumanoid(
        Dictionary<IAvatar, AvatarMuscleReferential?> cache, List<IAvatar> avatars)
    {
        foreach (IAvatar avatar in avatars)
        {
            if (GetReferential(cache, avatar) is { } referential)
            {
                return referential;
            }
        }
        return null;
    }
}
