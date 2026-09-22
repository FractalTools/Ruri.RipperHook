using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Ruri.Hook.Core
{
    /// <summary>
    /// Records what every retargeted upstream method looked like when its hook was written, and says so when it changed.
    /// </summary>
    /// <remarks>
    /// Set the RURI_HOOK_BASELINE environment variable to 1 and run once to rewrite the baseline file. Do that only
    /// after reading the upstream diff for each method the run reports, because re-recording is how a real behaviour
    /// change gets accepted as normal.
    /// </remarks>
    public static class HookBaselines
    {
        private const string FileName = "HookBaselines.json";
        private const string SourcePathKey = "RuriHookBaselineSourcePath";

        //Configuration -> method key -> fingerprint. An unoptimized build of the same source has different IL,
        //so a baseline is only meaningful next to the configuration it was recorded from.
        private static readonly Dictionary<string, Dictionary<string, string>> Baseline = Load();
        private static readonly Dictionary<string, Dictionary<string, string>> Observed = new(StringComparer.Ordinal);
        private static readonly SortedSet<string> Drifted = new(StringComparer.Ordinal);
        private static readonly SortedSet<string> Unbaselined = new(StringComparer.Ordinal);

        public static bool Recording { get; } =
            Environment.GetEnvironmentVariable("RURI_HOOK_BASELINE") is "1" or "true" or "TRUE";

        /// <summary>
        /// Compare an upstream method against its baseline. Never throws and never blocks a hook: a changed method is
        /// a reason to look, not proof that the hook is wrong.
        /// </summary>
        public static void Verify(MethodBase source, string? hookId)
        {
            string key = HookTargetFingerprint.KeyOf(source);
            string configuration = HookTargetFingerprint.ConfigurationOf(source);
            string? actual = HookTargetFingerprint.Compute(source);
            if (actual is null)
            {
                return;
            }

            if (!Observed.TryGetValue(configuration, out Dictionary<string, string>? observed))
            {
                observed = new Dictionary<string, string>(StringComparer.Ordinal);
                Observed[configuration] = observed;
            }
            observed[key] = actual;

            if (Recording)
            {
                return;
            }

            if (!Baseline.TryGetValue(configuration, out Dictionary<string, string>? baseline)
                || !baseline.TryGetValue(key, out string? expected))
            {
                Unbaselined.Add($"{configuration} {key}");
                return;
            }

            if (expected == actual)
            {
                return;
            }
            if (!Drifted.Add(key))
            {
                //Several hooks can retarget the same method. Say it once.
                return;
            }

            HookLogger.LogWarning(
                $"Upstream rewrote {key} ({configuration}) since the hook{(hookId is null ? "" : $" '{hookId}'")} was written " +
                $"(baseline {expected}, now {actual}). The hook replaces that method, so anything upstream added to it " +
                $"is not happening. Read the upstream diff before trusting this run.");
        }

        private static int reportedDrift = -1;
        private static int reportedUnbaselined = -1;

        /// <summary>
        /// Report what the run found, and rewrite the baseline file when recording.
        /// </summary>
        /// <remarks>
        /// A host reapplies hooks every time it switches games, so only a changed tally is worth saying again.
        /// </remarks>
        public static void Report()
        {
            if (Recording)
            {
                Write();
                return;
            }

            if (Drifted.Count == reportedDrift && Unbaselined.Count == reportedUnbaselined)
            {
                return;
            }
            reportedDrift = Drifted.Count;
            reportedUnbaselined = Unbaselined.Count;

            if (Drifted.Count > 0)
            {
                HookLogger.LogWarning(
                    $"{Drifted.Count} hooked upstream method(s) changed since their baseline. " +
                    $"Treat any wrong output, missing data, or runaway memory in this run as caused by that first.");
            }
            if (Unbaselined.Count > 0)
            {
                HookLogger.Log(
                    $"{Unbaselined.Count} hooked upstream method(s) have no baseline for their build configuration. " +
                    $"Run once with RURI_HOOK_BASELINE=1 to record them.");
            }
        }

        private static void Write()
        {
            Dictionary<string, Dictionary<string, string>> merged = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, string>> configuration in Baseline)
            {
                merged[configuration.Key] = new Dictionary<string, string>(configuration.Value, StringComparer.Ordinal);
            }
            foreach (KeyValuePair<string, Dictionary<string, string>> configuration in Observed)
            {
                if (!merged.TryGetValue(configuration.Key, out Dictionary<string, string>? target))
                {
                    target = new Dictionary<string, string>(StringComparer.Ordinal);
                    merged[configuration.Key] = target;
                }
                foreach (KeyValuePair<string, string> pair in configuration.Value)
                {
                    target[pair.Key] = pair.Value;
                }
            }

            int count = merged.Sum(static configuration => configuration.Value.Count);
            string json = JsonSerializer.Serialize(
                merged.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(
                    static p => p.Key,
                    static p => p.Value.OrderBy(static q => q.Key, StringComparer.Ordinal).ToDictionary(static q => q.Key, static q => q.Value)),
                new JsonSerializerOptions { WriteIndented = true });

            foreach (string path in CandidatePaths())
            {
                try
                {
                    File.WriteAllText(path, json);
                    HookLogger.LogSuccess($"Recorded {count} hook baselines to {path}");
                }
                catch (Exception exception)
                {
                    HookLogger.LogFailure($"Could not write hook baselines to {path}: {exception.Message}");
                }
            }
        }

        private static Dictionary<string, Dictionary<string, string>> Load()
        {
            foreach (string path in CandidatePaths())
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                try
                {
                    Dictionary<string, Dictionary<string, string>>? loaded =
                        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                    if (loaded is not null)
                    {
                        return new Dictionary<string, Dictionary<string, string>>(loaded, StringComparer.Ordinal);
                    }
                }
                catch (Exception exception)
                {
                    HookLogger.LogFailure($"Could not read hook baselines from {path}: {exception.Message}");
                }
            }
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }

        /// <summary>
        /// The file in the source tree when building from it, and the copy beside the assembly otherwise.
        /// </summary>
        private static IEnumerable<string> CandidatePaths()
        {
            string? sourcePath = typeof(HookBaselines).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == SourcePathKey)?.Value;
            if (!string.IsNullOrEmpty(sourcePath) && Directory.Exists(Path.GetDirectoryName(sourcePath)))
            {
                yield return sourcePath;
            }

            string? directory = Path.GetDirectoryName(typeof(HookBaselines).Assembly.Location);
            if (!string.IsNullOrEmpty(directory))
            {
                yield return Path.Combine(directory, FileName);
            }
        }
    }
}
