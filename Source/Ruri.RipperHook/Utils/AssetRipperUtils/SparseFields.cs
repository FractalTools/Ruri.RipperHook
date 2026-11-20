using System.Runtime.CompilerServices;
using AssetRipper.Assets;

namespace Ruri.RipperHook.HookUtils;

public static class SparseFields
{
    private static readonly ConditionalWeakTable<IUnityAssetBase, HashSet<string>> Absent = new();

    public static void MarkAbsent(IUnityAssetBase structure, string fieldName)
    {
        Absent.GetOrCreateValue(structure).Add(fieldName);
    }

    public static bool IsAbsent(IUnityAssetBase structure, string fieldName)
    {
        return Absent.TryGetValue(structure, out HashSet<string>? names) && names.Contains(fieldName);
    }
}
