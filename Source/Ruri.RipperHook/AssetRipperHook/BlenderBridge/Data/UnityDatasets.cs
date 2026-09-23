using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShapeChannel;
using Ruri.RipperHook.BlenderBridge;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Statements;
using Ruri.RipperHook.BlenderBridge.Tables;
using System.Globalization;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>What a Unity BUILD states about a selection, for every title built on that engine.
///
/// A title's own hook publishes what that title's designers wrote down -- a roster table, an
/// outfit catalog, a named expression library. This is the floor underneath all of it: every
/// build stores what a mesh's blend shapes are called, beside the deltas, so no title has to
/// be taught to answer it and a title that never wrote a catalog still answers.
///
/// It is asked of a SELECTION -- the archives one row is made of -- and reads the same
/// dependency closure an import of that row would (<see cref="ClosureReader"/>).
///
/// There is deliberately no such reader for ANIMATIONS. Which clips a row plays would mean
/// loading the closure of every archive those clips live in -- hundreds, for one character --
/// to produce names the cabmap already carries, so that question is answered by a search over
/// the map instead (the bundle browser's own).</summary>
public static class UnityDatasets
{
    public const string IdPrefix = "unity.";
    public const string BlendShapesId = "unity.blendshapes";

    private const string Seed = "seed";

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;

        // The seeds are REQUIRED: this answers about a model somebody picked, and with none
        // picked it has nothing to say -- which is a list a surface offers rather than opens.
        Datasets.Publish(BlendShapesId, DataRole.ExpressionCatalog, [DataParam.List(Seed, required: true)],
            "Every named blend shape the given seeds reach, as the MESH itself states it -- the "
            + "expression vocabulary a model was built with, which every Unity build stores beside "
            + "the deltas. A seed is read as a load reads it. Each row carries the mesh and the "
            + "shape's index in it.",
            BlendShapes);
    }

    /// <summary>Every named blend shape the selection reaches.</summary>
    private static ColumnTable BlendShapes(DataRequest request)
    {
        TableBuilder table = new(BlendShapesId,
            "name|Expression", "mesh|Mesh", "frames#|Frames", "index#|Index", "cab|Cab", "key|Id");
        table.Role(ColumnRole.Label, "name")
            .Role(ColumnRole.Facet | ColumnRole.Group, "mesh")
            .Role(ColumnRole.Key | ColumnRole.Payload, "key");

        string[] archives = StatementSources.Archives(request.List(Seed), request.Map);
        GameData? loaded = ClosureReader.Read(request.Map, archives);
        if (loaded is null)
        {
            return table.Build();
        }
        HashSet<string> reached = new(CabMap.ResolveClosureCabNames(request.Map, archives), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Whether an asset sits in an archive the selection actually REACHES, as the map
    /// states it. Loading is gated by FILE, and a build that pools dozens of unrelated archives
    /// into one file loads strangers alongside what was asked for -- a character's closure
    /// co-hosting another character's face -- so an asset from a co-tenant is left out rather
    /// than reported as this row's.</summary>
    private static bool Reaches(HashSet<string> reached, IUnityObjectBase asset) =>
        reached.Count == 0 || reached.Contains(asset.Collection.Name);

    /// <summary>One asset's identity inside a closure, in the SAME words the export side keys by
    /// -- so a row read here names the one asset a later load asks for.</summary>
    private static string Key(Dictionary<AssetCollection, string> identities, IUnityObjectBase asset) =>
        string.Create(CultureInfo.InvariantCulture, $"{identities[asset.Collection]}|{asset.PathID}");
}
