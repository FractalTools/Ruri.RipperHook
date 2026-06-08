using System.Linq;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 020 — Walk every mounted IoStore reader and harvest the
// per-package shader-map-hash list from the container header.
//
// CUE4Parse commit e56242ae commented out `ShaderMapHashes` from
// `FFilePackageStoreEntry` (the field is still serialized in the binary
// at the same offset). Since the submodule is frozen, we re-read the
// raw container header chunk and parse ShaderMapHashes manually.
//
// Result populates `state.Root.PackageShaderMapHashes` keyed by
// `PathWithoutExtension`.
//
// Runs FIRST inside ExportPipeline.Run so Pass 030's material scan can
// scope itself to packages whose hashes intersect the current archive.
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
                continue;

            var header = ioReader.ContainerHeader;
            var packageIds = header.PackageIds;
            if (packageIds == null || packageIds.Length == 0)
                continue;

            var perPackageHashes = ReadShaderMapHashesFromRawHeader(ioReader);
            if (perPackageHashes == null)
                continue;

            for (int i = 0; i < packageIds.Length; i++)
            {
                if (!perPackageHashes.TryGetValue(packageIds[i], out var hashes) || hashes.Length == 0)
                    continue;

                if (!ioReader.PackageIdIndex.TryGetValue(packageIds[i], out var gameFile))
                    continue;

                state.Root.PackageShaderMapHashes[gameFile.PathWithoutExtension] = hashes.Select(h => h.ToString()).ToList();
            }
        }

        state.IoStoreHashesExtracted = true;
        state.Log($"    IoStore shader-map hashes: packages={state.Root.PackageShaderMapHashes.Count}.");
    }

    private static Dictionary<FPackageId, FSHAHash[]>? ReadShaderMapHashesFromRawHeader(IoStoreReader ioReader)
    {
        var chunkId = new FIoChunkId(
            ioReader.TocResource.Header.ContainerId.Id,
            0,
            ioReader.Game >= EGame.GAME_UE5_0
                ? (byte) EIoChunkType5.ContainerHeader
                : (byte) EIoChunkType.ContainerHeader);

        var rawBytes = ioReader.Read(chunkId);
        var Ar = new FByteArchive("ContainerHeader", rawBytes, ioReader.Versions);
        return RawParse(Ar, ioReader.ContainerHeader!);
    }

    private static Dictionary<FPackageId, FSHAHash[]>? RawParse(FArchive Ar, FIoContainerHeader header)
    {
        var version = EIoContainerHeaderVersion.BeforeVersionWasAdded;
        if (Ar.Game >= EGame.GAME_UE5_0)
        {
            Ar.Read<uint>();
            version = Ar.Read<EIoContainerHeaderVersion>();
        }

        Ar.Position += 8; // skip ContainerId (FIoContainerId = ulong)

        if (version < EIoContainerHeaderVersion.OptionalSegmentPackages)
            Ar.Position += 4; // skip packageCount (uint)

        if (version == EIoContainerHeaderVersion.BeforeVersionWasAdded)
            return null; // no ShaderMapHashes in old format

        var pidCount = Ar.Read<int>();
        if (pidCount != header.PackageIds.Length)
            return null;

        Ar.Position += pidCount * 8; // skip package ID data

        var storeEntriesSize = Ar.Read<int>();
        if (storeEntriesSize <= 0)
            return null;

        var result = new Dictionary<FPackageId, FSHAHash[]>(header.PackageIds.Length);
        for (int i = 0; i < header.PackageIds.Length; i++)
        {
            var hashes = ReadEntryShaderMapHashes(Ar, version);
            if (hashes.Length > 0)
                result[header.PackageIds[i]] = hashes;
        }

        return result;
    }

    private static FSHAHash[] ReadEntryShaderMapHashes(FArchive Ar, EIoContainerHeaderVersion version)
    {
        if (version < EIoContainerHeaderVersion.Initial)
            return [];

        if (version < EIoContainerHeaderVersion.NoExportInfo)
            Ar.Position += 8; // skip ExportCount + ExportBundleCount

        Ar.Position += 8; // skip ImportedPackages CArrayView header

        var smhStart = Ar.Position;
        var smhCount = Ar.Read<int>();
        var smhOffset = Ar.Read<int>();

        if (smhCount <= 0)
            return [];

        var savePos = Ar.Position;
        Ar.Position = smhStart + smhOffset;

        var result = new FSHAHash[smhCount];
        for (int j = 0; j < smhCount; j++)
            result[j] = new FSHAHash(Ar);

        Ar.Position = savePos;
        return result;
    }
}
