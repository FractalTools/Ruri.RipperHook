using System.Collections.Concurrent;
using AssetRipper.IO.Files;

namespace Ruri.RipperHook.RuriEngine;

internal readonly record struct PackageFile(string AssetPath, string RelativePath, bool IsScript);

internal sealed class PackageIndex
{
    private const string MetaExtension = ".meta";
    private const string ScriptExtension = ".cs";
    private const string AssetExtension = ".asset";
    private const string GuidPrefix = "guid:";
    private const string PackagesRoot = "Packages";
    private const char VersionSeparator = '@';
    private const int GuidLineLimit = 4;

    private readonly Dictionary<string, PackageFile> files = new(StringComparer.Ordinal);

    public static PackageIndex Empty { get; } = new();

    public int Count => files.Count;

    public static PackageIndex Scan(string cacheDirectory, FileSystem fileSystem)
    {
        PackageIndex index = new();
        foreach (string packageDirectory in fileSystem.Directory.EnumerateDirectories(cacheDirectory))
        {
            string folder = fileSystem.Path.GetFileName(packageDirectory);
            int separator = folder.IndexOf(VersionSeparator);
            string name = separator < 0 ? folder : folder[..separator];
            foreach (string metaPath in fileSystem.Directory.EnumerateFiles(packageDirectory, "*" + MetaExtension, SearchOption.AllDirectories))
            {
                string assetPath = metaPath[..^MetaExtension.Length];
                bool isScript = assetPath.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase);
                if (!isScript && !assetPath.EndsWith(AssetExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (GuidOf(metaPath, fileSystem) is not { } guid || !fileSystem.File.Exists(assetPath))
                {
                    continue;
                }
                string relative = fileSystem.Path.GetRelativePath(packageDirectory, assetPath).Replace('\\', '/');
                index.files[guid] = new PackageFile(assetPath, $"{PackagesRoot}/{name}/{relative}", isScript);
            }
        }
        return index;
    }

    public bool TryGet(string guid, out PackageFile file) => files.TryGetValue(guid, out file);

    private static string? GuidOf(string metaPath, FileSystem fileSystem)
    {
        using Stream stream = fileSystem.File.OpenRead(metaPath);
        using StreamReader reader = new(stream);
        for (int line = 0; line < GuidLineLimit && reader.ReadLine() is { } text; line++)
        {
            if (text.StartsWith(GuidPrefix, StringComparison.Ordinal))
            {
                return text[GuidPrefix.Length..].Trim();
            }
        }
        return null;
    }
}
