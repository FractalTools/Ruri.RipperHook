using AssetRipper.Primitives;
using Ruri.Tpk.Pipeline;
using System.Diagnostics;

namespace Ruri.Tpk;

internal static class Program
{
    private const string DefaultDumpRoot = @"D:\Ruri\Git\FractalTools\TypeTree";
    private const string TpkFileName = "RuriTypeTree.tpk";

    public static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (args.Length > 0 && args[0] is Unreal.UnrealTpkLane.Switch)
            {
                return Unreal.UnrealTpkLane.Run(args[1..]);
            }
            if (args.Length > 0 && args[0] is Unreal.UnrealReflectionLane.Switch)
            {
                return Unreal.UnrealReflectionLane.Run(args[1..]);
            }

            bool drift = args.Length > 0 && args[0] is "--drift";
            if (drift)
            {
                args = args[1..];
            }

            if (args.Length > 2)
            {
                throw new ArgumentException($"Usage: Ruri.Tpk [--drift] [<TypeTree dump root>] [<output path>] | Ruri.Tpk {Unreal.UnrealTpkLane.Switch} <mappings.usmap> [<output.tpk>] [<unity layout version>] | Ruri.Tpk {Unreal.UnrealReflectionLane.Switch} <game executable> [<output.usmap>]");
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
