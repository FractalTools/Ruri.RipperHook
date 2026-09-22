using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using System.Collections.Concurrent;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// One archive of the install as cabmap rows: every package it holds is a CAB named by the
/// package's provider path, its dependencies the packages it imports, its classes the Unity
/// classes the export classes convert to, its one addressable path the Unity export path. On
/// IoStore the imports come straight off the container header's store entries -- no package is
/// parsed for them; the export classes come off the package header, whose exports stay lazy.
/// </summary>
public static class UnrealArchiveScan
{
    private const int HeaderParallelism = 8;

    public static List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)> ScanFull(string archivePath)
    {
        List<(string, string, List<string>, List<int>, List<string>)> rows = new();
        if (!UnrealInstall.IsArchive(archivePath))
        {
            return rows;
        }
        UnrealFileProvider provider = UnrealProviderSession.Current;
        IAesVfsReader? reader = FindReader(provider, archivePath);
        if (reader is null)
        {
            Logger.Verbose(LogCategory.Import, $"[Unreal] '{archivePath}' is not a mounted archive; skipped.");
            return rows;
        }

        List<GameFile> packages = new();
        foreach (GameFile file in reader.Files.Values)
        {
            if (file.IsUePackage)
            {
                packages.Add(file);
            }
        }
        if (packages.Count == 0)
        {
            return rows;
        }

        Dictionary<FPackageId, int>? storeIndex = reader is IoStoreReader ioReader && ioReader.ContainerHeader is { StoreEntries.Length: > 0 } header
            ? StoreIndex(header)
            : null;
        TypeMappings? mappings = provider.MappingsForGame;

        (string, string, List<string>, List<int>, List<string>)?[] results = new (string, string, List<string>, List<int>, List<string>)?[packages.Count];
        ParallelOptions options = new() { MaxDegreeOfParallelism = HeaderParallelism };
        Parallel.For(0, packages.Count, options, index =>
        {
            GameFile file = packages[index];
            try
            {
                results[index] = Row(provider, reader, storeIndex, mappings, file);
            }
            catch (Exception exception)
            {
                Logger.Warning(LogCategory.Import, $"[Unreal] Scan '{file.Path}': {exception.GetType().Name}: {exception.Message}");
            }
        });
        foreach (var row in results)
        {
            if (row is not null)
            {
                rows.Add(row.Value);
            }
        }
        provider.Release();
        Logger.Info(LogCategory.Import, $"[Unreal] Scanned '{reader.Name}': {packages.Count} packages, {rows.Count} rows.");
        return rows;
    }

    private static IAesVfsReader? FindReader(DefaultFileProvider provider, string archivePath)
    {
        string wanted = Path.GetFullPath(archivePath);
        foreach (IAesVfsReader reader in provider.MountedVfs)
        {
            if (string.Equals(Path.GetFullPath(reader.Path), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return reader;
            }
        }
        return null;
    }

    private static Dictionary<FPackageId, int> StoreIndex(FIoContainerHeader header)
    {
        Dictionary<FPackageId, int> index = new(header.PackageIds.Length);
        for (int i = 0; i < header.PackageIds.Length; i++)
        {
            index[header.PackageIds[i]] = i;
        }
        return index;
    }

    private static (string, string, List<string>, List<int>, List<string>) Row(UnrealFileProvider provider, IAesVfsReader reader,
        Dictionary<FPackageId, int>? storeIndex, TypeMappings? mappings, GameFile file)
    {
        List<string> dependencies = new();
        HashSet<int> classIds = new();

        if (file is FIoStoreEntry ioEntry && storeIndex is not null && reader is IoStoreReader ioReader)
        {
            FPackageId packageId = ioEntry.ChunkId.AsPackageId();
            if (storeIndex.TryGetValue(packageId, out int storeSlot))
            {
                foreach (FPackageId imported in ioReader.ContainerHeader!.StoreEntries[storeSlot].ImportedPackages)
                {
                    if (provider.FilesById.TryGetValue(imported, out GameFile? importedFile) && !dependencies.Contains(importedFile.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        dependencies.Add(importedFile.Path);
                    }
                }
            }
            IPackage package = provider.LoadUncached(file);
            if (package is IoPackage ioPackage)
            {
                foreach (FExportMapEntry export in ioPackage.ExportMap)
                {
                    string? className = ioPackage.ResolveObjectIndex(export.ClassIndex)?.Name.Text;
                    AddClasses(classIds, className, mappings);
                }
            }
        }
        else
        {
            IPackage package = provider.LoadUncached(file);
            if (package is Package pakPackage)
            {
                foreach (FObjectImport import in pakPackage.ImportMap)
                {
                    if (import.ClassName.Text == "Package" && import.ObjectName.Text.StartsWith('/'))
                    {
                        string fixedPath = provider.FixPath(import.ObjectName.Text);
                        if (provider.Files.TryGetValue(fixedPath, out GameFile? importedFile) && !dependencies.Contains(importedFile.Path, StringComparer.OrdinalIgnoreCase))
                        {
                            dependencies.Add(importedFile.Path);
                        }
                    }
                }
                foreach (FObjectExport export in pakPackage.ExportMap)
                {
                    string? className = pakPackage.ResolvePackageIndex(export.ClassIndex)?.Name.Text;
                    AddClasses(classIds, className, mappings);
                }
            }
        }

        List<string> containerPaths = new() { UnrealPaths.ContainerPath(file.Path) };
        if (classIds.Contains((int)ClassIDType.GameObject))
        {
            containerPaths.Add(UnrealPaths.PrefabPath(file.Path));
        }
        return (file.Path, file.Path, dependencies, classIds.ToList(), containerPaths);
    }

    private static void AddClasses(HashSet<int> classIds, string? className, TypeMappings? mappings)
    {
        if (string.IsNullOrEmpty(className))
        {
            return;
        }
        foreach (ClassIDType produced in UnrealClasses.Of(className, mappings))
        {
            classIds.Add((int)produced);
        }
    }
}
