using System;
using System.Linq;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Shaders;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 040 — Build the per-library `UnifiedShaderLibraryMetadata` view
// for the current `state.Entry` and stash it under
// `state.Root.ShaderCodeArchives[entry.PathWithoutExtension]`.
//
// Reads either layout flavour FModel hands us:
//   - FSerializedShaderArchive (legacy / non-IoStore cooks): straightforward
//     copy of ShaderMapHashes / ShaderHashes / entries / indices.
//   - FIoStoreShaderCodeArchive (IoStore cooks): the entries lack
//     uncompressed Size / Frequency that Pass 010 already merged;
//     re-flatten what we can, leaving Size/UncompressedSize=0 since
//     downstream consumers index this map by hash, not by byte slice.
//
// Runs every library hit (no caching gate) — a single FModel session
// can export multiple libraries and each gets its own archive entry.
internal static class Pass040_BuildShaderLibraryMetadata
{
    public static void DoPass(ExportPipelineState state)
    {
        var entry = state.Entry;
        if (entry == null || !entry.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var archiveReader = entry.CreateReader();
        var archive = new FShaderCodeArchive(archiveReader);
        var libraryMetadata = new UnifiedShaderLibraryMetadata
        {
            LibraryPath = entry.PathWithoutExtension,
            LibraryName = entry.NameWithoutExtension,
            LibraryType = archive.SerializedShaders.GetType().Name
        };

        if (archive.SerializedShaders is FSerializedShaderArchive serialized)
        {
            libraryMetadata.ShaderMapHashes = serialized.ShaderMapHashes.Select(x => x.ToString()).ToList();
            libraryMetadata.ShaderHashes = serialized.ShaderHashes.Select(x => x.ToString()).ToList();
            libraryMetadata.ShaderMapEntries = serialized.ShaderMapEntries.Select(e => new UnifiedShaderMapArchiveEntry
            {
                ShaderIndicesOffset = e.ShaderIndicesOffset,
                NumShaders = e.NumShaders,
                FirstPreloadIndex = e.FirstPreloadIndex,
                NumPreloadEntries = e.NumPreloadEntries
            }).ToList();
            libraryMetadata.ShaderEntries = serialized.ShaderEntries.Select(e => new UnifiedShaderArchiveEntry
            {
                Offset = e.Offset,
                Size = e.Size,
                UncompressedSize = e.UncompressedSize,
                Frequency = e.Frequency
            }).ToList();
            libraryMetadata.ShaderIndices = serialized.ShaderIndices.ToList();
        }
        else if (archive.SerializedShaders is FIoStoreShaderCodeArchive ioStore)
        {
            libraryMetadata.ShaderMapHashes = ioStore.ShaderMapHashes.Select(x => x.ToString()).ToList();
            libraryMetadata.ShaderHashes = ioStore.ShaderHashes.Select(x => x.ToString()).ToList();
            libraryMetadata.ShaderMapEntries = ioStore.ShaderMapEntries.Select(e => new UnifiedShaderMapArchiveEntry
            {
                ShaderIndicesOffset = e.ShaderIndicesOffset,
                NumShaders = e.NumShaders,
                FirstPreloadIndex = 0,
                NumPreloadEntries = 0
            }).ToList();
            libraryMetadata.ShaderEntries = ioStore.ShaderEntries.Select(e => new UnifiedShaderArchiveEntry
            {
                Offset = (ulong)e.UncompressedOffsetInGroup,
                Size = 0,
                UncompressedSize = 0,
                Frequency = (byte)e.Frequency
            }).ToList();
            libraryMetadata.ShaderIndices = ioStore.ShaderIndices.ToList();
        }

        state.Root.ShaderCodeArchives[entry.PathWithoutExtension] = libraryMetadata;
        state.Log($"    Library {entry.NameWithoutExtension}: {libraryMetadata.ShaderMapHashes.Count} shader-maps, {libraryMetadata.ShaderHashes.Count} shaders, type={libraryMetadata.LibraryType}.");
    }
}
