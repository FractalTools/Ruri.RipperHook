using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.Streams.Smart;

namespace Ruri.RipperHook.BundleExport;

public sealed record BundleRepackResult(
    string Name,
    byte[] Data,
    string GenerationVersion,
    string EngineRevision,
    string[] Nodes,
    string[] Missing,
    long PayloadBytes);

public static class BundleRepack
{
    public static BundleRepackResult ToStandardBundle(byte[] source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);

        using SmartStream stream = SmartStream.CreateMemory(source);
        FileStreamBundleFile bundle = new();
        bundle.Read(stream);

        Dictionary<string, ResourceFile> payloads = new(StringComparer.Ordinal);
        foreach (ResourceFile resource in bundle.ResourceFiles)
        {
            payloads[resource.Name] = resource;
        }

        List<BundleEntry> entries = new(bundle.DirectoryInfo.Nodes.Length);
        List<string> missing = [];
        long payloadBytes = 0;
        foreach (FileStreamNode node in bundle.DirectoryInfo.Nodes)
        {
            if (!payloads.TryGetValue(node.Path, out ResourceFile? resource))
            {
                missing.Add(node.Path);
                continue;
            }
            byte[] data = resource.ToByteArray();
            payloadBytes += data.LongLength;
            entries.Add(new BundleEntry(node.Path, data, node.Flags));
        }

        string generation = bundle.Header.UnityWebBundleVersion.ToString();
        string revision = bundle.Header.UnityWebMinimumRevision.ToString();
        byte[] rebuilt = StandardBundleWriter.Write(entries, generation, revision);
        return new BundleRepackResult(name, rebuilt, generation, revision,
            entries.Select(entry => entry.Path).ToArray(), missing.ToArray(), payloadBytes);
    }
}
