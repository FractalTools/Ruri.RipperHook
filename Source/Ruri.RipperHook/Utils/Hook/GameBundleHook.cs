using System.Reflection;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.GameBundleHook;

public class GameBundleHook : CommonHook, IHookModule
{
    private static readonly MethodInfo FromSerializedFile = typeof(SerializedAssetCollection)
        .GetMethod("FromSerializedFile", ReflectionExtensions.PrivateStaticBindFlag());

    public delegate void FilePreInitializeDelegate(GameBundle _this, IEnumerable<string> paths,
        List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider);

    /// <summary>
    /// Optional scan-mode filter consulted by the VFS chunk extractor: when set, a chunk's inner file is
    /// only extracted (and decrypted) if this returns <c>true</c> for its name. Building a CAB map only
    /// needs the AssetBundles that host SerializedFiles/CABs — skipping the bulk resource payloads
    /// (video, audio, tables, streaming data) the extractor would otherwise ChaCha-decrypt is what makes
    /// the map build fast. <c>null</c> (the default) extracts everything, so normal loading/export is
    /// completely unaffected. Set it for the duration of a scan, then reset to <c>null</c>.
    /// </summary>
    public static Func<string, bool>? ScanIncludeFile;

    /// <summary>
    /// Default <see cref="ScanIncludeFile"/> predicate for CAB-map scanning: keep AssetBundles and
    /// standalone Unity SerializedFiles, skip everything else (a pure resource payload never hosts a CAB,
    /// so there is nothing to index in it). Erring toward keeping is safe — an unwanted file just parses
    /// to a ResourceFile and adds no CAB — so the few standard SerializedFile name prefixes are included
    /// as a hedge in case a game ever ships assets unbundled.
    /// </summary>
    public static bool CabScanIncludeFile(string name)
    {
        string n = name.Replace('\\', '/');
        if (n.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("/bundles/", StringComparison.OrdinalIgnoreCase)) return true;
        string leaf = n.Contains('/') ? n[(n.LastIndexOf('/') + 1)..] : n;
        return leaf.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("level", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Set by a VFS game hook: given an on-disk path, decrypt + parse JUST the SerializedFile metadata of
    /// every CAB-hosting bundle the path contains, and return one tuple per SerializedFile — releasing each
    /// bundle's bytes as it goes (bounded memory) and extracting in parallel. This is the fast path the CAB
    /// map builder prefers; <c>null</c> when no VFS hook is active (the builder then falls back to a generic
    /// per-file scheme read).
    /// </summary>
    public delegate List<(string Cab, List<string> Deps, List<int> ClassIds)> ScanChunkDelegate(string path);
    public static ScanChunkDelegate? ScanChunk;

    /// <summary>
    /// Project one SerializedFile's metadata into a CAB-map tuple: its CAB name (its fixed name, or
    /// <paramref name="fallbackName"/> when unnamed), the distinct dependency CAB names it references, and
    /// the distinct ClassIDs from its type table (a MonoBehaviour's negative script-type index maps to 114).
    /// Reads metadata only — never touches a single object's data.
    /// </summary>
    public static (string Cab, List<string> Deps, List<int> ClassIds) ReadSerializedMetadata(SerializedFile sf, string fallbackName)
    {
        string cab = string.IsNullOrWhiteSpace(sf.NameFixed) ? fallbackName : sf.NameFixed;
        List<string> deps = new();
        foreach (FileIdentifier dependency in sf.Dependencies)
        {
            string name = dependency.GetFilePath();
            if (!string.IsNullOrWhiteSpace(name) && !deps.Contains(name, StringComparer.OrdinalIgnoreCase))
                deps.Add(name);
        }
        HashSet<int> classIds = new();
        foreach (SerializedType type in sf.Types)
            classIds.Add(type.TypeID < 0 ? 114 : type.TypeID);
        return (cab, deps, classIds.ToList());
    }

    // Static callback used by the hooked method
    public static FilePreInitializeDelegate CustomFilePreInitialize;

    // Instance callback stored until activation
    private readonly FilePreInitializeDelegate _moduleCallback;

    public GameBundleHook(FilePreInitializeDelegate callback)
    {
        _moduleCallback = callback;
    }

    public void OnApply()
    {
        CustomFilePreInitialize = _moduleCallback;
    }

    [RetargetMethod(typeof(GameBundle), "InitializeFromPaths")]
    public void InitializeFromPaths(IEnumerable<string> paths, AssetFactoryBase assetFactory, FileSystem fileSystem, IGameInitializer? initializer)
    {
        var _this = (object)this as GameBundle;

        _this.ResourceProvider = initializer?.ResourceProvider;
        var fileStack = new List<FileBase>();
        UnityVersion defaultVersion = initializer is null ? default : initializer.DefaultVersion;

        CustomFilePreInitialize(_this, paths, fileStack, fileSystem, initializer?.DependencyProvider);

        while (fileStack.Count > 0)
        {
            switch (RemoveLastItem(fileStack))
            {
                case SerializedFile serializedFile:
                    FromSerializedFile.Invoke(null, new object[] { _this, serializedFile, assetFactory, defaultVersion });
                    break;
                case FileContainer container:
                    var serializedBundle = SerializedBundle.FromFileContainer(container, assetFactory, defaultVersion);
                    _this.AddBundle(serializedBundle);
                    break;
                case ResourceFile resourceFile:
                    _this.AddResource(resourceFile);
                    break;
                case FailedFile failedFile:
                    _this.AddFailed(failedFile);
                    break;
            }
        }
    }

    private static FileBase RemoveLastItem(List<FileBase> list)
    {
        var index = list.Count - 1;
        var file = list[index];
        list.RemoveAt(index);
        return file;
    }

    // Static Helper (unchanged)
    public static List<FileBase> LoadFilesAndDependencies(byte[] buffer, string path, string name, IDependencyProvider? dependencyProvider)
    {
        List<FileBase> files = new();
        HashSet<string> serializedFileNames = new();

        var file = SchemeReader.ReadFile(buffer, path, name);

        try
        {
            file?.ReadContentsRecursively();
        }
        catch (Exception ex)
        {
            file = new FailedFile()
            {
                Name = name,
                FilePath = path,
                StackTrace = ex.ToString(),
            };
        }

        while (file is CompressedFile compressedFile)
            file = compressedFile.UncompressedFile;

        if (file is ResourceFile || file is FailedFile)
        {
            files.Add(file);
        }
        else if (file is SerializedFile serializedFile)
        {
            files.Add(file);
            serializedFileNames.Add(serializedFile.NameFixed);
        }
        else if (file is FileContainer container)
        {
            files.Add(file);
            foreach (var serializedFileInContainer in container.FetchSerializedFiles())
                serializedFileNames.Add(serializedFileInContainer.NameFixed);
        }

        for (var i = 0; i < files.Count; i++)
        {
            var file1 = files[i];
            if (file1 is SerializedFile serializedFile)
                LoadDependencies(serializedFile, files, serializedFileNames, dependencyProvider);
            else if (file1 is FileContainer container)
                foreach (var serializedFileInContainer in container.FetchSerializedFiles())
                    LoadDependencies(serializedFileInContainer, files, serializedFileNames, dependencyProvider);
        }

        return files;
    }

    private static void LoadDependencies(SerializedFile serializedFile, List<FileBase> files, HashSet<string> serializedFileNames, IDependencyProvider? dependencyProvider)
    {
        foreach (var fileIdentifier in serializedFile.Dependencies)
        {
            var name = fileIdentifier.GetFilePath();
            if (serializedFileNames.Add(name) && dependencyProvider?.FindDependency(fileIdentifier) is { } dependency)
                files.Add(dependency);
        }
    }
}