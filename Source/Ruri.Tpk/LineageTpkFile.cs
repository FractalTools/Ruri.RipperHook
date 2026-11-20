using AssetRipper.Tpk;
using AssetRipper.Tpk.TypeTrees;
using Ruri.RipperHook.Core.TypeTree;
using System.Globalization;

namespace Ruri.Tpk;

internal static class LineageTpkFile
{
    public static void Write(IReadOnlyList<TpkTypeTreeBlob> blobs, CustomEngineType engine, string versionKey, string layoutVersion, string outputPath)
    {
        string lineageKey = ((int)engine).ToString(CultureInfo.InvariantCulture);
        TpkCollectionBlob collection = new();
        TypeTreeManifest manifest = new();
        for (int part = 0; part < blobs.Count; part++)
        {
            string key = blobs.Count == 1 ? lineageKey : lineageKey + "#" + part.ToString(CultureInfo.InvariantCulture);
            collection.Add(key, blobs[part]);
            manifest.Lineages.Add(new TypeTreeManifest.LineageEntry
            {
                Key = key,
                UnityNames = key,
                Versions = [new TypeTreeManifest.VersionEntry { Key = versionKey, Engine = layoutVersion }],
            });
            Console.WriteLine($"[{engine}]   blob {key}: {blobs[part].ClassInformation.Count} classes, {blobs[part].NodeBuffer.Count} nodes");
        }
        collection.Add(TypeTreeManifest.BlobName, new TpkJsonBlob { Text = manifest.ToJson() });

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        TpkFile.FromBlob(collection, TpkCompressionType.Brotli).WriteToFile(outputPath);
    }
}
