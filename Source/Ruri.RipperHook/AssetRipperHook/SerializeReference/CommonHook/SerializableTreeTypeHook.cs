using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    private const string ManagedReferenceNode = "managedReference";
    private const string ManagedRefArrayItemNode = "managedRefArrayItem";

    [RetargetMethod(typeof(SerializableTreeType), "AddNode")]
    public static bool AddNode(TypeTreeNodeStruct node, List<SerializableType.Field> fields)
    {
        if (!TryGetManagedReferenceShape(node, out int arrayDepth, out bool align))
        {
            return false;
        }
        fields.Add(new SerializableType.Field(ManagedReferenceType.Shared, arrayDepth, node.Name, align));
        return true;
    }

    private static bool TryGetManagedReferenceShape(TypeTreeNodeStruct node, out int arrayDepth, out bool align)
    {
        arrayDepth = 0;
        align = node.AlignBytes;
        if (node.TypeName is ManagedReferenceNode or ManagedRefArrayItemNode)
        {
            return true;
        }
        if (node.SubNodes.Count == 1
            && node.SubNodes[0].IsArray
            && node.SubNodes[0].SubNodes.Count > 1
            && node.SubNodes[0].SubNodes[1].TypeName is ManagedReferenceNode or ManagedRefArrayItemNode)
        {
            arrayDepth = 1;
            align = node.SubNodes[0].AlignBytes;
            return true;
        }
        return false;
    }
}
