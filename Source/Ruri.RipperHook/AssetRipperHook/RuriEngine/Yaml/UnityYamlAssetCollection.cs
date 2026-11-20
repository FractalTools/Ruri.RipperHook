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
}

internal readonly record struct ExternalReference(string Guid, int Type);

internal sealed class UnityYamlAssetCollection : VirtualAssetCollection
{
    private const int SerializedReferenceType = 2;
    private const int ImportedReferenceType = 3;

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
}

internal sealed class ProjectIndex(string root)
{
    private readonly Dictionary<string, UnityYamlAssetCollection> collections = new(StringComparer.Ordinal);

    public string Root { get; } = root;

    public IEnumerable<UnityYamlAssetCollection> Collections => collections.Values;

    public void Add(UnityYamlAssetCollection collection) => collections[collection.Entry.Guid] = collection;

    public bool TryGetCollection(string guid, [NotNullWhen(true)] out UnityYamlAssetCollection? collection)
    {
        return collections.TryGetValue(guid, out collection);
    }

    public int SlotFor(UnityYamlAssetCollection origin, string guid, int type)
    {
        return TryGetCollection(guid, out UnityYamlAssetCollection? target)
            ? origin.AddDependency(target)
            : origin.ExternalSlot(guid, type);
    }
}
