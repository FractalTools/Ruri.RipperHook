using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.AR;

public sealed class ManagedReferenceType : SerializableType
{
    public static ManagedReferenceType Shared { get; } = new();

    private ManagedReferenceType() : base(null, PrimitiveType.Complex, "managedReference")
    {
        Fields = new[] { new Field(SerializablePrimitiveType.GetOrCreate(PrimitiveType.Long), 0, "rid", false) };
        MaxDepth = 1;
    }
}

public sealed class ManagedReferencesRegistryType : SerializableType
{
    public static ManagedReferencesRegistryType Shared { get; } = new();

    private ManagedReferencesRegistryType() : base(null, PrimitiveType.Complex, "ManagedReferencesRegistry")
    {
        Fields = System.Array.Empty<Field>();
        MaxDepth = 0;
    }
}
