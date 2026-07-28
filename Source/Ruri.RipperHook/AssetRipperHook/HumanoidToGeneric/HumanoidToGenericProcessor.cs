using AssetRipper.Assets;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Extensions;
using Ruri.RipperHook.Humanoid;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Resolves every humanoid AnimationClip in the loaded scope into an ordinary generic clip, once,
/// at asset-processing time -- so that nothing downstream has to know Unity's Mecanim muscle
/// encoding exists.
///
/// A humanoid clip stores the body as ~95 normalised muscle floats plus a root reference instead of
/// per-bone transform curves. Anything that cannot solve that sees a motionless body, which is why
/// each consumer previously grew its own solver: one in the glTF exporter, a second, separately
/// maintained one in Python inside the Blender importer, each obliged to stay bit-identical to
/// Unity's native implementation and to each other. Doing it here instead means the .anim YAML the
/// project exporter writes, the glTF exporter and the in-process Blender bridge all read the same
/// already-generic curves, and the muscle solver exists exactly once.
///
/// Ordering: this must run AFTER <c>EditorFormatProcessor</c>, which is what converts a clip's
/// compressed release data into the editor-format curve lists this reads and writes (its
/// <c>AnimationClipConverter.Process</c> call). The hook registers into
/// <c>ExportHandlerHook.CustomAssetProcessors</c>, whose insertion point already sits there.
/// </summary>
public sealed class HumanoidToGenericProcessor : IAssetProcessor
{
    public void Process(GameData gameData)
    {
        // A clip carries no avatar reference of its own: the muscle values are meaningless without
        // the referential that says which bone each one drives and with what limits. The Animator
        // component is what pairs the two, so that is the join -- never a name or path guess.
        List<(IAnimator Animator, IAvatar Avatar)> rigs = new();
        List<IAnimationClip> clips = new();
        foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
        {
            switch (asset)
            {
                case IAnimator animator when animator.AvatarP is { } avatar:
                    rigs.Add((animator, avatar));
                    break;
                case IAnimationClip clip:
                    clips.Add(clip);
                    break;
            }
        }

        if (rigs.Count == 0 || clips.Count == 0)
        {
            return;
        }

        Dictionary<IAvatar, AvatarMuscleReferential?> referentialByAvatar = new();
        int converted = 0;
        int curves = 0;

        foreach (IAnimationClip clip in clips)
        {
            // Pair the clip with the rig that plays it. A clip no Animator references still gets
            // solved: EndField attaches battle controllers from game code, so those clips arrive
            // unreferenced, and their muscle names are avatar-relative but plaintext and
            // path-hash independent, so any humanoid referential in scope decodes them identically.
            AvatarMuscleReferential? referential = null;
            foreach ((IAnimator animator, IAvatar avatar) in rigs)
            {
                if (animator.ContainsAnimationClip(clip))
                {
                    referential = GetReferential(referentialByAvatar, avatar);
                    break;
                }
            }
            referential ??= FirstHumanoid(referentialByAvatar, rigs);
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

    /// <summary>
    /// Build (and remember) one avatar's muscle referential. A null result is cached too: a generic
    /// rig, or a stripped runtime stub avatar with no usable human skeleton, must not be retried
    /// once per clip.
    /// </summary>
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

    /// <summary>
    /// The first avatar in scope that actually produces a referential. Content-driven on purpose:
    /// a character's neighbourhood routinely carries stub avatars (empty TOS, no muscle setup)
    /// alongside the real one, and only trying to build tells them apart -- a size or name
    /// heuristic would be exactly the kind of guessing this pipeline avoids everywhere else.
    /// </summary>
    private static AvatarMuscleReferential? FirstHumanoid(
        Dictionary<IAvatar, AvatarMuscleReferential?> cache, List<(IAnimator Animator, IAvatar Avatar)> rigs)
    {
        foreach ((_, IAvatar avatar) in rigs)
        {
            if (GetReferential(cache, avatar) is { } referential)
            {
                return referential;
            }
        }
        return null;
    }
}
