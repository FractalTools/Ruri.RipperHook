using Ruri.RipperHook.Core.TypeTree;
using Ruri.Tpk.Pipeline;
using System.Reflection;

namespace Ruri.Tpk.RuriEngine;

internal static class RuriEngineTpkLane
{
    public const string Switch = "--ruriengine";
    public const string VersionKey = "engine";

    private const int ReportedFailures = 20;

    public static int Run(string[] args)
    {
        if (args.Length is < 1 or > 3)
        {
            throw new ArgumentException($"Usage: Ruri.Tpk {Switch} <player data directory> [<output.tpk>] [<unity layout version>]");
        }
        string dataDirectory = Path.GetFullPath(args[0]);
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException($"Player data directory not found: {dataDirectory}");
        }
        string outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : RepoLayout.RuriEngineTpkPath;
        string layoutVersion = args.Length > 2 ? args[2] : TypeTreeBlobWriter.LayoutVersion.ToString();

        Console.WriteLine($"[RuriEngine] data={dataDirectory}");
        Console.WriteLine($"[RuriEngine] output={outputPath}");

        System.Diagnostics.Stopwatch phase = System.Diagnostics.Stopwatch.StartNew();
        using EngineAssemblySet engine = EngineAssemblySet.Open(dataDirectory);
        Console.WriteLine($"[RuriEngine] assemblies={engine.Assemblies.Count} ({phase.ElapsedMilliseconds} ms)");

        phase.Restart();
        List<Type> candidates = new();
        List<string> skipped = new();
        foreach (Assembly assembly in engine.Assemblies)
        {
            foreach (Type type in TypesOf(assembly))
            {
                try
                {
                    if (EngineSerializationRules.IsClassCandidate(type)
                        && (EngineSerializationRules.IsPublicType(type) || EngineSerializationRules.SerializedFields(type).Count > 0))
                    {
                        candidates.Add(type);
                    }
                }
                catch (Exception exception)
                {
                    skipped.Add($"{type.FullName}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        Console.WriteLine($"[RuriEngine] candidate types={candidates.Count} unreadable={skipped.Count} ({phase.ElapsedMilliseconds} ms)");
        List<IGrouping<string, Type>> shared = candidates.GroupBy(static type => type.FullName!, StringComparer.Ordinal).Where(static group => group.Count() > 1).ToList();
        if (shared.Count > 0)
        {
            HashSet<string> sharedNames = shared.Select(static group => group.Key).ToHashSet(StringComparer.Ordinal);
            int excluded = candidates.RemoveAll(type => sharedNames.Contains(type.FullName!));
            Console.WriteLine($"[RuriEngine] excluded {excluded} types under {shared.Count} full name(s) declared by more than one assembly, which the engine component registry refuses to resolve");
            foreach (IGrouping<string, Type> group in shared.Take(ReportedFailures))
            {
                Console.WriteLine($"[RuriEngine]   {group.Key} x{group.Count()}");
            }
        }

        phase.Restart();
        EngineTypeTreeBuilder builder = new();
        builder.Build(candidates);
        Console.WriteLine($"[RuriEngine] type trees built: classes={builder.ClassCount} failed={builder.Failures.Count} ({phase.ElapsedMilliseconds} ms)");
        foreach (string failure in skipped.Concat(builder.Failures).Take(ReportedFailures))
        {
            Console.WriteLine($"[RuriEngine]   {failure}");
        }
        Console.WriteLine($"[RuriEngine] serialized fields without a type tree node: {builder.Unsupported.Values.Sum(static entry => entry.Count)} across {builder.Unsupported.Count} field type(s)");
        foreach (KeyValuePair<string, (int Count, string Example)> unsupported in builder.Unsupported.OrderByDescending(static pair => pair.Value.Count).Take(ReportedFailures))
        {
            Console.WriteLine($"[RuriEngine]   {unsupported.Key} x{unsupported.Value.Count} (first: {unsupported.Value.Example})");
        }

        LineageTpkFile.Write(builder.Blobs, CustomEngineType.RuriEngine, VersionKey, layoutVersion, outputPath);
        Console.WriteLine($"[RuriEngine] Wrote {builder.ClassCount} classes in {builder.Blobs.Count} blob(s): {new FileInfo(outputPath).Length / 1024.0 / 1024.0:F2} MB");
        return 0;
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(static type => type is not null)!;
        }
    }
}
