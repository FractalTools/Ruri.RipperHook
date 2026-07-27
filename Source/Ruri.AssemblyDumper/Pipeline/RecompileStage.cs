using System.Diagnostics;

namespace Ruri.AssemblyDumper.Pipeline;

/// <summary>
/// 收尾两步：先用 AR.AssemblyDumper.Recompiler 反编译生成的 dll 到 Source/Ruri.SourceGenerated 项目树，
/// 再 dotnet build 那个项目（其 &lt;CopyAfterBuild&gt; 已经把 dll 同步到 Source/Ruri.RipperHook/Libraries）。
/// </summary>
internal static class RecompileStage
{
    public static int Decompile(string emittedDllPath, string outputProjectDir, string version)
    {
        string repoRoot = LocateRepoRoot();
        string recompilerProj = Path.Combine(repoRoot, "AssetRipper", "Source", "AssetRipper.AssemblyDumper.Recompiler",
            "AssetRipper.AssemblyDumper.Recompiler.csproj");
        if (!File.Exists(recompilerProj)) throw new FileNotFoundException("Recompiler csproj not found", recompilerProj);
        if (!File.Exists(emittedDllPath)) throw new FileNotFoundException("Emitted dll not found", emittedDllPath);

        Directory.CreateDirectory(outputProjectDir);
        return Run("dotnet",
            $"run --project \"{recompilerProj}\" -c Release -- \"{emittedDllPath}\" \"{outputProjectDir}\" \"{version}\"");
    }

    public static int Build(string ruriSourceGeneratedCsprojPath)
    {
        if (!File.Exists(ruriSourceGeneratedCsprojPath))
            throw new FileNotFoundException("Ruri.SourceGenerated.csproj not found", ruriSourceGeneratedCsprojPath);
        return Run("dotnet", $"build \"{ruriSourceGeneratedCsprojPath}\" -c Release");
    }

    private static int Run(string fileName, string args)
    {
        Console.WriteLine($"[Recompile] {fileName} {args}");
        var psi = new ProcessStartInfo(fileName, args) { UseShellExecute = false };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(dir.FullName, "AssetRipper")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Source")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
