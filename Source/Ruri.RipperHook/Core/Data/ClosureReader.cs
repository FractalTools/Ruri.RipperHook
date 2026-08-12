using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.Data;

public static class ClosureReader
{
    public static GameData Load(CabTable table, IEnumerable<string> seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(seedCabNames);

        CabClosure closure = new CabSelection { SeedCabNames = seedCabNames.ToArray() }.Resolve(table);
        if (closure.Files.Length == 0)
        {
            throw new InvalidOperationException(
                "no files resolved for the requested CABs -- they are not in this cabmap.");
        }

        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;

        HashSet<string> loadFilter = closure.LoadFilterFileNames;
        GameBundleHook.LoadIncludeFile = loadFilter.Count > 0 ? name => loadFilter.Contains(name) : null;
        try
        {
            return new ExportHandler(settings).Load(closure.Files, LocalFileSystem.Instance);
        }
        finally
        {
            GameBundleHook.LoadIncludeFile = null;
        }
    }
}
