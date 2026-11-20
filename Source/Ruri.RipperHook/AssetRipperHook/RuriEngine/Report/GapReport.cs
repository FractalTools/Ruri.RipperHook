using System.Collections.Concurrent;
using AssetRipper.IO.Files;

namespace Ruri.RipperHook.RuriEngine;

internal static class GapCategories
{
    public const string UnmappedClass = "unmappedClasses";
    public const string NoEngineType = "noEngineType";
    public const string UnresolvedClass = "unresolvedClasses";
    public const string UnresolvedScript = "unresolvedScripts";
    public const string IgnoredField = "ignoredFields";
    public const string ShapeMismatch = "shapeMismatches";
    public const string DanglingReference = "danglingReferences";
    public const string UnconvertedReference = "referencesToUnconvertedAssets";
    public const string DuplicateComponent = "duplicateComponents";
    public const string UnmappedImporter = "unmappedImporters";
    public const string UnreadableFile = "unreadableFiles";
    public const string OrphanMeta = "orphanMetas";
    public const string UnsupportedLayout = "unsupportedLayouts";
    public const string PackageReferenceBlanked = "packageReferencesBlanked";
    public const string PackageReferenceRedirected = "packageReferencesRedirected";
}

internal sealed class GapReport
{
    public const string FileName = "RuriEngineGapReport.json";

    private const int SampleLimit = 100;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> samples = new(StringComparer.Ordinal);

    public string ProjectName { get; set; } = string.Empty;

    public string ProjectVersion { get; set; } = string.Empty;

    public void Add(string category, string key, string? sample = null)
    {
        ConcurrentDictionary<string, int> bucket = counts.GetOrAdd(category, static _ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal));
        bucket.AddOrUpdate(key, 1, static (_, count) => count + 1);
        if (sample is null)
        {
            return;
        }
        ConcurrentQueue<string> queue = samples.GetOrAdd(category, static _ => new ConcurrentQueue<string>());
        if (queue.Count < SampleLimit)
        {
            queue.Enqueue(sample);
        }
    }

    public ObjectValue ToValue()
    {
        ObjectValue root = new();
        if (ProjectName.Length > 0 || ProjectVersion.Length > 0)
        {
            ObjectValue project = new();
            project.Add("name", ScalarValue.FromText(ProjectName));
            project.Add("unityVersion", ScalarValue.FromText(ProjectVersion));
            root.Add("project", project);
        }
        foreach (string category in counts.Keys.Order(StringComparer.Ordinal))
        {
            ObjectValue bucket = new();
            long total = 0;
            foreach (KeyValuePair<string, int> entry in counts[category].OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                bucket.Add(entry.Key, ScalarValue.FromInteger(entry.Value));
                total += entry.Value;
            }
            ObjectValue section = new();
            section.Add("total", ScalarValue.FromInteger(total));
            section.Add("distinct", ScalarValue.FromInteger(bucket.Count));
            section.Add("counts", bucket);
            if (samples.TryGetValue(category, out ConcurrentQueue<string>? queue))
            {
                ArrayValue examples = new();
                foreach (string sample in queue)
                {
                    examples.Items.Add(ScalarValue.FromText(sample));
                }
                section.Add("samples", examples);
            }
            root.Add(category, section);
        }
        return root;
    }

    public void Write(FileSystem fileSystem, string path)
    {
        string? directory = fileSystem.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.Directory.Create(directory);
        }
        fileSystem.File.WriteAllBytes(path, JsonValueWriter.Write(ToValue()));
    }
}
