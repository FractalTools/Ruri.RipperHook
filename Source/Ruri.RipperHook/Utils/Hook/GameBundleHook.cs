using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_142;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.GameBundleHook;

public class GameBundleHook : CommonHook, IHookModule
{
    private static readonly MethodInfo FromSerializedFile = typeof(SerializedAssetCollection)
        .GetMethod("FromSerializedFile", ReflectionExtensions.PrivateStaticBindFlag());

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SubscribeToGameHookTeardown()
    {
        Ruri.Hook.RuriHook.GameHookRemoved += ResetGameState;
    }

    public delegate void FilePreInitializeDelegate(GameBundle _this, IEnumerable<string> paths,
        List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider);

    public static Func<string, bool>? ScanIncludeFile;

    public static bool CabScanIncludeFile(string name)
    {
        ReadOnlySpan<char> n = name;
        if (n.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)) return true;
        if (HasBundlesSegment(n)) return true;
        int cut = n.LastIndexOfAny('/', '\\');
        ReadOnlySpan<char> leaf = cut >= 0 ? n[(cut + 1)..] : n;
        if (leaf.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return leaf.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("level", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("resources.assets", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".assets", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("mainData", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasBundlesSegment(ReadOnlySpan<char> name)
    {
        int from = 0;
        while (true)
        {
            int index = name[from..].IndexOf("bundles", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }
            int start = from + index;
            int end = start + "bundles".Length;
            if (start > 0 && name[start - 1] is '/' or '\\' && end < name.Length && name[end] is '/' or '\\')
            {
                return true;
            }
            from = start + 1;
        }
    }

    public static Func<string, bool>? LoadIncludeFile;


    public delegate IEnumerable<(string FileName, long FileNameHash, string BlockType, long Length, string ChkPath)> EnumerateVfsFilesDelegate(string[] vfsRoots, string[]? blockTypeFilter);
    public static EnumerateVfsFilesDelegate? EnumerateVfsFiles;

    public delegate byte[] ExtractVfsFileDelegate(string[] vfsRoots, string fileName);
    public static ExtractVfsFileDelegate? ExtractVfsFile;



    public delegate List<(string Cab, List<string> Deps, List<int> ClassIds)> ScanChunkDelegate(string path);
    public static ScanChunkDelegate? ScanChunk;

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


    public static UnityVersion NameScanVersion;

    public delegate List<(string Cab, string FileName, List<string> Paths)> ScanChunkNamesDelegate(string path);
    public static ScanChunkNamesDelegate? ScanChunkNames;

    public delegate List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)> ScanChunkFullDelegate(string path);
    public static ScanChunkFullDelegate? ScanChunkFull;

    public static (string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths) ReadFullMetadata(SerializedFile sf, string fallbackName)
    {
        (string cab, List<string> deps, List<int> classIds) = ReadSerializedMetadata(sf, fallbackName);
        (_, _, List<string> paths) = ReadContainerNames(sf, fallbackName);
        return (cab, fallbackName, deps, classIds, paths);
    }

    public const string AssetRowSeparator = "::";

    public static List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)> ReadFullMetadataRows(SerializedFile sf, string fallbackName)
    {
        (string cab, string fileName, List<string> deps, List<int> classIds, List<string> paths) =
            ReadFullMetadata(sf, fallbackName);
        List<(string, string, List<string>, List<int>, List<string>)> rows = new()
        {
            (cab, fileName, deps, classIds, paths),
        };
        if (paths.Count > 0)
        {
            return rows;        }
        foreach ((long pathId, int classId, string name) in HarvestAssetNames(sf))
        {
            rows.Add(($"{cab}{AssetRowSeparator}{pathId}", fileName,
                new List<string> { cab }, new List<int> { classId }, new List<string> { name }));
        }
        return rows;
    }


    public static List<(long PathId, int ClassId, string Name)> HarvestAssetNames(SerializedFile sf)
    {
        List<(long, int, string)> assets = new();
        bool bigEndian = sf.EndianType == EndianType.BigEndian;
        int pathIdSize = ObjectInfo.IsLongID(sf.Generation) ? 8 : 4;
        int pptrSize = sizeof(int) + pathIdSize;
        int componentEntrySize = sf.Version.GreaterThanOrEquals(5, 5) ? pptrSize : sizeof(int) + pptrSize;

        foreach (ObjectInfo objectInfo in sf.Objects)
        {
            ReadOnlySpan<byte> data = objectInfo.ObjectData;
            int classId = objectInfo.TypeID < 0 ? (int)ClassIDType.MonoBehaviour : objectInfo.TypeID;
            int offset;
            switch (classId)
            {
                case (int)ClassIDType.GameObject:
                {
                    if (data.Length < sizeof(int))
                    {
                        continue;
                    }
                    int count = ReadInt32(data, 0, bigEndian);
                    long afterArray = sizeof(int) + (long)count * componentEntrySize;
                    if (count < 0 || count > 0x10000 || afterArray + sizeof(int) >= data.Length)
                    {
                        continue;
                    }
                    offset = (int)afterArray + sizeof(int);                    break;
                }
                case (int)ClassIDType.MonoBehaviour:
                    offset = pptrSize + sizeof(int) + pptrSize;
                    break;
                default:
                    offset = 0;                    break;
            }

            string? name = TryReadAlignedString(data, offset, bigEndian);
            if (name is not null)
            {
                assets.Add((objectInfo.FileID, classId, name));
            }
        }
        return assets;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian) => bigEndian
        ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[offset..])
        : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    private static string? TryReadAlignedString(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if (offset < 0 || offset + sizeof(int) > data.Length)
        {
            return null;
        }
        int length = ReadInt32(data, offset, bigEndian);
        if (length <= 0 || length > 255 || offset + sizeof(int) + length > data.Length)
        {
            return null;
        }
        ReadOnlySpan<byte> bytes = data.Slice(offset + sizeof(int), length);
        foreach (byte b in bytes)
        {
            if (b < 0x20 || b == 0x7F)
            {
                return null;
            }
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private sealed class AssetBundleOnlyFactory : AssetFactoryBase
    {
        private readonly GameAssetFactory _inner;

        public AssetBundleOnlyFactory(IAssemblyManager assemblyManager)
        {
            _inner = new GameAssetFactory(assemblyManager);
        }

        public override IUnityObjectBase? ReadAsset(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData, SerializedType? assetType)
        {
            return assetInfo.ClassID == (int)ClassIDType.AssetBundle
                ? _inner.ReadAsset(assetInfo, assetData, assetType)
                : null;
        }
    }

    private static readonly IAssemblyManager NameScanAssemblyManager = new BaseManager(static _ => { });
    private static readonly AssetBundleOnlyFactory NameScanFactory = new(NameScanAssemblyManager);

    public static (string Cab, string FileName, List<string> Paths) ReadContainerNames(SerializedFile sf, string fallbackName)
    {
        string cab = string.IsNullOrWhiteSpace(sf.NameFixed) ? fallbackName : sf.NameFixed;
        List<string> paths = new();
        try
        {
            AssetCollection collection = (AssetCollection)FromSerializedFile.Invoke(null, new object[] { new GameBundle(), sf, NameScanFactory, NameScanVersion })!;
            foreach (IUnityObjectBase asset in collection)
            {
                if (asset is not IAssetBundle assetBundle)
                {
                    continue;
                }
                var container = assetBundle.Container;
                int count = container.Count;
                for (int i = 0; i < count; i++)
                {
                    string key = container.GetKey(i).String;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        paths.Add(key);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Verbose(LogCategory.Import, $"[NameScan] '{cab}': {exception.GetType().Name}: {exception.Message}");
        }
        return (cab, fallbackName, paths);
    }

    public static FilePreInitializeDelegate CustomFilePreInitialize;

    /// <summary>
    /// Drop every decoder a game hook installs here. These are plain statics, so
    /// tearing down a game hook's MonoMod detours does NOT unset them: without this
    /// the first game's VFS readers stay wired up and silently reject the next game's
    /// bundles (symptom: a rebuilt cabmap has 0 CABs until the process restarts).
    /// Called by the hook kernel whenever a game hook leaves the active set.
    /// </summary>
    public static void ResetGameState()
    {
        ScanIncludeFile = null;
        LoadIncludeFile = null;
        EnumerateVfsFiles = null;
        ExtractVfsFile = null;
        ScanChunk = null;
        ScanChunkNames = null;
        ScanChunkFull = null;
        NameScanVersion = default;
        CustomFilePreInitialize = null;
    }

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

        var phase = System.Diagnostics.Stopwatch.StartNew();
        CustomFilePreInitialize(_this, paths, fileStack, fileSystem, initializer?.DependencyProvider);
        long preInitializeMs = phase.ElapsedMilliseconds;

        phase.Restart();
        SerializedBundle?[] preBuiltBundles = new SerializedBundle?[fileStack.Count];
        Parallel.For(0, fileStack.Count, index =>
        {
            if (fileStack[index] is FileContainer container)
            {
                preBuiltBundles[index] = SerializedBundle.FromFileContainer(container, assetFactory, defaultVersion);
            }
        });

        for (int index = fileStack.Count - 1; index >= 0; index--)
        {
            switch (fileStack[index])
            {
                case SerializedFile serializedFile:
                    FromSerializedFile.Invoke(null, new object[] { _this, serializedFile, assetFactory, defaultVersion });
                    break;
                case FileContainer:
                    _this.AddBundle(preBuiltBundles[index]!);
                    break;
                case ResourceFile resourceFile:
                    _this.AddResource(resourceFile);
                    break;
                case FailedFile failedFile:
                    _this.AddFailed(failedFile);
                    break;
            }
        }
        fileStack.Clear();
        Logger.Info(LogCategory.Import,
            $"[GameBundle] preInit={preInitializeMs}ms assetRead={phase.ElapsedMilliseconds}ms");
    }

    private static FileBase RemoveLastItem(List<FileBase> list)
    {
        var index = list.Count - 1;
        var file = list[index];
        list.RemoveAt(index);
        return file;
    }

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

    private static readonly object _dependencyProviderLock = new();

    private static void LoadDependencies(SerializedFile serializedFile, List<FileBase> files, HashSet<string> serializedFileNames, IDependencyProvider? dependencyProvider)
    {
        foreach (var fileIdentifier in serializedFile.Dependencies)
        {
            var name = fileIdentifier.GetFilePath();
            if (!serializedFileNames.Add(name) || dependencyProvider is null)
                continue;
            FileBase? dependency;
            lock (_dependencyProviderLock)
            {
                dependency = dependencyProvider.FindDependency(fileIdentifier);
            }
            if (dependency is not null)
                files.Add(dependency);
        }
    }
}