namespace Ruri.UEShaderTpkDumper.Core;

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
