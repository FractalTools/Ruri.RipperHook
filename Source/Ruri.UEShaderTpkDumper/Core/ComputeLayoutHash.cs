using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper.Core;

/// <summary>
/// The hash the engine stamps on a uniform buffer layout, as THAT engine computes it.
///
/// A cooked shader carries this hash for every uniform buffer it binds, and it is the one thing
/// that says "the layout this shader was compiled against is the layout this seed describes".
/// The engine changed the formula twice: 4.x folds the buffer's static-slot INDEX into the low
/// byte, 5.0 replaced that with the binding flags and a bit saying whether a slot exists at all,
/// 5.5 added three bits of usage flags. One formula typed from one version hashed every 4.x seed
/// to a value no 4.x cook carries, and the whole engine-symbol lane read as absent.
///
/// The 4.x slot index is assigned at engine start in registration order, which no source can
/// state, so a 4.x seed is hashed with slot zero and its reader matches on everything but that
/// byte -- XOR being invertible, the cook's own byte then reads straight out of the difference.
/// </summary>
public static class ComputeLayoutHash
{
    public readonly record struct Resource(int Offset, int UbmtValue);

    /// <summary>Bit the layout carries when the buffer is not emulated (also set by a uniform view).</summary>
    private const uint NoEmulatedBit = 1u << 1;

    /// <summary>Bit the layout carries when the buffer's members are reflected for binding.</summary>
    private const uint NeedsReflectedMembersBit = 1u << 2;

    /// <summary>Bit the layout carries when the buffer is a view into a uniform buffer object.</summary>
    private const uint UniformViewBit = 1u << 3;

    public static uint Compute(string formula, int constantBufferSize, int bindingFlags, bool hasStaticSlot,
        int usageFlags, IReadOnlyDictionary<string, int> usageFlagValues, IReadOnlyList<Resource> resources)
    {
        uint hash = (uint)(constantBufferSize & 0xFFFF) << 16;
        switch (formula)
        {
            case EngineFacts.SizeAndStaticSlot:
                break;
            case EngineFacts.SizeFlagsStatic:
                hash |= (uint)(bindingFlags & 0xFF) << 8;
                hash |= hasStaticSlot ? 1u : 0u;
                break;
            case EngineFacts.SizeFlagsStaticUsage:
                hash |= (uint)(bindingFlags & 0xFF) << 8;
                hash |= hasStaticSlot ? 1u : 0u;
                hash |= LayoutFlagBits(usageFlags, usageFlagValues);
                break;
            default:
                throw new ArgumentException($"no hash formula named '{formula}'", nameof(formula));
        }

        foreach (Resource resource in resources)
        {
            hash ^= (uint)(resource.Offset & 0xFFFF);
        }

        int n = resources.Count;
        while (n >= 4)
        {
            hash ^= (uint)(resources[--n].UbmtValue & 0xFF) << 0;
            hash ^= (uint)(resources[--n].UbmtValue & 0xFF) << 8;
            hash ^= (uint)(resources[--n].UbmtValue & 0xFF) << 16;
            hash ^= (uint)(resources[--n].UbmtValue & 0xFF) << 24;
        }
        while (n >= 2)
        {
            hash ^= (uint)(resources[--n].UbmtValue & 0xFF) << 0;
            hash ^= (uint)(resources[--n].UbmtValue & 0xFF) << 16;
        }
        while (n > 0)
        {
            hash ^= (uint)(resources[--n].UbmtValue & 0xFF);
        }
        return hash;
    }

    /// <summary>
    /// The layout flag bits a buffer's usage flags turn into, as FShaderParametersMetadata::InitializeLayout
    /// states it: a uniform view is also not emulated, and each of the three usages has a bit of its own.
    /// </summary>
    private static uint LayoutFlagBits(int usageFlags, IReadOnlyDictionary<string, int> usageFlagValues)
    {
        uint bits = 0;
        bool uniformView = Has(usageFlags, usageFlagValues, "UniformView");
        if (uniformView)
        {
            bits |= UniformViewBit;
        }
        if (uniformView || Has(usageFlags, usageFlagValues, "NoEmulatedUniformBuffer"))
        {
            bits |= NoEmulatedBit;
        }
        if (Has(usageFlags, usageFlagValues, "NeedsReflectedMembers"))
        {
            bits |= NeedsReflectedMembersBit;
        }
        return bits;
    }

    private static bool Has(int usageFlags, IReadOnlyDictionary<string, int> usageFlagValues, string name)
        => usageFlagValues.TryGetValue(name, out int bit) && (usageFlags & bit) != 0;
}
