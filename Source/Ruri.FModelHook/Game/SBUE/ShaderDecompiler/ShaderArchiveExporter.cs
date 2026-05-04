using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.ViewModels;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    public static class ShaderArchiveExporter
    {
        public static byte[]? SaveShaderLibrary(GameFile entry)
        {
            var headerAr = entry.CreateReader();
            var archive = new FShaderCodeArchive(headerAr);

            if (archive.SerializedShaders is not FIoStoreShaderCodeArchive ioArchive)
            {
                // Already a serialized shader archive layout; export the original bytes unchanged.
                return entry.Read();
            }

            if (entry is not VfsEntry vfsEntry || vfsEntry.Vfs is not IoStoreReader store)
            {
                // IoStore shader archive export requires access to the backing IoStore reader so group chunks can be resolved.
                return null;
            }

            using var outStream = new MemoryStream();
            using var writer = new BinaryWriter(outStream);

            // Rebuild a standard serialized shader archive stream so downstream tools can consume IoStore shader libraries
            // without reproducing IoStore group resolution logic.
            writer.Write((uint)2);

            // Write Arrays
            WriteShaHashArray(writer, ioArchive.ShaderMapHashes);
            WriteShaHashArray(writer, ioArchive.ShaderHashes);

            // We need to read and resolve the Code Chunks to build the Code Buffer and new Entries
            var codeBuffer = new MemoryStream();
            var shaderEntries = new List<FShaderCodeEntry>();
            var preloadEntries = new List<FFileCachePreloadEntry>();
            var shaderMapEntries = new List<FShaderMapEntry>();

            // 1. Read All Chunks & Decompress
            var decompressedGroups = new byte[ioArchive.ShaderGroupEntries.Length][];
            for (int i = 0; i < ioArchive.ShaderGroupEntries.Length; i++)
            {
                var chunkId = ioArchive.ShaderGroupIoHashes[i];
                var chunkData = store.Read(chunkId);
                var groupEntry = ioArchive.ShaderGroupEntries[i];
                
                if (groupEntry.CompressedSize < groupEntry.UncompressedSize)
                {
                    decompressedGroups[i] = DecompressShaderChunk(chunkData, (int)groupEntry.UncompressedSize);
                }
                else
                {
                    decompressedGroups[i] = chunkData;
                }
            }
            
            // Pre-calculate sizes per group
            var groupSlices = new List<List<(int shaderIndex, int offset)>>();
            for(int g=0; g < ioArchive.ShaderGroupEntries.Length; g++) groupSlices.Add(new List<(int, int)>());
            
            for(int i=0; i < ioArchive.ShaderEntries.Length; i++)
            {
                var entryInfo = ioArchive.ShaderEntries[i];
                groupSlices[(int)entryInfo.ShaderGroupIndex].Add((i, (int)entryInfo.UncompressedOffsetInGroup));
            }

            // Flattened list of code bytes
            var finalShaderCode = new byte[ioArchive.ShaderEntries.Length][];
            // Sort slices and extract
            for(int g=0; g < ioArchive.ShaderGroupEntries.Length; g++)
            {
                var slices = groupSlices[g];
                slices.Sort((a,b) => a.offset.CompareTo(b.offset));
                
                var groupData = decompressedGroups[g];
                var totalLen = groupData.Length;
                
                for(int k=0; k < slices.Count; k++)
                {
                    var (sIdx, off) = slices[k];
                    var nextOff = (k == slices.Count - 1) ? totalLen : slices[k+1].offset;
                    var len = nextOff - off;
                    
                    if (len < 0) len = 0; // Error safety
                    
                    var chunk = new byte[len];
                    if (off + len <= groupData.Length)
                    {
                        Array.Copy(groupData, off, chunk, 0, len);
                    }
                    finalShaderCode[sIdx] = chunk;
                }
            }

            long currentOffset = 0;

            // Now write them sequentially
            for(int i=0; i < ioArchive.ShaderEntries.Length; i++)
            {
                var code = finalShaderCode[i];
                // Handle potential null from error
                if (code == null) code = new byte[0]; 

                shaderEntries.Add(new FShaderCodeEntry 
                {
                    Offset = (ulong)currentOffset,
                    Size = (uint)code.Length,
                    UncompressedSize = (uint)code.Length,
                    Frequency = (byte)ioArchive.ShaderEntries[i].Frequency
                });
                
                codeBuffer.Write(code, 0, code.Length);
                currentOffset += code.Length;
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
            
            // Append Code
            codeBuffer.Position = 0;
            codeBuffer.CopyTo(outStream);

            return outStream.ToArray();
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
