using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Processing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Ruri.RipperHook.Bridge;

public static class ClosureGraphBlob
{
    public static Dictionary<AssetCollection, string> CollectionIdentities(GameData gameData)
    {
        Dictionary<AssetCollection, string> identities = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        foreach (AssetCollection collection in gameData.GameBundle.FetchAssetCollections())
        {
            StringBuilder builder = new(collection.Name.Length + 32);
            AppendBundleChain(collection.Bundle, builder);
            builder.Append(collection.Name);
            string identity = builder.ToString();
            occurrences.TryGetValue(identity, out int seen);
            occurrences[identity] = seen + 1;
            if (seen > 0)
            {
                identity = $"{identity}#{seen}";
            }
            identities.Add(collection, identity);
        }
        return identities;

        static void AppendBundleChain(Bundle? bundle, StringBuilder builder)
        {
            if (bundle is null || bundle.Parent is null)
            {
                return;
            }
            AppendBundleChain(bundle.Parent, builder);
            builder.Append(bundle.Name).Append('/');
        }
    }

    public static (string MetaJson, byte[] Payload) Build(GameData gameData)
    {
        List<IUnityObjectBase> assets = new();
        Dictionary<IUnityObjectBase, int> indexOfAsset = new(ReferenceEqualityComparer.Instance);
        List<string> collectionNames = new();
        List<int> collectionOf = new();

        Dictionary<AssetCollection, string> identities = CollectionIdentities(gameData);
        foreach (AssetCollection collection in gameData.GameBundle.FetchAssetCollections())
        {
            int collectionIndex = collectionNames.Count;
            collectionNames.Add(identities[collection]);
            foreach (IUnityObjectBase asset in collection)
            {
                indexOfAsset[asset] = assets.Count;
                assets.Add(asset);
                collectionOf.Add(collectionIndex);
            }
        }

        int count = assets.Count;
        string[] names = new string[count];
        int[] classIds = new int[count];
        long[] pathIds = new long[count];
        for (int i = 0; i < count; i++)
        {
            IUnityObjectBase asset = assets[i];
            names[i] = asset.GetBestName().Replace('\0', ' ');
            classIds[i] = asset.ClassID;
            pathIds[i] = asset.PathID;
        }

        List<int> edgeFrom = new();
        List<int> edgeTo = new();
        List<int> edgeField = new();
        Dictionary<string, int> fieldIds = new(StringComparer.Ordinal);
        List<string> fieldNames = new();
        for (int i = 0; i < count; i++)
        {
            IUnityObjectBase asset = assets[i];
            foreach ((string field, PPtr pointer) in asset.FetchDependencies())
            {
                if (pointer.PathID == 0)
                {
                    continue;
                }
                IUnityObjectBase? target = asset.Collection.TryGetAsset(pointer);
                if (target is null || !indexOfAsset.TryGetValue(target, out int targetIndex))
                {
                    continue;
                }
                if (!fieldIds.TryGetValue(field, out int fieldId))
                {
                    fieldId = fieldNames.Count;
                    fieldIds[field] = fieldId;
                    fieldNames.Add(field.Replace('\0', ' '));
                }
                edgeFrom.Add(i);
                edgeTo.Add(targetIndex);
                edgeField.Add(fieldId);
            }
        }

        byte[] nameBytes = Encoding.UTF8.GetBytes(string.Join('\0', names));
        byte[] collectionNameBytes = Encoding.UTF8.GetBytes(string.Join('\0', collectionNames));
        byte[] fieldNameBytes = Encoding.UTF8.GetBytes(string.Join('\0', fieldNames));

        Dictionary<string, int[]> sections = new(StringComparer.Ordinal);
        int offset = 0;
        void Reserve(string key, int length)
        {
            sections[key] = new[] { offset, length };
            offset += length;
        }
        Reserve("names", nameBytes.Length);
        Reserve("collectionNames", collectionNameBytes.Length);
        Reserve("fieldNames", fieldNameBytes.Length);
        Reserve("classIds", count * sizeof(int));
        Reserve("collectionOf", count * sizeof(int));
        Reserve("pathIds", count * sizeof(long));
        Reserve("edgeFrom", edgeFrom.Count * sizeof(int));
        Reserve("edgeTo", edgeTo.Count * sizeof(int));
        Reserve("edgeField", edgeField.Count * sizeof(int));

        byte[] payload = new byte[offset];
        void Put(string key, ReadOnlySpan<byte> data) => data.CopyTo(payload.AsSpan(sections[key][0], sections[key][1]));
        Put("names", nameBytes);
        Put("collectionNames", collectionNameBytes);
        Put("fieldNames", fieldNameBytes);
        Put("classIds", MemoryMarshal.AsBytes<int>(classIds));
        Put("collectionOf", MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(collectionOf)));
        Put("pathIds", MemoryMarshal.AsBytes<long>(pathIds));
        Put("edgeFrom", MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(edgeFrom)));
        Put("edgeTo", MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(edgeTo)));
        Put("edgeField", MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(edgeField)));

        GraphIndex meta = new()
        {
            AssetCount = count,
            EdgeCount = edgeFrom.Count,
            CollectionCount = collectionNames.Count,
            FieldCount = fieldNames.Count,
            Sections = sections,
        };
        return (JsonSerializer.Serialize(meta), payload);
    }

    private sealed class GraphIndex
    {
        public int AssetCount { get; set; }
        public int EdgeCount { get; set; }
        public int CollectionCount { get; set; }
        public int FieldCount { get; set; }
        public Dictionary<string, int[]> Sections { get; set; } = new();
    }
}
