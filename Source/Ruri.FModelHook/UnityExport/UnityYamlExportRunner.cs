using AssetRipper.Assets;
using AssetRipper.Primitives;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using Ruri.FModelHook.UnityExport.Engine;
using Ruri.FModelHook.UnityExport.Mappings;

namespace Ruri.FModelHook.UnityExport;

// Headless UE -> Unity YAML export driver. Given a mounted CUE4Parse provider,
// it walks matching packages, converts every export through the mapper registry,
// and writes the synthetic Unity assets as .asset + .meta under OutputDirectory.
// Mirrors HeadlessShaderExportRunner's shape (provider in, stats out) so the CLI
// drives it the same way it drives shader export — this is the self-test loop's
// workhorse.
public static class UnityYamlExportRunner
{
    public sealed class Options
    {
        public required IFileProvider Provider { get; init; }
        public required string OutputDirectory { get; init; }
        // Target Unity version text (e.g. "2022.3.0f1"). Parsed here so the CLI
        // stays free of any AssetRipper type dependency. Null/empty -> 2022.3.0f1.
        public string? UnityVersionText { get; init; }
        // Substring tokens (OR). Null/empty = every .uasset/.umap.
        public IReadOnlyList<string>? PackageFilter { get; init; }
        // Cap packages scanned (self-test throttle). 0/null = unlimited.
        public int? MaxPackages { get; init; }
        public Action<string> Log { get; init; } = _ => { };
        public Action<string> LogError { get; init; } = _ => { };
    }

    public sealed class RunResult
    {
        public int PackagesScanned;
        public int ExportsSeen;
        public int Converted;
        public int FilesWritten;
        public readonly SortedDictionary<string, int> ConvertedByType = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> UnmappedByType = new(StringComparer.Ordinal);
    }

    public static RunResult Run(Options options)
    {
        List<string> packages = SelectPackages(options);
        options.Log($"[UnityExport] {packages.Count} package(s) selected " +
                    $"(filter={(options.PackageFilter is { Count: > 0 } f ? string.Join(",", f) : "*")}).");
        return ConvertAndExport(options.Provider, packages, options.OutputDirectory, options.UnityVersionText, options.Log, options.LogError);
    }

    // Convert an explicit set of package keys and write the result. Shared by the
    // headless CLI (Run) and the FModel GUI hook (which passes the right-clicked
    // selection). Null/empty unityVersionText defaults to 2022.3.0f1.
    public static RunResult ConvertAndExport(
        IFileProvider provider,
        IReadOnlyList<string> packageKeys,
        string outputDirectory,
        string? unityVersionText,
        Action<string> log,
        Action<string> logError)
    {
        UnityMappings.RegisterAll();

        UnityVersion version;
        string versionText = string.IsNullOrWhiteSpace(unityVersionText) ? "2022.3.0f1" : unityVersionText!;
        try
        {
            version = UnityVersion.Parse(versionText);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid Unity version '{versionText}': {ex.Message}", ex);
        }
        log($"[UnityExport] target Unity version = {version}");

        RunResult result = new();
        UnityYamlExportSession session = new(version, logError);

        foreach (string key in packageKeys)
        {
            if (!provider.Files.TryGetValue(key, out GameFile? gameFile) || gameFile == null)
                continue;

            IPackage package;
            try
            {
                package = provider.LoadPackage(gameFile);
            }
            catch (Exception ex)
            {
                logError($"[UnityExport] LoadPackage failed '{key}': {ex.Message}");
                continue;
            }
            result.PackagesScanned++;

            foreach (Lazy<UObject> lazy in package.ExportsLazy)
            {
                UObject export;
                try
                {
                    export = lazy.Value;
                }
                catch (Exception ex)
                {
                    logError($"[UnityExport] export deserialize failed in '{key}': {ex.Message}");
                    continue;
                }
                result.ExportsSeen++;

                string typeName = export.GetType().Name;
                if (session.Convert(export) != null)
                {
                    result.Converted++;
                    Bump(result.ConvertedByType, typeName);
                }
                else
                {
                    Bump(result.UnmappedByType, typeName);
                }
            }
        }

        Directory.CreateDirectory(outputDirectory);
        result.FilesWritten = session.ExportAll(outputDirectory);

        log($"[UnityExport] Done. packages={result.PackagesScanned} exports={result.ExportsSeen} " +
            $"converted={result.Converted} files={result.FilesWritten}");
        LogTypeBreakdown(log, result);
        return result;
    }

    private static List<string> SelectPackages(Options options)
    {
        IReadOnlyList<string>? filter = options.PackageFilter;
        List<string> selected = new();
        foreach (string key in options.Provider.Files.Keys)
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                !key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                continue;

            if (filter is { Count: > 0 })
            {
                bool match = false;
                foreach (string token in filter)
                {
                    if (key.Contains(token, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
                if (!match) continue;
            }
            selected.Add(key);
        }
        selected.Sort(StringComparer.OrdinalIgnoreCase);

        if (options.MaxPackages is > 0 && selected.Count > options.MaxPackages.Value)
            selected = selected.GetRange(0, options.MaxPackages.Value);
        return selected;
    }

    private static void LogTypeBreakdown(Action<string> log, RunResult result)
    {
        if (result.ConvertedByType.Count > 0)
        {
            log("[UnityExport] converted by type: " +
                string.Join(", ", result.ConvertedByType.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        if (result.UnmappedByType.Count > 0)
        {
            // The top unmapped types are the worklist for the next phase.
            IEnumerable<string> top = result.UnmappedByType
                .OrderByDescending(kv => kv.Value)
                .Take(15)
                .Select(kv => $"{kv.Key}={kv.Value}");
            log("[UnityExport] unmapped by type (top 15): " + string.Join(", ", top));
        }
    }

    private static void Bump(SortedDictionary<string, int> counts, string key)
        => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
}
