using System.Collections.Concurrent;
using System.Diagnostics;
using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.CabMapping;

internal sealed class ReferenceReport
{
    public int FilesRead;
    public long ObjectsRead;
    public long ObjectsUnreadable;
    public long ObjectsUnknown;
    public long ScriptLayoutsUnresolved;
    public long ReferencesToUnnamedFiles;
    public long ReferencesOutsideMap;
    public long ReferencesToMissingObjects;

    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);

    public void Failure(string where, Exception exception)
    {
        string message = $"{where}: {exception.GetType().Name}: {exception.Message}";
        if (_failures.AddOrUpdate(message, 1, static (_, count) => count + 1) == 1)
        {
            Logger.Error(LogCategory.Import, $"[CabMap] References: {message}");
        }
    }

    public void Log(int expectedFiles, long rawEdges, long edges, Stopwatch watch)
    {
        Logger.Info(LogCategory.Import,
            $"[CabMap] References: {FilesRead}/{expectedFiles} file(s), {ObjectsRead} object(s), "
            + $"{rawEdges} raw reference(s) -> {edges} asset-to-asset edge(s) in {watch.Elapsed.TotalSeconds:F1} s");
        Warn(expectedFiles - FilesRead, "file(s) with asset rows were not read");
        Warn(ObjectsUnreadable, "object(s) AssetRipper could not parse");
        Warn(ObjectsUnknown, "object(s) of a class AssetRipper does not know");
        Warn(ScriptLayoutsUnresolved, "MonoBehaviour(s) whose script layout could not be resolved");
        Warn(ReferencesToUnnamedFiles, "reference(s) name a file the dependency table does not hold");
        Warn(ReferencesOutsideMap, "reference(s) point at a file outside the map");
        Warn(ReferencesToMissingObjects, "reference(s) point at an object the target file does not hold");
        foreach ((string message, int count) in _failures.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Logger.Warning(LogCategory.Import, $"[CabMap] References: {count} x {message}");
        }
    }

    private static void Warn(long count, string what)
    {
        if (count > 0)
        {
            Logger.Warning(LogCategory.Import, $"[CabMap] References: {count} {what}");
        }
    }
}
