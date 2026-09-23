using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>Loading the dependency closure of a set of seed CABs -- the ONE statement of what
/// "everything this reaches" means and of how the load is gated to it, so the importer, the
/// shader reader and every dataset that asks about a selection all see the same assets.</summary>
public static class ClosureReader
{
    public static CabClosure Resolve(CabTable table, IEnumerable<string> seedCabNames,
        bool reachThroughDependents = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(seedCabNames);
        return new CabSelection
        {
            SeedCabNames = seedCabNames.ToArray(),
            ReachThroughDependents = reachThroughDependents,
        }.Resolve(table);
    }

    /// <summary>Load one closure's files, gated to the closure itself. The gate is process-wide
    /// state on the bundle hook, so it is set and cleared HERE rather than by each caller
    /// remembering to.</summary>
    public static GameData Load(CabClosure closure, ExportHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        HashSet<string> loadFilter = closure.LoadFilterFileNames;
        GameBundleHook.LoadIncludeFile = loadFilter.Count > 0 ? name => loadFilter.Contains(name) : null;
        HashSet<string> seedFiles = closure.SeedFileNames;
        GameBundleHook.LoadSeedFile = seedFiles.Count > 0 ? name => seedFiles.Contains(name) : null;
        GameBundleHook.LoadMappedFile = closure.Mapped;
        try
        {
            return handler.Load(closure.Files, LocalFileSystem.Instance);
        }
        finally
        {
            GameBundleHook.LoadIncludeFile = null;
            GameBundleHook.LoadSeedFile = null;
            GameBundleHook.LoadMappedFile = null;
        }
    }

    /// <summary>What a reader of a closure asks for: the assets, under the settings a read needs
    /// and nothing an export would. Null when the seeds are in no loaded map -- a caller that has
    /// nothing to say about an empty selection says nothing.</summary>
    public static GameData? Read(CabTable table, IEnumerable<string> seedCabNames,
        bool reachThroughDependents = false)
    {
        CabClosure closure = Resolve(table, seedCabNames, reachThroughDependents);
        if (closure.Files.Length == 0)
        {
            return null;
        }
        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
        return Load(closure, new ExportHandler(settings));
    }

    /// <summary>The closure's assets, for a caller to whom an empty selection is a mistake.</summary>
    public static GameData Load(CabTable table, IEnumerable<string> seedCabNames) =>
        Read(table, seedCabNames) ?? throw new InvalidOperationException(
            "no files resolved for the requested CABs -- they are not in this cabmap.");
}
