using System.Globalization;

namespace Ruri.RipperHook.RuriEngine;

internal static class NodeMeta
{
    private const string ImporterName = "NativeFormatImporter";
    private const string FormatVersionKey = "fileFormatVersion";
    private const string DefaultFormatVersion = "2";

    private static readonly string[] CarriedKeys = ["userData", "assetBundleName", "assetBundleVariant"];

    public static UnityYamlMapping Of(ProjectEntry entry, long rootKey)
    {
        UnityYamlMapping source = entry.Meta.TryGet(entry.ImporterName, out UnityYamlNode? block) && block is UnityYamlMapping found ? found : new UnityYamlMapping();
        UnityYamlMapping importer = new();
        importer.Add("externalObjects", new UnityYamlMapping());
        importer.Add("mainObjectFileID", Scalar(rootKey.ToString(CultureInfo.InvariantCulture)));
        foreach (string key in CarriedKeys)
        {
            importer.Add(key, Scalar(source.TryGetText(key) ?? string.Empty));
        }
        UnityYamlMapping meta = new();
        meta.Add(FormatVersionKey, Scalar(entry.Meta.TryGetText(FormatVersionKey) ?? DefaultFormatVersion));
        meta.Add("guid", Scalar(entry.Guid));
        meta.Add(ImporterName, importer);
        return meta;
    }

    private static UnityYamlScalar Scalar(string text) => new(text, false);
}
