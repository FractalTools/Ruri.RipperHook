using System.Reflection;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.GameBundleHook;

public class GameBundleHook : CommonHook, IHookModule
{
    private static readonly MethodInfo FromSerializedFile = typeof(SerializedAssetCollection)
        .GetMethod("FromSerializedFile", ReflectionExtensions.PrivateStaticBindFlag());

    public delegate void FilePreInitializeDelegate(GameBundle _this, IEnumerable<string> paths,
        List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider);

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