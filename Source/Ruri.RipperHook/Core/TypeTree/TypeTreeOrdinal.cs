using System;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.Core.TypeTree;

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
