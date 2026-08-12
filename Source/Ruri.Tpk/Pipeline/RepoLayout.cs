using System.IO;

namespace Ruri.Tpk.Pipeline;

internal static class RepoLayout
{
    public static string Root { get; } = Locate();

    public static string HookSourceRoot =>
        Path.Combine(Root, "Source", "Ruri.RipperHook", "AssetRipperGameHook");

    private static string Locate()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ruri-RipperHook.slnx")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Source", "Ruri.RipperHook")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root above {AppContext.BaseDirectory} " +
            "(looking for Ruri-RipperHook.slnx next to Source/Ruri.RipperHook).");
    }
}
