using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_111;
using AssetRipper.SourceGenerated.Classes.ClassID_221;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.AnimationClipOverride;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShapeChannel;
using Ruri.RipperHook.Bridge;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Tables;
using System.Globalization;

namespace Ruri.RipperHook.Data;

/// <summary>What a Unity BUILD states about a selection, for every title built on that engine.
///
/// A title's own hook publishes what that title's designers wrote down -- a roster table, an
/// outfit catalog, a named expression library. These two are the floor underneath all of it:
/// the engine stores which clips an animator plays and what a mesh's blend shapes are called,
/// in every build, so no title has to be taught to answer them and a title that never wrote a
/// catalog still answers.
///
/// Both are asked of a SELECTION -- the archives one row is made of -- and both read the same
/// dependency closure an import of that row would (<see cref="ClosureReader"/>).</summary>
public static class UnityDatasets
{
    public const string IdPrefix = "unity.";
    public const string AnimationsId = "unity.animations";
    public const string BlendShapesId = "unity.blendshapes";

    private const string Cab = "cab";

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;

        Datasets.Publish(AnimationsId, DataRole.AnimationCatalog, [DataParam.List(Cab)],
            "Every animation the given archives reach, filed under what PLAYS it: an animator "
            + "controller states its clips, an override controller restates the ones it replaces, "
            + "a legacy Animation component lists its own. A clip nothing names is listed under "
            + "no player. Each row carries the asset key that loads that one clip.",
            Animations);

