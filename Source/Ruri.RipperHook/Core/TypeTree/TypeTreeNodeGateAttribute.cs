using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// Marks a <c>static bool Gate(TypeTreeReadContext context)</c> that decides whether one node is
/// present in the byte stream at all.
///
/// A Unity type tree is unconditional -- every node it lists is always serialized -- so a fork that
/// makes a field conditional is the one layout deviation the tpk simply cannot encode. EndField's
/// Mesh is the canonical case: <c>m_CompressedMesh</c> is only written when
/// <c>m_CollisionMeshBaked</c> is false.
///
/// The gate runs against values captured earlier in the same read, so every node it inspects must be
/// listed in <see cref="Captures"/> -- that is what tells the read plan to retain them instead of
/// discarding.
///
/// Competes for its slot by (class, node path) and is resolved by <c>[Since]</c> exactly like every
/// other capability.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class TypeTreeNodeGateAttribute : Attribute
{
    public ClassIDType ClassID { get; }

    /// <summary>Slash separated path from the class root, using sanitized node names.</summary>
    public string NodePath { get; }

    public string[] Captures { get; set; } = Array.Empty<string>();

    public TypeTreeNodeGateAttribute(ClassIDType classID, string nodePath)
    {
        ClassID = classID;
        NodePath = nodePath;
    }
}
