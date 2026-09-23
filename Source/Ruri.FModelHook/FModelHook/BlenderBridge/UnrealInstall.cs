using CUE4Parse.UE4.Versions;
using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>
/// What a packaged Unreal install looks like on disk, read off the install and nothing else:
/// the archive folders (every <c>Paks</c> directory holding .pak/.utoc containers), the project
/// folder those sit under, and the engine version the build literal compiled into the game
/// executable states. No name of any game appears here.
/// </summary>
public static class UnrealInstall
{
    private const string PaksFolderName = "Paks";
    private const string ContentFolderName = "Content";
    private const string BinariesFolderName = "Binaries";
    private const int SearchDepth = 4;
    private const int ScanChunkSize = 1 << 24;
    private const int ScanOverlap = 256;
    private static readonly byte[] LiteralNeedle = Encoding.Unicode.GetBytes("++UE");
    private static readonly Regex LiteralPattern = new(@"^\+\+UE(?<engine>\d+)\+Release-(?<major>\d+)\.(?<minor>\d+)(?!\d)", RegexOptions.CultureInvariant);

    public static readonly string[] ArchiveExtensions = [".pak", ".utoc", ".ucas"];

    public static bool IsArchive(string path)
    {
        foreach (string extension in ArchiveExtensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Every archive folder under the install, shallowest first.</summary>
    public static string[] PakFolders(string gameRoot)
    {
        List<string> found = new();
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return [];
        }
        Walk(gameRoot, 0, found);
        found.Sort(static (left, right) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right));
        return found.ToArray();
    }

    private static void Walk(string directory, int depth, List<string> found)
    {
        if (Path.GetFileName(directory).Equals(PaksFolderName, StringComparison.OrdinalIgnoreCase) && HoldsArchives(directory))
        {
            found.Add(directory);
            return;
        }
        if (depth >= SearchDepth)
        {
            return;
        }
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        foreach (string child in children)
        {
            Walk(child, depth + 1, found);
        }
    }

    private static bool HoldsArchives(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (IsArchive(file))
            {
                return true;
            }
        }
        return false;
    }

    public static string[] ContentRoots(string gameRoot) => PakFolders(gameRoot);

    /// <summary>The project folder an archive folder belongs to (<c>Project/Content/Paks</c>), or null.</summary>
    public static string? ProjectFolder(string pakFolder)
    {
        string? content = Path.GetDirectoryName(pakFolder);
        if (content is null || !Path.GetFileName(content).Equals(ContentFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Path.GetDirectoryName(content);
    }

    /// <summary>
    /// The engine version the project's largest executable states, as <c>major.minor</c>. Every
    /// Unreal binary carries, compiled in from the engine's Build.version, the branch and build
    /// version literals (<c>++UE5+Release-5.1</c>, <c>++UE5+Release-5.1-CL-23901901</c>, stored
    /// as UTF-16) that the engine itself reports as its version. The version resource a packaged
    /// executable may also carry is stamped from the same file, is absent from many shipped
    /// builds and is a studio's to restate, so it is not read. Empty for a build on a renamed
    /// engine branch, whose executable carries no such literal.
    /// </summary>
    public static string EngineVersion(string pakFolder)
    {
        string? executable = LargestExecutable(pakFolder);
        return executable is null ? string.Empty : BuildVersionLiteral(executable);
    }

    /// <summary>
    /// The engine version a container dialect states, for a build whose executable carries no
    /// literal to read. The EGame value IS that version: CUE4Parse composes every member from a
    /// major base and a minor shifted into place (<c>GameUe5Base + (4 &lt;&lt; 16)</c>), and a
    /// studio's own member is that base plus an offset, so InfinityNikki reads 5.4 and
    /// WutheringWaves 4.26 with no table for anyone to keep up to date.
    /// </summary>
    public static string EngineVersion(EGame game) =>
        $"{(int)game >> 24}.{((int)game >> 16) & 0xFF}";

    private static string? LargestExecutable(string pakFolder)
    {
        string? project = ProjectFolder(pakFolder);
        if (project is null)
        {
            return null;
        }
        string binaries = Path.Combine(project, BinariesFolderName);
        if (!Directory.Exists(binaries))
        {
            return null;
        }
        string? best = null;
        long bestSize = -1;
        foreach (string executable in Directory.EnumerateFiles(binaries, "*.exe", SearchOption.AllDirectories))
        {
            long size = new FileInfo(executable).Length;
            if (size > bestSize)
            {
                bestSize = size;
                best = executable;
            }
        }
        return best;
    }

    /// <summary>
    /// The <c>major.minor</c> of the first build version literal in the executable, found by
    /// scanning its bytes for the UTF-16 <c>++UE</c> the literal opens with. The file is read in
    /// windows that overlap by more than a literal is long, so no literal straddling two windows
    /// is missed, and a hit is only read where that much follows it.
    /// </summary>
    private static string BuildVersionLiteral(string executable)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ScanChunkSize + ScanOverlap);
        try
        {
            using FileStream stream = new(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            int carried = 0;
            while (true)
            {
                int read = Fill(stream, buffer, carried, ScanChunkSize);
                int length = carried + read;
                bool last = read < ScanChunkSize;
                int searchable = last ? length : length - ScanOverlap;
                int start = 0;
                while (start < searchable)
                {
                    int hit = buffer.AsSpan(start, searchable - start).IndexOf(LiteralNeedle);
                    if (hit < 0)
                    {
                        break;
                    }
                    int position = start + hit;
                    int count = Math.Min(ScanOverlap, length - position) & ~1;
                    Match match = LiteralPattern.Match(Encoding.Unicode.GetString(buffer, position, count));
                    if (match.Success)
                    {
                        return match.Groups["major"].Value + "." + match.Groups["minor"].Value;
                    }
                    start = position + 2;
                }
                if (last)
                {
                    return string.Empty;
                }
                Buffer.BlockCopy(buffer, length - ScanOverlap, buffer, 0, ScanOverlap);
                carried = ScanOverlap;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int Fill(FileStream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    /// <summary>
    /// The CUE4Parse game enum a stock engine version maps to: the <c>GAME_UE{major}_{minor}</c>
    /// member when one exists, else the latest member of that major.
    /// </summary>
    public static EGame? EngineFromVersion(string engineVersion)
    {
        if (string.IsNullOrWhiteSpace(engineVersion))
        {
            return null;
        }
        string[] parts = engineVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
        {
            return null;
        }
        if (Enum.TryParse($"GAME_UE{major}_{minor}", out EGame exact))
        {
            return exact;
        }
        return Enum.TryParse($"GAME_UE{major}_LATEST", out EGame latest) ? latest : null;
    }
}
