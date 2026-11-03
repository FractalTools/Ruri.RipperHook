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

        long failures = _failures.Values.Sum();
        long unreadFiles = expectedFiles - FilesRead;
        long issues = unreadFiles + ObjectsUnreadable + ObjectsUnknown + ScriptLayoutsUnresolved
            + ReferencesToUnnamedFiles + ReferencesOutsideMap + ReferencesToMissingObjects + failures;
        Logger.Log(issues > 0 ? LogType.Warning : LogType.Info, LogCategory.Import,
            $"[CabMap] References: files not read {unreadFiles}, objects AssetRipper could not parse {ObjectsUnreadable}, "
            + $"objects of an unknown class {ObjectsUnknown}, MonoBehaviours without a script layout {ScriptLayoutsUnresolved}, "
            + $"references naming no file {ReferencesToUnnamedFiles}, references outside the map {ReferencesOutsideMap}, "
            + $"references to a missing object {ReferencesToMissingObjects}, exceptions {failures}");
        foreach ((string message, int count) in _failures.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Logger.Warning(LogCategory.Import, $"[CabMap] References: {count} x {message}");
        }
    }
}
