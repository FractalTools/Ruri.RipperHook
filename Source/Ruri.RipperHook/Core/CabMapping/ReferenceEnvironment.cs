using AssetRipper.Assets.IO;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Platforms;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.CabMapping;

internal sealed class ReferenceEnvironment : IDisposable
{
    private readonly IAssemblyManager _assemblyManager;

    private ReferenceEnvironment(IAssemblyManager assemblyManager)
    {
        _assemblyManager = assemblyManager;
        Factory = new GameAssetFactory(assemblyManager);
        ScriptFactory = new ClassFilterAssetFactory(Factory, (int)ClassIDType.MonoScript);
    }

    public AssetFactoryBase Factory { get; }

    public AssetFactoryBase ScriptFactory { get; }

    public object StructureLock { get; } = new();

    public static ReferenceEnvironment Create(string rootFolder)
    {
        FileSystem fileSystem = LocalFileSystem.Instance;
        ImportSettings settings = new();
        List<string> paths = [rootFolder];
        PlatformChecker.CheckPlatform(paths, fileSystem, out PlatformGameStructure? platformStructure, out MixedGameStructure? mixedStructure);
        PlatformGameStructure structure = platformStructure ?? mixedStructure
            ?? throw new InvalidOperationException($"'{rootFolder}' holds neither a player nor serialized files.");
        structure.CollectFiles(skipStreamingAssets: true);

        IAssemblyManager? manager = null;
        void RequestAssembly(string assembly)
        {
            string? path = structure.RequestAssembly(assembly);
            if (path is null)
            {
                Logger.Warning(LogCategory.Import, $"Assembly '{assembly}' hasn't been found");
                return;
            }
            manager!.Load(path, fileSystem);
            Logger.Info(LogCategory.Import, $"Assembly '{assembly}' has been loaded");
        }

        manager = structure.Backend switch
        {
            ScriptingBackend.Mono => new MonoManager(RequestAssembly),
            ScriptingBackend.IL2Cpp => new IL2CppManager(RequestAssembly, settings.ScriptContentLevel),
            _ => new BaseManager(RequestAssembly),
        };
        Logger.Info(LogCategory.Import, $"[CabMap] References: files use the '{structure.Backend}' scripting backend.");
        try
        {
            manager.Initialize(structure);
        }
        catch (Exception exception)
        {
            Logger.Error(LogCategory.Import, "[CabMap] References: could not initialize the assembly manager; script layouts stay unresolved.", exception);
            manager.Dispose();
            manager = new BaseManager(RequestAssembly);
        }
        return new ReferenceEnvironment(manager);
    }

    public void Dispose() => _assemblyManager.Dispose();
}
