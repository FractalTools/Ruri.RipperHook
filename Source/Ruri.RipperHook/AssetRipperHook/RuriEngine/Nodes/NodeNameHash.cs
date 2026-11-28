namespace Ruri.RipperHook.RuriEngine;

internal static class NodeNameHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
    private const string NoneName = "None";

    public static ulong Of(string name)
    {
        if (name.Length == 0 || name.Equals(NoneName, StringComparison.OrdinalIgnoreCase))
        {
            return 0UL;
        }
        ulong hash = OffsetBasis;
        foreach (char letter in name)
        {
            hash = unchecked((hash ^ char.ToUpperInvariant(letter)) * Prime);
        }
        return hash == 0UL ? 1UL : hash;
    }
}
