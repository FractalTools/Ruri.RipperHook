using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    private const string ManagedReferenceNode = "managedReference";
    private const string ManagedRefArrayItemNode = "managedRefArrayItem";

    /// <summary>
    /// 条件前缀:只拦 <c>[SerializeReference]</c> 字段节点,自己建成
    /// <see cref="ManagedReferenceType"/>(内容仅一个 <c>SInt64 rid</c>),其余返回 false 交回原实现。
    /// <para>不认这个形状的话,原实现会按普通复合类型去建结构,读取必然错位;
    /// <c>List&lt;T&gt;</c> 形态的元素节点名是 <c>managedRefArrayItem</c>,不认还会让
    /// <c>WalkEditor</c> 吐出 Unity 拒收的 <c>Array:</c> 包装。</para>
    /// </summary>
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

    /// <summary>直接字段 → 深度 0;<c>List&lt;T&gt;</c> → 深度 1。都不是则不是引用字段。</summary>
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
