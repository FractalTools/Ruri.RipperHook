using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// Auto-discover UE source trees under a single root (default
// `D:\GameStudy\UE`). Each first-level subdir is one engine version —
// the version string is extracted from the folder name with a flexible
// pattern, so the user-downloaded raw names like `UnrealEngine-5.4.4-release`
// don't have to be renamed. Patterns recognised:
//
//   * `UnrealEngine-<X>.<Y>.<Z>-release`
//   * `UnrealEngine-<X>.<Y>-release`
//   * `UE-<X>.<Y>.<Z>`
//   * Anything matching `<X>.<Y>(.<Z>)?` somewhere in the leaf name
//
// Once a version is parsed we point at `Engine/Source` to walk the
// scanner roots (Runtime/Developer/Editor/Plugins).
public sealed record UeVersion(int Major, int Minor, int Patch)
{
    public override string ToString() => Patch == 0 ? $"{Major}.{Minor}" : $"{Major}.{Minor}.{Patch}";
}

public sealed record DiscoveredEngine(string RootDir, UeVersion Version, string OriginalFolderName);

public static class UeSourceScanner
{
    private static readonly Regex s_versionPattern = new(
        @"(?<x>\d+)\.(?<y>\d+)(?:\.(?<z>\d+))?",
        RegexOptions.Compiled);

    public static IEnumerable<DiscoveredEngine> DiscoverEngines(string rootDir)
    {
        if (!Directory.Exists(rootDir)) yield break;
        foreach (string dir in Directory.EnumerateDirectories(rootDir))
        {
            string leaf = Path.GetFileName(dir);
            UeVersion? ver = TryParseVersion(leaf);
            if (ver == null) continue;
            // Sanity check: must contain Engine/Source.
            if (!Directory.Exists(Path.Combine(dir, "Engine", "Source"))) continue;
            yield return new DiscoveredEngine(dir, ver, leaf);
        }
    }

    public static UeVersion? TryParseVersion(string folderName)
    {
        Match m = s_versionPattern.Match(folderName);
        if (!m.Success) return null;
        int x = int.Parse(m.Groups["x"].Value);
        int y = int.Parse(m.Groups["y"].Value);
        int z = m.Groups["z"].Success ? int.Parse(m.Groups["z"].Value) : 0;
        // Reject impossibly low/high values to avoid matching arbitrary digit
        // pairs in non-version folder names.
        if (x < 4 || x > 9 || y > 99) return null;
        return new UeVersion(x, y, z);
    }

    // The four UE source roots that actually ship FShader / FRHI*Layout*
    // declarations. Anything else (ThirdParty, Programs/Build, etc.) is
    // skipped because it never contains material/shader-binding code.
    private static readonly string[] s_relativeRoots =
    {
        "Engine/Source/Runtime",
        "Engine/Source/Developer",
        "Engine/Source/Editor",
        "Engine/Plugins",
    };

    private static readonly string[] s_extensions = { ".h", ".cpp", ".inl" };

    public static IEnumerable<string> EnumerateSourceFiles(string ueRoot)
    {
        foreach (string rel in s_relativeRoots)
        {
            string abs = Path.Combine(ueRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(abs)) continue;
            foreach (string file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                for (int i = 0; i < s_extensions.Length; i++)
                {
                    if (string.Equals(ext, s_extensions[i], StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                        break;
                    }
                }
            }
        }
    }

    // Strip `// ...` and `/* ... */` so subsequent regex passes don't trip
    // over macro-shaped text inside doc comments. Block comments use
    // Singleline so they can span lines.
    private static readonly Regex s_blockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex s_lineComment = new(@"//[^\n]*", RegexOptions.Compiled);

    public static string StripComments(string text)
    {
        text = s_blockComment.Replace(text, " ");
        text = s_lineComment.Replace(text, " ");
        return text;
    }
}
