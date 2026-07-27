using AssetRipper.Primitives;
using Ruri.AssemblyDumper.Pipeline;
using System.Diagnostics;

namespace Ruri.AssemblyDumper;

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
    private const string DefaultTypeTreeJsonDirectory = @"D:\Ruri\Git\FractalTools\TypeTree\output";
    private const string TpkFileName = "RuriTypeTree.tpk";

    public static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (args.Length > 2)
            {
                throw new ArgumentException("Usage: Ruri.AssemblyDumper [<TypeTree JSON directory>] [<output .tpk path>]");
            }

            string jsonDirectory = ResolveTypeTreeJsonDirectory(args.Length > 0 ? args[0] : DefaultTypeTreeJsonDirectory);
            string outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : DefaultOutputPath();

            Console.WriteLine($"[Build] typeTreeJsonDir={jsonDirectory}");
            Console.WriteLine($"[Build] output={outputPath}");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            TypeTreeTpkBuilder.WriteFromJsonDirectory(jsonDirectory, outputPath);

            Console.WriteLine($"[Build] Done. {new FileInfo(outputPath).Length / 1024.0 / 1024.0:F2} MB");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ruri.AssemblyDumper] FATAL: {ex}");
            return 1;
        }
        finally
        {
            sw.Stop();
            Console.WriteLine($"[Ruri.AssemblyDumper] Total: {sw.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>The tpk lands next to the hook sources so it is committed and embedded with them.</summary>
    private static string DefaultOutputPath() =>
        Path.Combine(LocateRepoRoot(), "Source", "Ruri.RipperHook", "Libraries", TpkFileName);

    private static string ResolveTypeTreeJsonDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("TypeTree JSON directory path is required.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"TypeTree JSON directory not found: {fullPath}");
        }

        if (directory.GetDirectories().Length > 0)
        {
            throw new ArgumentException($"Input must be the flat TypeTree JSON output directory, not a parent folder: {fullPath}", nameof(path));
        }

        FileInfo[] files = directory.GetFiles();
        if (files.Length == 0)
        {
            throw new ArgumentException($"TypeTree JSON directory is empty: {fullPath}", nameof(path));
        }

        foreach (FileInfo file in files)
        {
            if (!file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Input directory must contain only TypeTree version JSON files. Unexpected file: {file.FullName}", nameof(path));
            }

            if (!UnityVersion.TryParse(Path.GetFileNameWithoutExtension(file.Name), out _, out _))
            {
                throw new ArgumentException($"Input directory must contain only Unity-versioned TypeTree JSON files. Unexpected file: {file.FullName}", nameof(path));
            }
        }

        return fullPath;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(dir.FullName, "AssetRipper")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Source")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
