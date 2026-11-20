namespace Ruri.RipperHook.RuriEngine;

internal delegate ObjectValue? MappedDecoder(DecodeContext context);

internal static class MappedDecoders
{
    private static readonly Dictionary<string, MappedDecoder> Table = new(StringComparer.Ordinal)
    {
        ["Mesh"] = MeshDecoder.Decode,
        ["Material"] = MaterialDecoder.Decode,
        ["GraphicsSettings"] = GraphicsSettingsDecoder.Decode,
    };

    public static bool TryGet(string unityClassName, [NotNullWhen(true)] out MappedDecoder? decoder)
    {
        return Table.TryGetValue(unityClassName, out decoder);
    }
}
