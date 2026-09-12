using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass020_ExtractIoStoreShaderMapHashes
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.IoStoreHashesExtracted) return;

        var provider = state.Provider;
        if (provider == null) return;

        var readers = provider.MountedVfs.Concat(provider.UnloadedVfs);
        foreach (var reader in readers)
        {
            try
            {
            // A container mounted without its directory index has no package index either, and
            // a package id is the only way back from a shader map to the package that owns it.
            if (reader is not IoStoreReader ioReader || ioReader.ContainerHeader == null || ioReader.PackageIdIndex == null)
                continue;

            var header = ioReader.ContainerHeader;
            var packageIds = header.PackageIds;
            if (packageIds == null || packageIds.Length == 0)
                continue;

            // The header chunk a container's shader-map hashes live in is not something every
            // container carries, and asking one that does not is how this pass used to take the
            // whole export down with it.
            Dictionary<FPackageId, FSHAHash[]>? perPackageHashes;
            try
            {
                perPackageHashes = ReadShaderMapHashesFromRawHeader(ioReader);
            }
            catch (Exception failed)
            {
                state.Log($"    IoStore shader-map hashes: '{ioReader.Name}' states none ({failed.GetType().Name}).");
                continue;
            }
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
            catch (Exception failed)
            {
                // Best effort by contract: this pass states what the containers that CAN answer
                // say, and one that cannot must not take the whole export down with it.
                state.Log($"    IoStore shader-map hashes: a container states none ({failed.GetType().Name}).");
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

        Ar.Position += 8;
        if (version < EIoContainerHeaderVersion.OptionalSegmentPackages)
            Ar.Position += 4;
        if (version == EIoContainerHeaderVersion.BeforeVersionWasAdded)
            return null;
        var pidCount = Ar.Read<int>();
        if (pidCount != header.PackageIds.Length)
            return null;

        Ar.Position += pidCount * 8;
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
            Ar.Position += 8;
        Ar.Position += 8;
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
