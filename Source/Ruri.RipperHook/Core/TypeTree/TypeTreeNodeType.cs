namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// Serialization category of a type tree node. 1:1 port of
/// <c>AssetRipper.AssemblyDumper.NodeType</c> -- the read interpreter must classify nodes exactly
/// the way the assembly dumper did, or the emitted-vs-interpreted layouts diverge.
/// </summary>
public enum TypeTreeNodeType
{
    Type,
    Boolean,
    Character,
    String,
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Single,
    Double,
    Vector,
    Array,
    Pair,
    Map,
    TypelessData,
}
