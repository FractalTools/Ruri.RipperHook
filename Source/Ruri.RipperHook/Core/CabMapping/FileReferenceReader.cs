using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.CabMapping;

internal sealed class FileReferenceReader
{
    private readonly ReferenceEnvironment _environment;
    private readonly ScriptHostCache _hosts;
    private readonly ReferenceReport _report;

    public FileReferenceReader(ReferenceEnvironment environment, ScriptHostCache hosts, ReferenceReport report)
    {
        _environment = environment;
        _hosts = hosts;
        _report = report;
    }

    public FileReferences Read(string cab, SerializedFile file)
    {
        HashSet<long> rowIds = [];
        foreach ((long pathId, _, _) in GameBundleHook.HarvestAssetNames(file))
        {
            rowIds.Add(pathId);
        }

        string[] dependencyNames = DependencyNames(file);
        using GameBundle bundle = new();
        SerializedAssetCollection collection = bundle.AddCollectionFromSerializedFile(file, _environment.Factory);
        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase) { cab };
        foreach (string name in dependencyNames)
        {
            if (name.Length > 0 && included.Add(name) && _hosts.Find(name) is { } host)
            {
                bundle.AddCollectionFromSerializedFile(host, _environment.ScriptFactory);
            }
        }
        bundle.InitializeAllDependencyLists();

        Dictionary<long, long> mergedOwners = new();
        HashSet<(long Source, int FileId, long Target)> edges = new();
        ReferenceWalker walker = new();
        foreach (IUnityObjectBase asset in collection)
        {
            Interlocked.Increment(ref _report.ObjectsRead);
            CountUnreadable(asset);

            walker.Clear();
            Walk(asset, walker);

            long pathId = asset.PathID;
            bool hasRow = rowIds.Contains(pathId);
            long owner = hasRow ? pathId : OwnerOf(walker.Found, rowIds);
            if (!hasRow)
            {
                mergedOwners[pathId] = owner;
            }

            foreach ((string field, int fileId, long target) in walker.Found)
            {
                if (!StructuralFields.Contains(field))
                {
                    edges.Add((owner, fileId, target));
                }
            }
        }

        return new FileReferences
        {
            Cab = cab,
            DependencyNames = dependencyNames,
            RowIds = rowIds,
            MergedOwners = mergedOwners,
            Edges = edges,
        };
    }

    private void Walk(IUnityObjectBase asset, ReferenceWalker walker)
    {
        try
        {
            if (asset is IMonoBehaviour monoBehaviour)
            {
                lock (_environment.StructureLock)
                {
                    monoBehaviour.LoadStructure();
                }
                if (monoBehaviour.Structure is null)
                {
                    Interlocked.Increment(ref _report.ScriptLayoutsUnresolved);
                }
            }
            asset.WalkStandard(walker);
        }
        catch (Exception exception)
        {
            walker.Clear();
            _report.Failure($"WalkStandard of {asset.ClassName}", exception);
        }
    }

    private void CountUnreadable(IUnityObjectBase asset)
    {
        if (asset is UnreadableObject)
        {
            Interlocked.Increment(ref _report.ObjectsUnreadable);
        }
        else if (asset is UnknownObject)
        {
            Interlocked.Increment(ref _report.ObjectsUnknown);
        }
    }

    private static long OwnerOf(List<(string Field, int FileId, long PathId)> found, HashSet<long> rowIds)
    {
        foreach ((string field, int fileId, long pathId) in found)
        {
            if (field == StructuralFields.Owner)
            {
                return fileId == 0 && rowIds.Contains(pathId) ? pathId : 0;
            }
        }
        return 0;
    }

    private static string[] DependencyNames(SerializedFile file)
    {
        ReadOnlySpan<FileIdentifier> identifiers = file.Dependencies;
        string[] names = new string[identifiers.Length];
        for (int index = 0; index < names.Length; index++)
        {
            string name = identifiers[index].GetFilePath();
            names[index] = string.IsNullOrWhiteSpace(name) ? string.Empty : name;
        }
        return names;
    }
}
