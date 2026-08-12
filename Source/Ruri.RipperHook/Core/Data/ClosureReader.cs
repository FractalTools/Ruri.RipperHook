using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.Data;

/// <summary>
/// Reads a set of CABs into live, deserialized assets -- the read path a dataset producer uses.
///
/// <para>Deliberately LOAD-ONLY. The export pipeline next door (RipperBlenderBridge.ImportCabs)
/// exists to hand a host the serialized documents it will build geometry from, and pays for
/// processing, texture prewarm and serialization to do it. A producer wants none of that: it is
/// going to read a few fields off a TextAsset or a MonoBehaviour and emit a table of strings. Going
/// through the export path for that -- which is what reading a game's tables in the HOST forced --
/// meant serializing a whole closure to YAML, shipping it across the bridge and re-parsing it there
/// to recover values that were already in memory as typed objects.</para>
///
/// <para>Nothing here is game-specific: which CABs, and what to read out of them, is the
/// producer's business.</para>
/// </summary>
public static class ClosureReader
{
    /// <summary>
    /// Resolve <paramref name="seedCabNames"/> to their dependency closure and load it.
    /// The returned <see cref="GameData"/> owns the loaded bundle; read it and let it go.
    /// </summary>
    public static GameData Load(CabTable table, IEnumerable<string> seedCabNames)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(seedCabNames);

        CabClosure closure = new CabSelection { SeedCabNames = seedCabNames.ToArray() }.Resolve(table);
        if (closure.Files.Length == 0)
        {
            // Loudly, not as an empty result: a producer asks for CABs it resolved itself, so an
            // empty closure means the resolution was wrong, and returning "no rows" would report
            // that as the game genuinely having none. A producer that expects a bundle to be
            // absent checks before calling.
            throw new InvalidOperationException(
                "no files resolved for the requested CABs -- they are not in this cabmap.");
        }

        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        // A producer reads the bundles' own typetrees; nothing it emits comes from a script stub,
        // and Level1+ re-runs the whole IL2Cpp assembly scan per call.
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
