using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// The virtual folder tree a cabmap browses as: every row filed under each container path
/// it carries, which is the game's own addressable naming and so a real filesystem path.
/// Built once per map in O(total path segments) and then read in O(children of one folder),
/// never by rescanning the rows.
/// </summary>
public sealed class CabFolders
{
    /// <summary>Where a row with no container path at all is filed, so it stays reachable
    /// instead of vanishing. It is a FOLDER keyed by the row's own cab, not a leaf, because
    /// a childless node reads as a file.</summary>
    public const string NoPathBucket = "(no virtual path)";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CabTable, CabFolders> Built = new();

    private readonly Node _root = new();

    private sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public readonly List<int> Files = [];
        public int FileCount;
    }

    public static CabFolders Of(CabTable table) => Built.GetValue(table, Build);

    private static CabFolders Build(CabTable table)
    {
        CabFolders folders = new();
        List<string> segments = [];
        for (int id = 0; id < table.Count; id++)
        {
            bool placed = false;
            int paths = table.ContainerPathCount(id);
            for (int path = 0; path < paths; path++)
            {
                Split(table.ContainerPath(id, path), segments);
                if (segments.Count == 0)
                {
                    continue;
                }
                folders.Add(segments, id);
                placed = true;
            }
            if (placed)
            {
                continue;
            }
            segments.Clear();
            segments.Add(NoPathBucket);
            segments.Add(table.CabName(id));
            folders.Add(segments, id);
        }
        return folders;
    }

    private static void Split(string path, List<string> into)
    {
        into.Clear();
        int at = 0;
        while (at < path.Length)
        {
            int next = path.IndexOf('/', at);
            if (next < 0)
            {
                next = path.Length;
            }
            if (next > at)
            {
                into.Add(path[at..next]);
            }
            at = next + 1;
        }
    }

    private void Add(List<string> segments, int id)
    {
        Node node = _root;
        foreach (string segment in segments)
        {
            if (!node.Children.TryGetValue(segment, out Node? child))
            {
                child = new Node();
                node.Children[segment] = child;
            }
            node = child;
            node.FileCount++;
        }
        node.Files.Add(id);
    }

    private Node? At(IReadOnlyList<string> path)
    {
        Node node = _root;
        foreach (string segment in path)
        {
            if (!node.Children.TryGetValue(segment, out Node? child))
            {
                return null;
            }
            node = child;
        }
        return node;
    }

    /// <summary>Whether this path still exists in THIS map -- what lets a browser restore the
    /// folder a user was last in without stranding them when it is a different game.</summary>
    public bool Has(IReadOnlyList<string> path) => At(path) is not null;

    /// <summary>One folder's own child folders, alphabetically, with how many rows live at or
    /// below each. A child that is only ever a leaf is a file, not a folder.</summary>
    public (string[] Names, int[] Counts) Children(IReadOnlyList<string> path)
    {
        Node? node = At(path);
        if (node is null)
        {
            return ([], []);
        }
        List<(string Name, int Count)> found = [];
        foreach ((string name, Node child) in node.Children)
        {
            if (child.Children.Count != 0)
            {
                found.Add((name, child.FileCount));
            }
        }
        found.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return (found.Select(entry => entry.Name).ToArray(), found.Select(entry => entry.Count).ToArray());
    }

    /// <summary>The rows whose container path ends exactly at one of this folder's children --
    /// what the browser lists as files.</summary>
    public int[] Files(IReadOnlyList<string> path)
    {
        Node? node = At(path);
        if (node is null)
        {
            return [];
        }
        List<int> files = [];
        foreach (Node child in node.Children.Values)
        {
            files.AddRange(child.Files);
        }
        return files.ToArray();
    }

    /// <summary>The folder one row's path lands in -- mirroring how it was PLACED, including the
    /// bucket a row with no path falls into, so "jump to this row's folder" always lands where
    /// the browser would already be showing it.</summary>
    public static string[] FolderOf(CabTable table, int id, int pathIndex)
    {
        int paths = table.ContainerPathCount(id);
        if (paths == 0)
        {
            return [NoPathBucket];
        }
        List<string> segments = [];
        Split(table.ContainerPath(id, Math.Clamp(pathIndex, 0, paths - 1)), segments);
        return segments.Count == 0 ? [] : segments[..^1].ToArray();
    }

    /// <summary>WHICH of a multi-path row's paths a jump should target: the one it is currently
    /// being shown under (folder view), or the one it actually matched on (search view). A row
    /// with one path always answers 0, which is nearly every row.</summary>
    public static int BestPathIndex(CabTable table, int id, string query, string[] currentDir)
    {
        int paths = table.ContainerPathCount(id);
        if (paths <= 1)
        {
            return 0;
        }
        string needle = (query ?? string.Empty).Trim();
        List<string> segments = [];
        if (needle.Length == 0)
        {
            for (int path = 0; path < paths; path++)
            {
                Split(table.ContainerPath(id, path), segments);
                if (segments.Count == currentDir.Length + 1
                    && segments.Take(currentDir.Length).SequenceEqual(currentDir, StringComparer.Ordinal))
                {
                    return path;
                }
            }
            return 0;
        }
        for (int path = 0; path < paths; path++)
        {
            if (table.ContainerPath(id, path).Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }
        return 0;
    }

    /// <summary>The name one row is drawn as UNDER a given folder. Matters only for the rare row
    /// with more than one container path: its default name is path 0's leaf, which can belong to
    /// a completely different folder than the one it is being shown in.</summary>
    public static string LeafName(CabTable table, int id, string[] currentDir)
    {
        List<string> segments = [];
        for (int path = 0; path < table.ContainerPathCount(id); path++)
        {
            Split(table.ContainerPath(id, path), segments);
            if (segments.Count == currentDir.Length + 1
                && segments.Take(currentDir.Length).SequenceEqual(currentDir, StringComparer.Ordinal))
            {
                return segments[^1];
            }
        }
        return Name(table, id);
    }

    /// <summary>A row's default display name: the leaf of its first container path, saying how
    /// many other names it also answers to.</summary>
    public static string Name(CabTable table, int id)
    {
        ArrayBufferWriter<byte> name = new(64);
        WriteName(table, id, name);
        return Encoding.UTF8.GetString(name.WrittenSpan);
    }

    /// <summary><see cref="Name"/>, written as UTF-8 -- what a list of millions of rows is built
    /// from without a string per row.</summary>
    public static void WriteName(CabTable table, int id, IBufferWriter<byte> into)
    {
        int paths = table.ContainerPathCount(id);
        if (paths == 0)
        {
            return;
        }
        ReadOnlySpan<byte> first = table.ContainerPathUtf8(id, 0);
        int slash = first.LastIndexOf((byte)'/');
        into.Write(slash < 0 ? first : first[(slash + 1)..]);
        if (paths > 1)
        {
            Utf8.TryWrite(into.GetSpan(MaxSuffixBytes), CultureInfo.InvariantCulture, $" (+{paths - 1})", out int written);
            into.Advance(written);
        }
    }

    /// <summary>The longest " (+N)" an int can make.</summary>
    private const int MaxSuffixBytes = 16;

    public static string[] Segments(string path)
    {
        List<string> segments = [];
        Split(path ?? string.Empty, segments);
        return segments.ToArray();
    }

    public static string Joined(IReadOnlyList<string> path) => string.Join('/', path);

    internal static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);
}
