using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.ViewModels;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    // Pass 010 — Convert FModel's IoStore-resident shader archive
    // (FIoStoreShaderCodeArchive) into a flat FSerializedShaderArchive v2
    // byte stream so downstream tools can read it without re-implementing
    // IoStore group resolution. Also handles plain-archive passthrough
    // when FModel has already deserialised a non-IoStore archive.
    //
    // Streams output directly to disk via FileStream rather than buffering
    // the whole archive in a `MemoryStream`. The master `X6Game` archive
    // can hold 1M+ shader entries totalling 4GB+ of bytecode, which trips
    // MemoryStream's int.MaxValue (~2GB) cap with "Stream was too long"
    // (empirically confirmed on the X6Game cook before this rewrite).
    // The serialised format is offset-table-then-bulk-data, so we can
    // compute every offset upfront from `finalShaderCode[i].Length` and
    // never need to seek backwards — perfect for sequential file write.
    internal static class Pass010_SaveShaderArchive
    {
        // Stream the assembled FSerializedShaderArchive v2 directly to
        // `outputPath`. Returns true on success, false when the entry
        // cannot be exported as an FSerializedShaderArchive layout
        // (non-IoStore reader missing, etc.). The previous byte[]-returning
        // overload was retained for the fast non-IoStore passthrough case
        // where FModel hands us bytes directly — see SaveShaderLibrary
        // overload below.
        public static bool SaveShaderLibrary(GameFile entry, string outputPath, ExportPipelineState? state = null)
        {
            var headerAr = entry.CreateReader();
            var archive = new FShaderCodeArchive(headerAr);

            if (archive.SerializedShaders is not FIoStoreShaderCodeArchive ioArchive)
            {
                // Already a serialized shader archive layout; pipe the
                // original bytes through. Small enough that File.WriteAllBytes
                // is fine — non-IoStore archives are typically a handful of
                // MB. Hash extraction skipped; Pass 030's full-provider
                // fallback handles that case.
                if (state != null) state.CurrentArchiveShaderMapHashes.Clear();
                File.WriteAllBytes(outputPath, entry.Read());
                return true;
            }

            if (entry is not VfsEntry vfsEntry || vfsEntry.Vfs is not IoStoreReader store)
            {
                // IoStore shader archive export requires access to the backing IoStore reader so group chunks can be resolved.
                return false;
            }

            // Stash this archive's shader-map hashes on the export state so
            // Pass 030 can scope its material scan to packages that actually
            // reference shaders in this archive (vs. iterating every UAsset
            // in the provider, which is a 5-figure-files hang on big games).
            if (state != null) PopulateArchiveHashes(state, ioArchive.ShaderMapHashes);

            // Use 1MB FileStream buffer; the platform tests well above this
            // because the disk side caches the rest. The previous
            // double-MemoryStream design peaked at ~2x total code size in
            // RAM (codeBuffer + outStream), pushing 6-8 GB on the master
            // archive before the 2GB MemoryStream limit broke it.
            using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            using var writer = new BinaryWriter(outStream);

            // Rebuild a standard serialized shader archive stream so downstream tools can consume IoStore shader libraries
            // without reproducing IoStore group resolution logic.
            writer.Write((uint)2);

            // Write Arrays
            WriteShaHashArray(writer, ioArchive.ShaderMapHashes);
            WriteShaHashArray(writer, ioArchive.ShaderHashes);

            // Two-pass design with constant memory in the IoStore-archive
            // size:
            //   PASS 1 (compute sizes only):
            //     Walk every shader entry, group its slices by group index,
            //     sort within group by offset, derive per-shader size
            //     PURELY from offsets + group total uncompressed size — NO
            //     decompression. Build the FShaderCodeEntry table from
            //     these sizes.
            //   PASS 2 (stream code body):
            //     Walk groups in order. For each group: decompress IT, copy
            //     each of its slices to outStream in entry-index order via
            //     a small position-tracking trick, then DROP the
            //     decompressed buffer.
            //
            // Peak RAM = max(single decompressed group). For X6Game's
            // master archive (~4GB total bytecode across thousands of
            // groups, biggest group ~20-50MB) this caps RAM at ~50MB
            // instead of holding all 4GB in `decompressedGroups[]` at
            // once. Eliminates the OOM-trajectory that broke the master
            // archive in the previous design.
            //
            // Format constraint that allows two-pass: the FShaderCodeEntry
            // record is `(Offset, Size, UncompressedSize, Frequency)` per
            // shader — Offset is WITHIN the code body section. So Offset
            // is computable from accumulated sizes, Size is computable
            // from group-internal slice offsets, and Frequency comes from
            // the IoStore header. None of these need the actual bytecode.
            var shaderEntries = new List<FShaderCodeEntry>();
            var preloadEntries = new List<FFileCachePreloadEntry>();
            var shaderMapEntries = new List<FShaderMapEntry>();

            // PASS 1a — group every shader entry by its source IoStore
            // group, recording (entry-index, within-group-offset). Sort
            // within each group by offset; the per-shader size is then
            // `nextOffset - thisOffset`, with the last slice closed off
            // by the group's total uncompressed size.
            var groupSlices = new List<List<(int shaderIndex, int offset)>>(ioArchive.ShaderGroupEntries.Length);
            for (int g = 0; g < ioArchive.ShaderGroupEntries.Length; g++) groupSlices.Add(new List<(int, int)>());
            for (int i = 0; i < ioArchive.ShaderEntries.Length; i++)
            {
                var entryInfo = ioArchive.ShaderEntries[i];
                groupSlices[(int)entryInfo.ShaderGroupIndex].Add((i, (int)entryInfo.UncompressedOffsetInGroup));
            }
            foreach (var slices in groupSlices) slices.Sort((a, b) => a.offset.CompareTo(b.offset));

            // PASS 1b — derive per-shader uncompressed size via offset
            // diffs (no decompression).
            int[] shaderSizes = new int[ioArchive.ShaderEntries.Length];
            for (int g = 0; g < ioArchive.ShaderGroupEntries.Length; g++)
            {
                var slices = groupSlices[g];
                int groupTotal = (int)ioArchive.ShaderGroupEntries[g].UncompressedSize;
                for (int k = 0; k < slices.Count; k++)
                {
                    int off = slices[k].offset;
                    int nextOff = (k == slices.Count - 1) ? groupTotal : slices[k + 1].offset;
                    int len = nextOff - off;
                    if (len < 0) len = 0;
                    shaderSizes[slices[k].shaderIndex] = len;
                }
            }

            // PASS 1c — build the FShaderCodeEntry table. Offsets are
            // cumulative within the code body section.
            long currentOffset = 0;
            for (int i = 0; i < ioArchive.ShaderEntries.Length; i++)
            {
                int len = shaderSizes[i];
                shaderEntries.Add(new FShaderCodeEntry
                {
                    Offset = (ulong)currentOffset,
                    Size = (uint)len,
                    UncompressedSize = (uint)len,
                    Frequency = (byte)ioArchive.ShaderEntries[i].Frequency
                });
                currentOffset += len;
            }

            // 3. Metadata Mapping
            int currentPreloadIndex = 0;
            
            for(int i=0; i < ioArchive.ShaderMapEntries.Length; i++)
            {
                var ioMap = ioArchive.ShaderMapEntries[i];
                var mapEntry = new FShaderMapEntry
                {
                    ShaderIndicesOffset = ioMap.ShaderIndicesOffset,
                    NumShaders = ioMap.NumShaders,
                    FirstPreloadIndex = (uint)currentPreloadIndex,
                    NumPreloadEntries = 0 // Populate below
                };
                
                // For each shader in map, add preload entry
                for(int j=0; j < ioMap.NumShaders; j++)
                {
                    var sIdxIdx = (int)(ioMap.ShaderIndicesOffset + j);
                    if (sIdxIdx < ioArchive.ShaderIndices.Length)
                    {
                        var sIdx = ioArchive.ShaderIndices[sIdxIdx];
                        var sEntry = shaderEntries[(int)sIdx];
                        
                        preloadEntries.Add(new FFileCachePreloadEntry
                        {
                            Offset = (long)sEntry.Offset,
                            Size = (long)sEntry.Size
                        });
                        mapEntry.NumPreloadEntries++;
                        currentPreloadIndex++;
                    }
                }
                
                shaderMapEntries.Add(mapEntry);
            }

            // Write Structures
            // ShaderMapEntries
            writer.Write(shaderMapEntries.Count);
            foreach(var m in shaderMapEntries) WriteShaderMapEntry(writer, m);
            
            // ShaderEntries
            writer.Write(shaderEntries.Count);
            foreach(var e in shaderEntries) WriteShaderCodeEntry(writer, e);

            // PreloadEntries
            writer.Write(preloadEntries.Count);
            foreach(var p in preloadEntries) WritePreloadEntry(writer, p);
            
            // ShaderIndices
            writer.Write(ioArchive.ShaderIndices.Length);
            foreach(var idx in ioArchive.ShaderIndices) writer.Write(idx);

            writer.Flush();

            // PASS 2 — stream the code body to disk, decompressing
            // groups on-demand with a single-group cache.
            //
            // Walking entries in INDEX order (which the format requires —
            // each entry's Offset was computed cumulatively in that order)
            // means we touch groups in roughly group-order too because
            // UE's cook step packs related shaders into the same IoStore
            // group. The single-group cache below holds at most one
            // decompressed buffer at a time, so peak RAM during this loop
            // = `max(group.UncompressedSize)` rather than `sum(every
            // group)`. On X6Game's master archive that's ~50MB instead
            // of 4GB+.
            //
            // If entries DON'T cluster by group (very rare cook), we'd
            // re-decompress some groups; the time cost is bounded but
            // RAM stays flat. Worth it.
            int currentGroupIndex = -1;
            byte[]? currentGroupData = null;
            for (int i = 0; i < ioArchive.ShaderEntries.Length; i++)
            {
                var entryInfo = ioArchive.ShaderEntries[i];
                int groupIdx = (int)entryInfo.ShaderGroupIndex;
                int off = (int)entryInfo.UncompressedOffsetInGroup;
                int len = shaderSizes[i];
                if (len <= 0) continue;

                if (groupIdx != currentGroupIndex)
                {
                    // Drop the previous group's decompressed buffer
                    // before we materialise the next one — keeps RAM
                    // bounded to one group at a time.
                    currentGroupData = null;

                    var chunkId = ioArchive.ShaderGroupIoHashes[groupIdx];
                    var chunkData = store.Read(chunkId);
                    var groupEntry = ioArchive.ShaderGroupEntries[groupIdx];
                    if (groupEntry.CompressedSize < groupEntry.UncompressedSize)
                    {
                        currentGroupData = DecompressShaderChunk(chunkData, (int)groupEntry.UncompressedSize);
                    }
                    else
                    {
                        currentGroupData = chunkData;
                    }
                    currentGroupIndex = groupIdx;
                }

                if (currentGroupData != null && off + len <= currentGroupData.Length)
                {
                    outStream.Write(currentGroupData, off, len);
                }
                else
                {
                    // Defensive: write zeros if the slice extends past
                    // the decompressed buffer. Shouldn't happen for a
                    // well-formed IoStore archive, but keeps the file's
                    // metadata-declared sizes truthful.
                    var pad = new byte[len];
                    outStream.Write(pad, 0, len);
                }
            }

            outStream.Flush();
            return true;
        }

        // Backwards-compatible byte[] entry point for the rare
        // small-archive callers that prefer a buffer. Internally streams
        // to a temp file then reads back; not recommended for the master
        // archive (use the streaming overload above).
        public static byte[]? SaveShaderLibrary(GameFile entry, ExportPipelineState? state = null)
        {
            string tempPath = Path.GetTempFileName();
            try
            {
                if (!SaveShaderLibrary(entry, tempPath, state)) return null;
                return File.ReadAllBytes(tempPath);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        // Reset + repopulate the per-archive hash set from the parsed
        // shader-map-hash array. Each archive export sees a fresh hash
        // set; cached materials from prior exports are kept in
        // state.LoadedMaterialCache so a second archive that overlaps
        // doesn't re-load already-extracted UAssets.
        private static void PopulateArchiveHashes(ExportPipelineState state, FSHAHash[]? hashes)
        {
            state.CurrentArchiveShaderMapHashes.Clear();
            if (hashes == null) return;
            foreach (FSHAHash hash in hashes)
            {
                state.CurrentArchiveShaderMapHashes.Add(hash.ToString());
            }
        }

        private static byte[] DecompressShaderChunk(byte[] data, int expectedSize)
        {
             // Zstd Check
             if (data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
             {
                return CUE4Parse.Compression.Compression.Decompress(data, expectedSize, CompressionMethod.Zstd);
            }

            if (OodleHelper.Instance == null)
            {
                ApplicationViewModel.InitOodle().Wait();
            }
            if (OodleHelper.Instance != null)
            {
                var res = new byte[expectedSize];
                OodleHelper.Decompress(data, 0, data.Length, res, 0, expectedSize);
                return res;
            }

            return data;
        }

        private static void WriteShaHashArray(BinaryWriter writer, FSHAHash[] hashes)
        {
            writer.Write(hashes.Length);
            foreach (var h in hashes) writer.Write(h.Hash);
        }
        
        private static void WriteShaderMapEntry(BinaryWriter writer, FShaderMapEntry e)
        {
            writer.Write(e.ShaderIndicesOffset);
            writer.Write(e.NumShaders);
            writer.Write(e.FirstPreloadIndex);
            writer.Write(e.NumPreloadEntries);
        }
        
        private static void WriteShaderCodeEntry(BinaryWriter writer, FShaderCodeEntry e)
        {
            writer.Write(e.Offset);
            writer.Write(e.Size);
            writer.Write(e.UncompressedSize);
            writer.Write(e.Frequency);
        }
        
        private static void WritePreloadEntry(BinaryWriter writer, FFileCachePreloadEntry e)
        {
            writer.Write(e.Offset);
            writer.Write(e.Size);
        }
        
        struct FShaderMapEntry { public uint ShaderIndicesOffset; public uint NumShaders; public uint FirstPreloadIndex; public uint NumPreloadEntries; }
        struct FShaderCodeEntry { public ulong Offset; public uint Size; public uint UncompressedSize; public byte Frequency; }
        struct FFileCachePreloadEntry { public long Offset; public long Size; }
    }
}
