using CUE4Parse.UE4.Assets.Objects;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>
/// One serialized value as text. The engine's own ToString for a container states what it holds
/// rather than what is in it -- an array reads as its inner type and a count -- which turns the
/// one column a designer actually filled in into "StrProperty[4]". So containers are spelled out
/// here, once, for everything that states a property's value.
/// </summary>
public static class UnrealValues
{
    private const string Separator = ", ";

    public static string Rendered(object? value) => value switch
    {
        null => string.Empty,
        UScriptArray array => string.Join(Separator, array.Properties.Select(entry => Rendered(entry?.GenericValue))),
        UScriptSet set => string.Join(Separator, set.Properties.Select(entry => Rendered(entry?.GenericValue))),
        UScriptMap map => string.Join(Separator, map.Properties.Select(entry =>
            Rendered(entry.Key?.GenericValue) + "=" + Rendered(entry.Value?.GenericValue))),
        FScriptStruct structure => Rendered(structure.StructType),
        FStructFallback fallback => string.Join(Separator, fallback.Properties.Select(tag =>
            tag.Name.Text + "=" + Rendered(tag.Tag?.GenericValue))),
        _ => value.ToString() ?? string.Empty,
    };
}
