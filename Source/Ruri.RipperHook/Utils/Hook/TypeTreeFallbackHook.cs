using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.IO.Files.SerializedFiles;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;
using Ruri.RipperHook.Core.TypeTree;

namespace Ruri.RipperHook.HookUtils.TypeTreeFallbackHook;

/// <summary>
/// Serves AssetRipper's unknown-class fallback from the game's own type trees.
///
/// When <c>AssetFactory.CreateSerialized</c> has no generated class for a ClassID,
/// <c>GameAssetFactory</c> falls back to building a generic <c>TypeTreeObject</c> from a type tree
/// (<c>GameAssetFactory.cs</c>: <c>TryReadNormalObject</c> / <c>CreateAsset</c>). Stock AssetRipper
/// answers that from <c>SourceTpk</c> -- the type tree bytes its code generator embedded into
/// <c>AssetRipper.SourceGenerated</c> -- which knows only the Unity versions that generator was fed.
///
/// That source is wrong for us twice over. It has no idea about a fork's private classes, which is
/// exactly the case this fallback exists to handle; and it is frozen at whatever tpk container
/// format the published package was built with, so it cannot even be opened once the rest of the
/// stack moves forward. Ruri already carries the full picture -- every engine snapshot the game
/// builds on plus the game's own -- so the fallback reads from there instead and the stale bytes are
/// never touched.
///
/// The version is <see cref="TypeTreeDatabase.ActiveVersion"/>, not the <see cref="UnityVersion"/>
/// AssetRipper passes in: that parameter is the serialized file's engine version, which a fork keeps
/// pinned at whatever it branched from (EndField reports 2021.3.34f5 for every build), so it cannot
/// distinguish one game build from another. Resolving by the enabled hook's lineage version is what
/// every other read path already does.
/// </summary>
public class TypeTreeFallbackHook : CommonHook, IHookModule
{
    public void OnApply()
    {
        Registry.ApplyTypeHooks(GetType());
    }

    [RetargetMethod(typeof(TypeTreeNodeStruct), nameof(TryMakeFromTpk))]
    public static bool TryMakeFromTpk(ClassIDType classID, UnityVersion version, out TypeTreeNodeStruct releaseTree, out TypeTreeNodeStruct editorTree)
    {
        TypeTreeVersion activeVersion = TypeTreeDatabase.ActiveVersion;

        TypeTreeNode? release = TypeTreeDatabase.GetReleaseRoot(classID, activeVersion);
        if (release is null)
        {
            releaseTree = default;
            editorTree = default;
            return false;
        }

        releaseTree = Convert(release);

        // A build dump usually carries no editor layout. AssetRipper's own single-root
        // TypeTreeObject uses one structure for both walks, so reusing the release tree here is that
        // same shape rather than an invented one.
        TypeTreeNode? editor = TypeTreeDatabase.GetEditorRoot(classID, activeVersion);
        editorTree = editor is null ? releaseTree : Convert(editor);
        return true;
    }

    private static TypeTreeNodeStruct Convert(TypeTreeNode node)
    {
        TypeTreeNodeStruct[] subNodes = new TypeTreeNodeStruct[node.SubNodes.Length];
        for (int i = 0; i < subNodes.Length; i++)
        {
            subNodes[i] = Convert(node.SubNodes[i]);
        }
        return new TypeTreeNodeStruct(node.TypeName, node.Name, node.Version, node.MetaFlag, subNodes);
    }
}
