using AssetRipper.Primitives;
using Ruri.Tpk.Pipeline;
using System.Diagnostics;

namespace Ruri.Tpk;

/// <summary>
/// Packs the external TypeTree JSON dumps into the single <c>RuriTypeTree.tpk</c> that
/// <c>Ruri.RipperHook</c> embeds and interprets at runtime.
///
/// This used to be a full code generator: build a tpk, run the AssetRipper assembly dumper's 60+
/// passes over it, rename the emitted assembly to <c>Ruri.SourceGenerated</c>, decompile it, rebuild
/// it, and deploy a 53 MB DLL whose only job was to hold one <c>ReadRelease</c> per (class, engine
/// version). <c>Ruri.RipperHook.Core.TypeTree</c> now reads those same trees directly, so the tpk is
/// the whole deliverable and everything downstream of it is gone.
///
/// Input is the flat output directory produced by <c>TypeTree/RazTreeConverter.py</c>: one
/// <c>&lt;unityVersion&gt;.json</c> per engine build, where a custom engine encodes itself as
/// <c>2021.3.527x5</c> (type <c>x</c> = experimental, number 5 = <c>CustomEngineType.EndField</c>).
/// </summary>
internal static class Program
{
    private const string DefaultDumpRoot = @"D:\Ruri\Git\FractalTools\TypeTree";
    private const string TpkFileName = "RuriTypeTree.tpk";

    public static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // --drift does not pack anything: it diffs the same dumps against the closest OFFICIAL
            // Unity trees (fetched from AssetRipper/TypeTreeDumps into memory, never cached to disk)
            // and reports every place the fork deviates -- i.e. every place a hook is or should be.
            bool drift = args.Length > 0 && args[0] is "--drift";
            if (drift)
            {
                args = args[1..];
            }

            if (args.Length > 2)
            {
                throw new ArgumentException("Usage: Ruri.Tpk [--drift] [<TypeTree dump root>] [<output path>]");
            }

            string dumpRoot = ResolveDumpRoot(args.Length > 0 ? args[0] : DefaultDumpRoot);

            if (drift)
            {
                string reportDirectory = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(AppContext.BaseDirectory, "drift");
                Console.WriteLine($"[Drift] dumpRoot={dumpRoot}");
                Console.WriteLine($"[Drift] output={reportDirectory}");
                TypeTreeDrift.Run(dumpRoot, reportDirectory);
                return 0;
            }

            string outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : DefaultOutputPath();

            Console.WriteLine($"[Build] dumpRoot={dumpRoot}");
            Console.WriteLine($"[Build] output={outputPath}");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            TypeTreeTpkBuilder.WriteFromDumpRoot(dumpRoot, outputPath);

            Console.WriteLine($"[Build] Done. {new FileInfo(outputPath).Length / 1024.0 / 1024.0:F2} MB");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ruri.Tpk] FATAL: {ex}");
            return 1;
        }
        finally
        {
            sw.Stop();
            Console.WriteLine($"[Ruri.Tpk] Total: {sw.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>
    /// The tpk lands inside the game hook submodule, alongside the hooks whose layouts it describes,
    /// so it is versioned and embedded with them rather than sitting loose in the core project.
    /// </summary>
    private static string DefaultOutputPath() =>
        Path.Combine(RepoLayout.HookSourceRoot, "TypeTree", TpkFileName);

    private static string ResolveDumpRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("TypeTree dump root is required.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"TypeTree dump root not found: {fullPath}");
        }

        return fullPath;
    }
}
