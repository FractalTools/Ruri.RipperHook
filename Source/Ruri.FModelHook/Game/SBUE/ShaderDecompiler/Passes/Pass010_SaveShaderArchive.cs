using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.ViewModels;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    internal static class Pass010_SaveShaderArchive
    {
        public static bool SaveShaderLibrary(GameFile entry, string outputPath, ExportPipelineState? state = null)
        {
            var headerAr = entry.CreateReader();
            var archive = new FShaderCodeArchive(headerAr);

            if (archive.SerializedShaders is not FIoStoreShaderCodeArchive ioArchive)
            {
                if (state != null) state.CurrentArchiveShaderMapHashes.Clear();
                File.WriteAllBytes(outputPath, entry.Read());
                return true;
            }

            if (entry is not VfsEntry vfsEntry || vfsEntry.Vfs is not IoStoreReader store)
            {
                return false;
            }

            if (state != null) PopulateArchiveHashes(state, ioArchive.ShaderMapHashes);

            using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            using var writer = new BinaryWriter(outStream);

            writer.Write((uint)2);

            WriteShaHashArray(writer, ioArchive.ShaderMapHashes);
            WriteShaHashArray(writer, ioArchive.ShaderHashes);

            var shaderEntries = new List<FShaderCodeEntry>();
            var preloadEntries = new List<FFileCachePreloadEntry>();
            var shaderMapEntries = new List<FShaderMapEntry>();

            var groupSlices = new List<List<(int shaderIndex, int offset)>>(ioArchive.ShaderGroupEntries.Length);
            for (int g = 0; g < ioArchive.ShaderGroupEntries.Length; g++) groupSlices.Add(new List<(int, int)>());
            for (int i = 0; i < ioArchive.ShaderEntries.Length; i++)
            {
                var entryInfo = ioArchive.ShaderEntries[i];
                groupSlices[(int)entryInfo.ShaderGroupIndex].Add((i, (int)entryInfo.UncompressedOffsetInGroup));
            }
            foreach (var slices in groupSlices) slices.Sort((a, b) => a.offset.CompareTo(b.offset));

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

            int currentPreloadIndex = 0;
            
            for(int i=0; i < ioArchive.ShaderMapEntries.Length; i++)
            {
                var ioMap = ioArchive.ShaderMapEntries[i];
                var mapEntry = new FShaderMapEntry
                {
                    ShaderIndicesOffset = ioMap.ShaderIndicesOffset,
                    NumShaders = ioMap.NumShaders,
                    FirstPreloadIndex = (uint)currentPreloadIndex,
                    NumPreloadEntries = 0                };
                
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

            writer.Write(shaderMapEntries.Count);
            foreach(var m in shaderMapEntries) WriteShaderMapEntry(writer, m);
            
            writer.Write(shaderEntries.Count);
            foreach(var e in shaderEntries) WriteShaderCodeEntry(writer, e);

            writer.Write(preloadEntries.Count);
            foreach(var p in preloadEntries) WritePreloadEntry(writer, p);
            
            writer.Write(ioArchive.ShaderIndices.Length);
            foreach(var idx in ioArchive.ShaderIndices) writer.Write(idx);

            writer.Flush();

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
                    var pad = new byte[len];
                    outStream.Write(pad, 0, len);
                }
            }

            outStream.Flush();
            return true;
        }

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
