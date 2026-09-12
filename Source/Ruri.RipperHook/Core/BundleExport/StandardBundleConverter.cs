using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using Ruri.RipperHook.HookUtils;

namespace Ruri.RipperHook.BundleExport;

public sealed record BundleConvertResult(
    string Name,
    byte[] Data,
    int Converted,
    int Skipped,
    string[] Failures);

public static class StandardBundleConverter
{
    public static BundleConvertResult Convert(byte[] source, string name,
        IEnumerable<AssetCollection> collections, IReadOnlyCollection<int>? classIdFilter = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(collections);

        Dictionary<string, AssetCollection> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (AssetCollection collection in collections)
        {
            byName[collection.Name] = collection;
        }

        using SmartStream stream = SmartStream.CreateMemory(source);
        FileStreamBundleFile bundle = new();
        bundle.Read(stream);

        Dictionary<string, ResourceFile> payloads = new(StringComparer.Ordinal);
        foreach (ResourceFile resource in bundle.ResourceFiles)
        {
            payloads[resource.Name] = resource;
        }

        List<BundleEntry> entries = new(bundle.DirectoryInfo.Nodes.Length);
        List<string> failures = [];
        int converted = 0;
        int skipped = 0;

        foreach (FileStreamNode node in bundle.DirectoryInfo.Nodes)
        {
            if (!payloads.TryGetValue(node.Path, out ResourceFile? resource))
            {
                failures.Add($"{node.Path}: the bundle reader produced no payload for this node.");
                continue;
            }
            byte[] data = resource.ToByteArray();
            if (byName.TryGetValue(node.Path, out AssetCollection? collection)
                || byName.TryGetValue(node.PathFixed, out collection))
            {
                data = Rewrite(data, node.Path, collection, classIdFilter, failures,
                    ref converted, ref skipped);
            }
            else if (SerializedFile.IsSerializedFile(new MemoryStream(data)))
            {
                failures.Add($"{node.Path}: no loaded asset collection is named after this node, so its game "
                    + $"layout was copied through. Loaded collections: {string.Join(", ", byName.Keys)}");
            }
            entries.Add(new BundleEntry(node.Path, data, node.Flags));
        }

        byte[] rebuilt = StandardBundleWriter.Write(entries,
            bundle.Header.UnityWebBundleVersion.ToString(),
            bundle.Header.UnityWebMinimumRevision.ToString());
        return new BundleConvertResult(name, rebuilt, converted, skipped, failures.ToArray());
    }

    private static byte[] Rewrite(byte[] data, string nodePath, AssetCollection collection,
        IReadOnlyCollection<int>? classIdFilter, List<string> failures, ref int converted, ref int skipped)
    {
        using SmartStream stream = SmartStream.CreateMemory(data);
        SerializedFile file = new();
        try
        {
            file.Read(stream);
        }
        catch (Exception failed)
        {
            failures.Add($"{nodePath}: not a serialized file ({failed.GetType().Name}: {failed.Message}).");
            return data;
        }

        ObjectInfo[] objects = file.Objects();
        int rewritten = 0;
        for (int index = 0; index < objects.Length; index++)
        {
            if (classIdFilter is { Count: > 0 } && !classIdFilter.Contains(objects[index].ClassID))
            {
                skipped++;
                continue;
            }
            if (!collection.TryGetAsset(objects[index].FileID, out IUnityObjectBase? asset))
            {
                failures.Add($"{nodePath}[{objects[index].FileID}]: class {objects[index].ClassID} never reached "
                    + "the asset layer, so its game layout cannot be converted.");
                skipped++;
                continue;
            }
            MemoryStream buffer = new();
            AssetWriter writer = new(buffer, collection);
            try
            {
                asset.WriteRelease(writer);
                writer.Flush();
            }
            catch (Exception failed)
            {
                failures.Add($"{nodePath}[{objects[index].FileID}]: class {objects[index].ClassID} "
                    + $"cannot be written back ({failed.GetType().Name}: {failed.Message}).");
                skipped++;
                continue;
            }
            objects[index].ObjectData = buffer.ToArray();
            rewritten++;
            converted++;
        }

        if (rewritten == 0)
        {
            return data;
        }

        file.SetHasTypeTree(false);
        MemoryStream output = new();
        file.Write(output);
        return output.ToArray();
    }
}
