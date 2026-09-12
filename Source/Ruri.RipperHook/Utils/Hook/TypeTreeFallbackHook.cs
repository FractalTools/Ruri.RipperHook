using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.IO.Files.SerializedFiles;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;
using Ruri.RipperHook.Core.TypeTree;

namespace Ruri.RipperHook.HookUtils.TypeTreeFallbackHook;

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
