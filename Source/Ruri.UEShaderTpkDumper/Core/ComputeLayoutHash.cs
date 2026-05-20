namespace Ruri.UEShaderTpkDumper.Core;

// Re-implements `FRHIUniformBufferLayoutInitializer::ComputeHash` byte-for-byte
// from `Engine/Source/Runtime/RHI/Public/RHIUniformBufferLayoutInitializer.h`
// (5.4) / `RHIResources.h:806-836` (5.1). The math XOR-folds ConstantBufferSize
// (high 16 bits), BindingFlags (mid 8 bits), and the StaticSlot indicator (bit 0)
// with every resource's MemberOffset (uint16) and MemberType (uint8). The
// MemberType byte is the UBMT integer enum — see `UbmtTables` for the
// version-aware mapping.
//
// UE 5.5+ NOTE: UE 5.5 added 3 more bits derived from `ERHIUniformBufferFlags`
// (NoEmulatedUniformBuffer, NeedsReflectedMembers, UniformView). They default
// to 0 so most UBs match either path identically, but UBs that explicitly set
// any flag would diverge — TODO when a failure surfaces.
public static class ComputeLayoutHash
{
    public readonly record struct Resource(int Offset, int UbmtValue);

    public static uint Compute(int constantBufferSize, int bindingFlags, bool hasStaticSlot, IReadOnlyList<Resource> resources)
    {
        uint h = ((uint)(constantBufferSize & 0xFFFF) << 16)
               | ((uint)(bindingFlags & 0xFF) << 8)
               | (hasStaticSlot ? 1u : 0u);

        foreach (Resource r in resources)
        {
            h ^= (uint)(r.Offset & 0xFFFF);
        }

        int n = resources.Count;
        while (n >= 4)
        {
            h ^= (uint)(resources[--n].UbmtValue & 0xFF) << 0;
            h ^= (uint)(resources[--n].UbmtValue & 0xFF) << 8;
            h ^= (uint)(resources[--n].UbmtValue & 0xFF) << 16;
            h ^= (uint)(resources[--n].UbmtValue & 0xFF) << 24;
        }
        while (n >= 2)
        {
            h ^= (uint)(resources[--n].UbmtValue & 0xFF) << 0;
            h ^= (uint)(resources[--n].UbmtValue & 0xFF) << 16;
        }
        while (n > 0)
        {
            h ^= (uint)(resources[--n].UbmtValue & 0xFF);
        }
        return h;
    }
}
