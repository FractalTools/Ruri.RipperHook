using System;
using System.Globalization;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// Identifies one type tree snapshot: a <see cref="Lineage"/> (an engine or a game's fork of one)
/// plus a free-form <see cref="Version"/> string.
///
/// Both halves are opaque strings on purpose. The old scheme encoded a game build into a
/// <c>UnityVersion</c> by concatenating its digits -- <c>1.4.4</c> became build <c>1404</c> -- which
/// only worked because the assembly dumper demanded a <c>UnityVersion</c> key. That packing has two
/// hard failures: <c>UnityVersion.Build</c> is a <see cref="ushort"/>, so anything past five digits
/// silently wraps, and the digits collide (<c>1.0.14</c> and <c>10.1.4</c> both pack to <c>1014</c>).
/// Nothing needs the numeric form any more, so a version is whatever the game calls itself.
/// </summary>
public readonly record struct TypeTreeVersion(string Lineage, string Version)
{
    /// <summary>
    /// A game's lineage is its directory under the external TypeTree dumps, which
    /// <see cref="CustomEngineType"/> already pins by numeric value -- so the engine enum is the one
    /// source of truth for it, not a second table to keep in sync.
    /// </summary>
    public TypeTreeVersion(CustomEngineType engine, string version)
        : this(((int)engine).ToString(CultureInfo.InvariantCulture), version)
    {
    }

    public bool IsEmpty => string.IsNullOrEmpty(Lineage) || string.IsNullOrEmpty(Version);

    public override string ToString() => $"{Lineage}/{Version}";
}
