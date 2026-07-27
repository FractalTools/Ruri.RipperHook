using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// Marks a <c>static T Fix(T value)</c> that rewrites one primitive node's value right after it is
/// read, before it reaches the asset.
///
/// This is for forks that reuse a stock field with a non-stock meaning -- EndField writes mesh
/// compression mode 4, which stock AssetRipper has no decoder for and which has to be normalized to
/// 0 (uncompressed) or every downstream vertex reader misbehaves.
///
/// Competes for its slot by (class, node path) and is resolved by <c>[Since]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class TypeTreeValueFixAttribute : Attribute
{
    public ClassIDType ClassID { get; }

    /// <summary>Slash separated path from the class root, using sanitized node names.</summary>
    public string NodePath { get; }

    public TypeTreeValueFixAttribute(ClassIDType classID, string nodePath)
    {
        ClassID = classID;
        NodePath = nodePath;
    }
}
