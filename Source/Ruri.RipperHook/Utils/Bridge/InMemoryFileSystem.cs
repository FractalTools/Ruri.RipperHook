using AssetRipper.IO.Files;
using System.Text;

namespace Ruri.RipperHook.Bridge;

/// <summary>
/// Captures AssetRipper's export output in memory instead of writing to disk. Pass an instance of this
/// in place of <see cref="LocalFileSystem.Instance"/> to
/// <see cref="AssetRipper.Export.UnityProjects.ExportHandler.Export"/> and every file the real exporter
/// would have written lands in <see cref="Files"/> instead — byte-for-byte identical to what
/// <c>ripper.exe --export</c> writes to disk today, just never touching a physical file. One instance is
/// good for exactly one export call; create a fresh one each time (its unique-name dedup state, inherited
/// from <see cref="FileSystem.GetUniqueName"/>, must not bleed across unrelated exports).
/// </summary>
public sealed class InMemoryFileSystem : FileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path the exporter wrote, virtual-path keyed, in the order first committed.</summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public override InMemoryFileImplementation File { get; }
    public override InMemoryDirectoryImplementation Directory { get; }
    public override InMemoryPathImplementation Path { get; }
    public override string TemporaryDirectory { get; set; } = "mem:/tmp";

    public InMemoryFileSystem()
    {
        File = new(this);
        Directory = new(this);
        Path = new(this);
    }

    private void Commit(string path, byte[] bytes)
    {
        _files[path] = bytes;
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            _dirs.Add(dir);
        }
    }

    public sealed class InMemoryFileImplementation(InMemoryFileSystem fileSystem) : FileImplementation(fileSystem)
    {
        public override Stream Create(string path) => new CommitStream(fileSystem, path);

        public override Stream OpenWrite(string path) => new CommitStream(fileSystem, path);

        public override Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

        public override bool Exists(string path) => fileSystem._files.ContainsKey(path);

        public override void Delete(string path) => fileSystem._files.Remove(path);

        public override byte[] ReadAllBytes(string path) =>
            fileSystem._files.TryGetValue(path, out byte[]? bytes) ? bytes : throw new FileNotFoundException(path);

        public override string ReadAllText(string path) => ReadAllText(path, new UTF8Encoding(false));

        public override string ReadAllText(string path, Encoding encoding) => encoding.GetString(ReadAllBytes(path));

        public override void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => fileSystem.Commit(path, bytes.ToArray());

        public override void WriteAllText(string path, ReadOnlySpan<char> contents) => WriteAllText(path, contents, new UTF8Encoding(false));

        public override void WriteAllText(string path, ReadOnlySpan<char> contents, Encoding encoding) =>
            fileSystem.Commit(path, encoding.GetBytes(contents.ToString()));
    }

    public sealed class InMemoryDirectoryImplementation(InMemoryFileSystem fileSystem) : DirectoryImplementation(fileSystem)
    {
        public override void Create(string path) => fileSystem._dirs.Add(path);

        public override void Delete(string path) => fileSystem._dirs.Remove(path);

        public override bool Exists(string path) => fileSystem._dirs.Contains(path);

        // Enumerate/Get-files (EnumerateFiles/GetFiles/EnumerateDirectories/GetDirectories) are an
        // export-output-listing operation real exporters don't need — they only ever Create/Exists-check
        // the directory they're about to write into. Left as the base's NotSupportedException default;
        // if a future exporter needs one, add it here once observed rather than guessing now.
    }

    public sealed class InMemoryPathImplementation(InMemoryFileSystem fileSystem) : PathImplementation(fileSystem)
    {
        // Virtual paths are opaque dictionary keys, already "full" by construction.
        public override string GetFullPath(string path) => path;

        public override bool IsPathRooted(ReadOnlySpan<char> path) => true;
    }

    /// <summary>A Create/OpenWrite target that commits its buffer into the owning filesystem on Dispose.</summary>
    private sealed class CommitStream(InMemoryFileSystem owner, string path) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                owner.Commit(path, ToArray());
            }
            base.Dispose(disposing);
        }
    }
}
