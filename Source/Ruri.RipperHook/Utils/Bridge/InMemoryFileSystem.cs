using AssetRipper.IO.Files;
using System.Text;

namespace Ruri.RipperHook.Bridge;

public sealed class InMemoryFileSystem : FileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastCommitTicks;

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public List<(string Path, long Bytes, double Ms)> CommitTimeline { get; } = new();

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
        long now = _clock.ElapsedTicks;
        CommitTimeline.Add((path, bytes.LongLength,
            (now - _lastCommitTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
        _lastCommitTicks = now;
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

    }

    public sealed class InMemoryPathImplementation(InMemoryFileSystem fileSystem) : PathImplementation(fileSystem)
    {
        public override string GetFullPath(string path) => path;

        public override bool IsPathRooted(ReadOnlySpan<char> path) => true;
    }

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
