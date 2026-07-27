using System;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// The one place that knows how a lineage's chain position is expressed as the
/// <see cref="UnityVersion"/> key a stock <c>TpkTypeTreeBlob</c> requires.
///
/// A tpk keys class definitions by <see cref="UnityVersion"/> and that is not changing. What changed
/// is what the key *means*: it is now a bare position in the lineage's chain, carrying none of the
/// game's own version. The game's version string lives in <see cref="TypeTreeManifest"/>, which is
/// what makes any version format expressible.
///
/// The remaining <see cref="ushort"/> ceiling therefore bounds the number of dumped snapshots in one
/// lineage, not the value of a version -- <see cref="MaxOrdinal"/> snapshots of a single game is not
/// a limit anything will reach, and overrunning it throws rather than wrapping.
/// </summary>
public static class TypeTreeOrdinal
{
    public const int MaxOrdinal = ushort.MaxValue;

    public static UnityVersion ToUnityVersion(int ordinal)
    {
        if ((uint)ordinal > MaxOrdinal)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal,
                $"A lineage cannot hold more than {MaxOrdinal} snapshots.");
        }
        return new UnityVersion(0, 0, (ushort)ordinal);
    }

    public static int ToOrdinal(UnityVersion version) => version.Build;
}
