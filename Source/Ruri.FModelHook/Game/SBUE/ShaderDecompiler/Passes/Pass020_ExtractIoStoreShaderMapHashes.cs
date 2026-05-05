using System.Linq;
using CUE4Parse.UE4.IO;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 020 — Walk every mounted IoStore reader and harvest the
// per-package shader-map-hash list straight out of the container header
// (`StoreEntries[i].ShaderMapHashes`). Result populates
// `state.Root.PackageShaderMapHashes` keyed by package
// `PathWithoutExtension`.
//
// This is the IoStore-side data source for the on-disk shader-map hash.
// It's distinct from the per-material `CookedShaderMapIdHash` /
// `ShaderContentHash` populated by Pass 030 — IoStore cooks store the
// on-disk hash here, NOT inside the material's UAsset, so without this
// pass the asset-info sidecar links are missing for IoStore packages.
//
// Runs FIRST inside ExportPipeline.Run so Pass 030's material scan can
// scope itself to packages whose hashes intersect the current archive
// (instead of walking every UAsset in the provider).
//
// Cached: same FModel session keeps the same provider, so this only
// runs once per `ExportPipelineState`.
internal static class Pass020_ExtractIoStoreShaderMapHashes
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.IoStoreHashesExtracted) return;

        var provider = state.Vm?.Provider;
        if (provider == null) return;

        var readers = provider.MountedVfs.Concat(provider.UnloadedVfs);
        foreach (var reader in readers)
        {
            if (reader is not IoStoreReader ioReader || ioReader.ContainerHeader == null)
            {
                continue;
            }

            var header = ioReader.ContainerHeader;
            var packageIds = header.PackageIds;
            var storeEntries = header.StoreEntries;
            if (packageIds == null || storeEntries == null || packageIds.Length != storeEntries.Length)
            {
                continue;
            }

            for (int i = 0; i < packageIds.Length; i++)
            {
                var entry = storeEntries[i];
                if (entry.ShaderMapHashes == null || entry.ShaderMapHashes.Length == 0)
                {
                    continue;
                }

                if (!ioReader.PackageIdIndex.TryGetValue(packageIds[i], out var gameFile))
                {
                    continue;
                }

                string packageName = gameFile.PathWithoutExtension;
                state.Root.PackageShaderMapHashes[packageName] = entry.ShaderMapHashes.Select(h => h.ToString()).ToList();
            }
        }

        state.IoStoreHashesExtracted = true;
        state.Log($"    IoStore shader-map hashes: packages={state.Root.PackageShaderMapHashes.Count}.");
    }
}
