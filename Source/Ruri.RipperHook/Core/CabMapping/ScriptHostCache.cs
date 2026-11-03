using System.Collections.Concurrent;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.CabMapping;

internal sealed class ScriptHostCache
{
    private readonly string _rootFolder;
    private readonly IReadOnlyDictionary<string, CabMap.Entry> _entries;
    private readonly ReferenceReport _report;
    private readonly ConcurrentDictionary<string, Lazy<SerializedFile?>> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public ScriptHostCache(string rootFolder, IReadOnlyDictionary<string, CabMap.Entry> entries, ReferenceReport report)
    {
        _rootFolder = rootFolder;
        _entries = entries;
        _report = report;
    }

    public SerializedFile? Find(string cab)
    {
        if (!_entries.TryGetValue(cab, out CabMap.Entry? entry) || !entry.ClassIds.Contains((int)ClassIDType.MonoScript))
        {
            return null;
        }
        return _hosts.GetOrAdd(cab, _ => new Lazy<SerializedFile?>(() => Load(cab, entry))).Value;
    }

    private SerializedFile? Load(string cab, CabMap.Entry entry)
    {
        SerializedFile? host = null;
        SerializedFileOpener.Visit(Path.Combine(_rootFolder, entry.RelativePath),
            (archiveName, file) =>
            {
                string name = string.IsNullOrWhiteSpace(file.NameFixed) ? archiveName : file.NameFixed;
                if (string.Equals(name, cab, StringComparison.OrdinalIgnoreCase))
                {
                    host = file;
                }
            },
            (stage, exception) => _report.Failure($"{stage} script host '{entry.RelativePath}'", exception));
        return host;
    }
}