        Datasets.Publish(BlendShapesId, DataRole.ExpressionCatalog, [DataParam.List(Cab)],
            "Every named blend shape the given archives reach, as the MESH itself states it -- "
            + "the expression vocabulary a model was built with, which every Unity build stores "
            + "beside the deltas. Each row carries the mesh and the shape's index in it.",
            BlendShapes);
    }

    /// <summary>Every animation the selection reaches, filed under what plays it.
    ///
    /// Not "every clip in these archives": a character's closure co-hosts whole libraries
    /// belonging to somebody else, and the game's own filing of a clip is which animator names
    /// it. So the players are walked first and each clip is filed under the first one that names
    /// it; what is left over is still listed, because a title that plays clips by name from
    /// script names none of them in data.</summary>
    private static ColumnTable Animations(DataRequest request)
    {
        TableBuilder table = new(AnimationsId,
            "name|Clip", "player|Played By", "length|Length", "frames#|Frames",
            "cab|Cab", "key|Id");
        table.Role(ColumnRole.Label, "name")
            .Role(ColumnRole.Facet | ColumnRole.Group, "player")
            .Role(ColumnRole.Detail, "length")
            .Role(ColumnRole.Key | ColumnRole.Payload, "key");

        GameData? loaded = ClosureReader.Read(request.Map, request.List(Cab));
        if (loaded is null)
        {
            return table.Build();
        }
        HashSet<string> reached = Reached(request);
        Dictionary<AssetCollection, string> identities = ClosureGraphBlob.CollectionIdentities(loaded);
        Dictionary<IAnimationClip, string> playedBy = new(ReferenceEqualityComparer.Instance);
        List<IAnimationClip> clips = [];

        // The first player to name a clip is the one it is filed under; a clip met on its own
        // before anything names it is refiled the moment something does.
        void File(IAnimationClip? clip, string player)
        {
            if (clip is null)
            {
                return;
            }
            if (playedBy.TryGetValue(clip, out string? already))
            {
                if (already.Length == 0)
                {
                    playedBy[clip] = player;
                }
                return;
            }
            playedBy[clip] = player;
            clips.Add(clip);
        }

        foreach (IUnityObjectBase asset in loaded.GameBundle.FetchAssets())
        {
            request.Cancellation.ThrowIfCancellationRequested();
            if (!Reaches(reached, asset))
            {
                continue;
            }
            switch (asset)
            {
                case IAnimatorController controller:
                    foreach (IAnimationClip? clip in controller.AnimationClipsP)
                    {
                        File(clip, controller.GetBestName());
                    }
                    break;
                case IAnimatorOverrideController overrides:
                    foreach (IAnimationClipOverride pair in overrides.Clips)
                    {
                        File(pair.OriginalClip.TryGetAsset(overrides.Collection), overrides.GetBestName());
                        File(pair.OverrideClip.TryGetAsset(overrides.Collection), overrides.GetBestName());
                    }
                    break;
                case IAnimation animation:
                    foreach (IAnimationClip? clip in animation.AnimationsP)
                    {
                        File(clip, animation.GetBestName());
                    }
                    break;
                case IAnimationClip loose:
                    File(loose, string.Empty);
                    break;
            }
        }

        foreach (IAnimationClip clip in clips)
        {
            (string length, double frames) = Duration(clip);
            table.Row(clip.GetBestName(), playedBy.GetValueOrDefault(clip, string.Empty), length, frames,
                clip.Collection.Name, Key(identities, clip));
        }
        return table.Build();
    }

    /// <summary>How long a clip runs, as the clip itself states it. A build that strips the
    /// muscle clip leaves nothing to say, and saying nothing is the honest answer.</summary>
    private static (string Length, double Frames) Duration(IAnimationClip clip)
    {
        if (!clip.Has_MuscleClip_C74())
        {
            return (string.Empty, 0d);
        }
        float seconds = clip.MuscleClip_C74.StopTime - clip.MuscleClip_C74.StartTime;
        if (seconds <= 0f)
        {
            return (string.Empty, 0d);
        }
        double frames = Math.Round(seconds * clip.SampleRate_C74);
        return (seconds.ToString("0.##", CultureInfo.InvariantCulture) + " s", frames);
    }

    /// <summary>Every named blend shape the selection reaches.</summary>
    private static ColumnTable BlendShapes(DataRequest request)
    {
        TableBuilder table = new(BlendShapesId,
            "name|Expression", "mesh|Mesh", "frames#|Frames", "index#|Index", "cab|Cab", "key|Id");
        table.Role(ColumnRole.Label, "name")
            .Role(ColumnRole.Facet | ColumnRole.Group, "mesh")
            .Role(ColumnRole.Key | ColumnRole.Payload, "key");

        GameData? loaded = ClosureReader.Read(request.Map, request.List(Cab));
        if (loaded is null)
        {
            return table.Build();
        }
        HashSet<string> reached = Reached(request);
        Dictionary<AssetCollection, string> identities = ClosureGraphBlob.CollectionIdentities(loaded);
        foreach (IUnityObjectBase asset in loaded.GameBundle.FetchAssets())
        {
            request.Cancellation.ThrowIfCancellationRequested();
            // Only the channelled form carries names. The 4.1 shape list stores deltas under no
            // name at all, so a build that old ships no expression vocabulary to browse.
            if (asset is not IMesh mesh || !mesh.Has_Shapes() || !Reaches(reached, asset))
            {
                continue;
            }
            string name = mesh.GetBestName();
            string key = Key(identities, mesh);
            int index = 0;
            foreach (MeshBlendShapeChannel channel in mesh.Shapes.Channels)
            {
                table.Row(channel.Name.String, name, channel.FrameCount, index, mesh.Collection.Name,
                    key + "|" + index.ToString(CultureInfo.InvariantCulture));
                index++;
            }
        }
        return table.Build();
    }

    /// <summary>The archives the selection actually REACHES, as the map states it.
    ///
    /// Loading is gated by FILE, and a build that pools dozens of unrelated archives into one
    /// file therefore loads strangers alongside what was asked for -- a character's closure
    /// co-hosting another character's face. The map knows which archives the seeds reach, so
    /// that is the answer, and an asset from a co-tenant is left out rather than reported as
    /// this row's.</summary>
    private static HashSet<string> Reached(DataRequest request) =>
        new(CabMap.ResolveClosureCabNames(request.Map, request.List(Cab)),
            StringComparer.OrdinalIgnoreCase);

    private static bool Reaches(HashSet<string> reached, IUnityObjectBase asset) =>
        reached.Count == 0 || reached.Contains(asset.Collection.Name);

    /// <summary>One asset's identity inside a closure, in the SAME words the export side keys by
    /// -- so a row read here names the one asset a later load asks for.</summary>
    private static string Key(Dictionary<AssetCollection, string> identities, IUnityObjectBase asset) =>
        string.Create(CultureInfo.InvariantCulture, $"{identities[asset.Collection]}|{asset.PathID}");
}
