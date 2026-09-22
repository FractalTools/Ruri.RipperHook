using CUE4Parse.MappingsProvider.Usmap;

namespace Ruri.Tpk.Unreal.Reflection;

/// <summary>
/// The property schema of one build, engine-neutral: every reflected enumeration and every
/// struct (script structs and classes alike, as the unversioned serializer sees them) with its
/// super and its own properties in declaration order. What a .usmap states, before it is a file.
/// </summary>
internal sealed class ReflectedSchema
{
    public List<ReflectedEnum> Enums { get; } = new();

    public List<ReflectedStruct> Structs { get; } = new();

    /// <summary>
    /// Intrinsic classes the executable constructs but whose type the program database never
    /// recorded, so their parent cannot be stated: left out rather than misstated, by name.
    /// </summary>
    public List<string> OmittedClasses { get; } = new();
}

internal sealed record ReflectedEnum(string Name, IReadOnlyList<ReflectedEnumerator> Entries);

internal readonly record struct ReflectedEnumerator(string Name, long Value);

internal sealed record ReflectedStruct(string Name, string? Super, IReadOnlyList<ReflectedProperty> Properties);

internal sealed record ReflectedProperty(string Name, int ArrayDim, ReflectedType Type);

internal sealed record ReflectedType(EPropertyType Kind, string? StructName = null, string? EnumName = null, ReflectedType? Inner = null, ReflectedType? Value = null);
