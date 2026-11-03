namespace Ruri.RipperHook.CabMapping;

internal static class StructuralFields
{
    public const string Owner = "m_GameObject";

    private static readonly HashSet<string> Names = [Owner, "m_Components", "m_Father", "m_Children"];

    public static bool Contains(string outermostField) => Names.Contains(outermostField);
}
