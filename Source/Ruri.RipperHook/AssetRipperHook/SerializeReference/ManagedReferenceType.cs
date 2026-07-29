using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.AR;

/// <summary>
/// <c>[SerializeReference]</c> 字段在类型树里的形态:内容只有一个 <c>SInt64 rid</c>。
/// 不认这个形状就会按普通复合类型去建结构,读取必然错位。
/// </summary>
public sealed class ManagedReferenceType : SerializableType
{
    public static ManagedReferenceType Shared { get; } = new();

    private ManagedReferenceType() : base(null, PrimitiveType.Complex, "managedReference")
    {
        Fields = new[] { new Field(SerializablePrimitiveType.GetOrCreate(PrimitiveType.Long), 0, "rid", false) };
        MaxDepth = 1;
    }
}

/// <summary>尾部 <c>ManagedReferencesRegistry</c> 的标记类型;真实二进制布局在读写时单独处理。</summary>
public sealed class ManagedReferencesRegistryType : SerializableType
{
    public static ManagedReferencesRegistryType Shared { get; } = new();

    private ManagedReferencesRegistryType() : base(null, PrimitiveType.Complex, "ManagedReferencesRegistry")
    {
        Fields = System.Array.Empty<Field>();
        MaxDepth = 0;
    }
}
