using System.Collections.Concurrent;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.RuriEngine;

internal enum ProjectFileKind
{
    Folder,
    YamlAsset,
    Script,
    Payload,
    Skipped,
}

internal sealed class ProjectEntry
{
    public required string Guid { get; init; }

    public required string RelativePath { get; init; }

    public required string AbsolutePath { get; init; }

    public required UnityYamlMapping Meta { get; init; }

    public required string ImporterName { get; init; }

    public required ProjectFileKind Kind { get; init; }

    public bool IsPackage { get; init; }

    public bool WritesMeta { get; init; } = true;

    public string? ConvertedRelativePath { get; set; }

    public UnityYamlMapping? OutputMeta { get; set; }

    public string OutputRelativePath => ConvertedRelativePath ?? RelativePath;

    public long? MainObjectFileId
    {
        get
        {
            return Meta.TryGet(ImporterName, out UnityYamlNode? importer)
                && importer is UnityYamlMapping block
                && block.TryGetText("mainObjectFileID") is { } text
                && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long id)
                && id != 0
                ? id
                : null;
        }
    }

    public bool IsMain(long fileId) => MainObjectFileId is { } main ? fileId == main : ObjectGuid.IsMainByStride(fileId);
}

internal readonly record struct ExternalReference(string Guid, int Type);

internal sealed class UnityYamlAssetCollection : VirtualAssetCollection
{
    private const int SerializedReferenceType = 2;
    private const int ImportedReferenceType = 3;
    private const int SelfFileIndex = 0;

    private readonly Dictionary<int, ExternalReference> externals = new();
    private readonly Dictionary<(string Guid, int Type), int> externalSlots = new();

    public UnityYamlAssetCollection(Bundle bundle, ProjectEntry entry, UnityVersion version) : base(bundle)
    {
        Entry = entry;
        Name = entry.Guid;
        FilePath = entry.AbsolutePath;
        OriginalVersion = version;
        Version = version;
        Platform = BuildTarget.NoTarget;
        Flags = TransferInstructionFlags.NoTransferInstructionFlags;
        EndianType = EndianType.LittleEndian;
    }

    public ProjectEntry Entry { get; }

    public ProjectIndex? Project { get; set; }

    public Dictionary<long, UnityYamlMapping> Bodies { get; } = new();

    public NodeFile? Nodes { get; set; }

    public int ReferenceType => Entry.Kind == ProjectFileKind.YamlAsset ? SerializedReferenceType : ImportedReferenceType;

    public void Add(IUnityObjectBase asset) => AddAsset(asset);

    public int ExternalSlot(string guid, int type)
    {
        if (externalSlots.TryGetValue((guid, type), out int slot))
        {
            return slot;
        }
        slot = Dependencies.Count;
        SetDependency(slot, null);
        externalSlots[(guid, type)] = slot;
        externals[slot] = new ExternalReference(guid, type);
        return slot;
    }

    public bool TryGetExternal(int fileIndex, out ExternalReference external) => externals.TryGetValue(fileIndex, out external);

    public ReferenceValue Reference(long fileId, RecordTable records)
    {
        if (Nodes is { } nodes)
        {
            return nodes.Reference(Entry.Guid, fileId);
        }
        ReferenceValue reference = ReferenceValue.To(Entry.Guid, fileId, Entry.IsMain(fileId));
        return TryGetAsset(SelfFileIndex, fileId, out IUnityObjectBase? asset) ? reference.WithComponentType(records.ComponentTypeOf(asset)) : reference;
    }
}

internal sealed class ProjectIndex(string root)
{
    private readonly ConcurrentDictionary<string, UnityYamlAssetCollection> collections = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<UnityYamlAssetCollection> pending = new();
    private readonly object materializing = new();

    public string Root { get; } = root;

    public IEnumerable<UnityYamlAssetCollection> Collections => collections.Values;

    public Func<string, UnityYamlAssetCollection?>? Materialize { get; set; }

    public void Add(UnityYamlAssetCollection collection) => collections[collection.Entry.Guid] = collection;

    public bool TryGetCollection(string guid, [NotNullWhen(true)] out UnityYamlAssetCollection? collection)
    {
        if (collections.TryGetValue(guid, out collection))
        {
            return true;
        }
        if (Materialize is null)
        {
            return false;
        }
        lock (materializing)
        {
            if (collections.TryGetValue(guid, out collection))
            {
                return true;
            }
            collection = Materialize(guid);
            if (collection is null)
            {
                return false;
            }
            collections[guid] = collection;
            if (collection.Entry.Kind == ProjectFileKind.YamlAsset)
            {
                pending.Enqueue(collection);
            }
            return true;
        }
    }

    public List<UnityYamlAssetCollection> TakePending()
    {
        List<UnityYamlAssetCollection> taken = new();
        while (pending.TryDequeue(out UnityYamlAssetCollection? next))
        {
            taken.Add(next);
        }
        return taken;
    }

    public int SlotFor(UnityYamlAssetCollection origin, string guid, int type)
    {
        return TryGetCollection(guid, out UnityYamlAssetCollection? target)
            ? origin.AddDependency(target)
            : origin.ExternalSlot(guid, type);
    }
}
