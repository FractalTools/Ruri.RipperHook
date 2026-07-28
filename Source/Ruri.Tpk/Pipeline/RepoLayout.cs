using System.IO;

namespace Ruri.Tpk.Pipeline;

/// <summary>
/// Where the repository is, resolved from markers that actually exist at its root.
///
/// This used to be duplicated per call site and keyed on a <c>Directory.Build.props</c> beside
/// <c>AssetRipper/</c> and <c>Source/</c> -- a combination that is never true here, because the props
/// files live one level down in each sub-tree. Every caller therefore fell through to a hard-coded
/// "five parents up", which happened to land correctly for the packer and silently did not for
/// anything with a different output depth. Marker-based resolution belongs in one place, and the
/// markers have to be things the root really has.
/// </summary>
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
