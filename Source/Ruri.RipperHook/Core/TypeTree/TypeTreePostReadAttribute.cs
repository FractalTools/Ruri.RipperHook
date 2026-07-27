using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// Marks a <c>static void PostRead(TypeTreeReadContext context)</c> that runs once the whole asset
/// has been read.
///
/// This is where a fork's extra payload gets turned into something stock AssetRipper understands:
/// decoding EndField's ACL compressed animation buffers into the sample arrays, CRC32-hashing
/// <c>m_TOSData</c> back into <c>m_TOS</c>, hoisting a nested shader blob to the root. The raw
/// game-only nodes those need are named in <see cref="Captures"/> and reachable through
/// <see cref="TypeTreeReadContext.Require"/>.
///
/// Competes for its slot by class and is resolved by <c>[Since]</c>. Several hooks may target the
/// same class by giving them distinct <see cref="Slot"/> names.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class TypeTreePostReadAttribute : Attribute
{
    public ClassIDType ClassID { get; }

    /// <summary>Distinguishes independent post-read steps on the same class; defaults to the only slot.</summary>
    public string Slot { get; set; } = "";

    public string[] Captures { get; set; } = Array.Empty<string>();

    public TypeTreePostReadAttribute(ClassIDType classID)
    {
        ClassID = classID;
    }
}
