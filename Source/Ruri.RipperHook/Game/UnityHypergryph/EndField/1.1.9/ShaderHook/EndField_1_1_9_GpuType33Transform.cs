using AssetRipper.Export.Modules.Shaders.Extensions;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using K4os.Compression.LZ4;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Ruri.RipperHook.Endfield;

internal static class Endfield_1_1_9_GpuType33Transform
{
    public static bool IsEnabled { get; set; } = true;

    private const sbyte GpuType_Custom33 = 33;
    private const sbyte GpuType_DX11VertexSM40 = 15;
    private const sbyte GpuType_DX11PixelSM40 = 17;
    private const int Platform_D3D11 = 4;
    private const int StandardBlobVersion = 202012090;

    private const int PD_Offset2 = 0x04;
    private const int PD_Size2 = 0x08;
    private const int PD_Offset1 = 0x0C;
    private const int PD_Size1 = 0x10;

    public static void Apply(IShader shader, UnityVersion version)
    {
        if (!IsEnabled) return;
        if (!HasGpuType33(shader)) return;

        Console.WriteLine("[GpuType33Transform] Detected custom GpuType=33 entries, applying full blob reconstruction...");

        int d3d11Index = FindPlatformIndex(shader);
        if (d3d11Index < 0)
        {
            Console.WriteLine("[GpuType33Transform] No d3d11 platform found, skipping");
            return;
        }

        var (splitMap, reindexMap) = ReconstructAllBlobsAndSplitD3D11(shader, d3d11Index, version);

        TransformPlayerSubPrograms(shader, splitMap, reindexMap);
    }

    #region Detection

    private static bool HasGpuType33(IShader shader)
    {
        dynamic dynShader = shader;
        if (dynShader.ParsedForm?.SubShaders == null) return false;

        foreach (var subShader in dynShader.ParsedForm.SubShaders)
        {
            foreach (var pass in subShader.Passes)
            {
                var pspOuter = pass.ProgVertex?.PlayerSubPrograms;
                if (pspOuter == null || pspOuter.Count == 0) continue;

                var lastInner = pspOuter[pspOuter.Count - 1];
                foreach (var entry in lastInner)
                {
                    if (entry.GpuProgramType == GpuType_Custom33)
                        return true;
                }
            }
        }
        return false;
    }

    private static int FindPlatformIndex(IShader shader)
    {
        dynamic dynShader = shader;
        var platforms = dynShader.Platforms;
        for (int i = 0; i < platforms.Count; i++)
        {
            if ((int)platforms[i] == Platform_D3D11) return i;
        }
        return -1;
    }

    #endregion

    #region Blob Reconstruction (修复多Chunk合并越界)

    private static (Dictionary<int, (int vsIdx, int psIdx)> splitMap, Dictionary<int, int> reindexMap)
        ReconstructAllBlobsAndSplitD3D11(IShader shader, int d3d11Index, UnityVersion version)
    {
        var blobs = shader.ReadBlobs();

        var allCompressedBlobs = new List<byte[]>();
        var finalOffsets = new List<uint>();
        var finalCompressedLengths = new List<uint>();
        var finalDecompressedLengths = new List<uint>();
        uint currentOffset = 0;

        bool hasSegment = version.GreaterThanOrEquals(2019, 3);
        int entrySize = hasSegment ? 12 : 8;

        var splitMap = new Dictionary<int, (int vsIdx, int psIdx)>();
        var reindexMap = new Dictionary<int, int>();

        for (int i = 0; i < blobs.Length; i++)
        {
            var blob = blobs[i];
            byte[] finalDecompressedBlob;

            var entries = blob.Entries;
            if (entries == null || entries.Length == 0)
            {
                // 处理无代码编译的情况
                finalDecompressedBlob = new byte[4];
            }
            else if (i == d3d11Index)
            {
                // 【D3D11 平台】提取并拆分 GpuType33
                var decompressedBlobSegments = GetSegments(blob);
                var newEntryData = new List<byte[]>();

                for (int j = 0; j < entries.Length; j++)
                {
                    var entryObj = entries[j];
                    byte[] segmentData = (byte[])decompressedBlobSegments[entryObj.Segment]!;
                    byte[] rawEntry = new byte[entryObj.Length];
                    Array.Copy(segmentData, entryObj.Offset, rawEntry, 0, entryObj.Length);

                    int progType = BitConverter.ToInt32(rawEntry, 4);
                    if (progType == GpuType_Custom33)
                    {
                        var (vsEntry, psEntry) = SplitCustomEntry(rawEntry, version);
                        splitMap[j] = (newEntryData.Count, newEntryData.Count + 1);
                        newEntryData.Add(vsEntry);
                        newEntryData.Add(psEntry);
                    }
                    else
                    {
                        reindexMap[j] = newEntryData.Count;
                        newEntryData.Add(rawEntry);
                    }
                }
                // RebuildBlobBinary 内部已经固定给所有新生成的 entry 写入 Segment = 0
                finalDecompressedBlob = RebuildBlobBinary(newEntryData, hasSegment);
            }
            else
            {
                // 【其他平台】执行合并重构，消除原本的多 Segment
                var decompressedBlobSegments = GetSegments(blob);
                using var decompressedStream = new MemoryStream();
                using var writer = new BinaryWriter(decompressedStream);

                int headerSize = 4 + entries.Length * entrySize;
                writer.BaseStream.Position = headerSize;

                var segmentStartOffsets = new long[decompressedBlobSegments.Count];
                for (int j = 0; j < decompressedBlobSegments.Count; j++)
                {
                    segmentStartOffsets[j] = writer.BaseStream.Position;
                    writer.Write((byte[])decompressedBlobSegments[j]!);
                }

                writer.BaseStream.Position = 0;
                writer.Write(entries.Length);

                foreach (var entryObj in entries)
                {
                    long absoluteOffset = segmentStartOffsets[entryObj.Segment] + entryObj.Offset;
                    writer.Write((int)absoluteOffset);
                    writer.Write(entryObj.Length);

                    // 【致命错误修复点】：因为之前的数据已经全部合并写入单一 Stream
                    // 这里必须强行将 Segment ID 写为 0，否则后续读取会越界
                    if (hasSegment) writer.Write(0);
                }
                finalDecompressedBlob = decompressedStream.ToArray();
            }

            // 压紧
            byte[] compressedPlatformBlob = new byte[LZ4Codec.MaximumOutputSize(finalDecompressedBlob.Length)];
            int compressedSize = LZ4Codec.Encode(finalDecompressedBlob, compressedPlatformBlob, LZ4Level.L00_FAST);
            Array.Resize(ref compressedPlatformBlob, compressedSize);

            allCompressedBlobs.Add(compressedPlatformBlob);
            finalOffsets.Add(currentOffset);
            finalCompressedLengths.Add((uint)compressedPlatformBlob.Length);
            finalDecompressedLengths.Add((uint)finalDecompressedBlob.Length);

            currentOffset += (uint)compressedPlatformBlob.Length;
        }

        // 把一切重写回原生底层的 MasterBlob
        using var masterBlobStream = new MemoryStream();
        foreach (var compressedBlob in allCompressedBlobs)
        {
            masterBlobStream.Write(compressedBlob, 0, compressedBlob.Length);
        }

        dynamic dynShader = shader;
        dynShader.CompressedBlob = masterBlobStream.ToArray();

        // 强行清理旧的多 Chunk 碎片，转为完美单 Chunk 格式
        ReplaceNestedArray(dynShader.Offsets_AssetList_AssetList_UInt32, finalOffsets);
        ReplaceNestedArray(dynShader.CompressedLengths_AssetList_AssetList_UInt32, finalCompressedLengths);
        ReplaceNestedArray(dynShader.DecompressedLengths_AssetList_AssetList_UInt32, finalDecompressedLengths);

        Console.WriteLine("[GpuType33Transform] Successfully reconstructed all blobs into single-chunk format.");
        return (splitMap, reindexMap);
    }

    private static IList GetSegments(object blob)
    {
        var field = blob.GetType().GetField("m_decompressedBlobSegments", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return (IList)field!.GetValue(blob)!;
    }

    private static void ReplaceNestedArray(dynamic outerList, List<uint> newSingleValues)
    {
        outerList.Clear();
        for (int i = 0; i < newSingleValues.Count; i++)
        {
            var inner = outerList.AddNew();
            inner.Add(newSingleValues[i]);
        }
    }

    #endregion

    #region Binary Helpers

    private static (byte[] vsEntry, byte[] psEntry) SplitCustomEntry(byte[] entryData, UnityVersion version)
    {
        using var reader = new BinaryReader(new MemoryStream(entryData));
        int blobVersion = reader.ReadInt32();
        int programType = reader.ReadInt32();
        int statsALU = reader.ReadInt32();
        int statsTEX = reader.ReadInt32();
        int statsFlow = reader.ReadInt32();
        int statsTempReg = reader.ReadInt32();

        int keywordCount = reader.ReadInt32();
        var keywordBytes = new List<byte[]>();
        for (int i = 0; i < keywordCount; i++)
        {
            int len = reader.ReadInt32();
            keywordBytes.Add(reader.ReadBytes(len));
            int pad = (4 - ((int)reader.BaseStream.Position % 4)) % 4;
            if (pad > 0) reader.ReadBytes(pad);
        }

        int programDataSize = reader.ReadInt32();
        byte[] programData = reader.ReadBytes(programDataSize);
        int pdPad = (4 - ((int)reader.BaseStream.Position % 4)) % 4;
        if (pdPad > 0) reader.ReadBytes(pdPad);

        byte[] tail = reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position));

        int offset1 = BitConverter.ToInt32(programData, PD_Offset1);
        int size1 = BitConverter.ToInt32(programData, PD_Size1);
        int offset2 = BitConverter.ToInt32(programData, PD_Offset2);
        int size2 = BitConverter.ToInt32(programData, PD_Size2);

        byte[] vsDxbc = new byte[size1];
        Array.Copy(programData, offset1, vsDxbc, 0, size1);
        byte[] psDxbc = new byte[size2];
        Array.Copy(programData, offset2, psDxbc, 0, size2);

        byte[] vsProgData = new byte[6 + vsDxbc.Length];
        vsProgData[0] = 0x01;
        Array.Copy(vsDxbc, 0, vsProgData, 6, vsDxbc.Length);

        byte[] psProgData = new byte[6 + psDxbc.Length];
        psProgData[0] = 0x01;
        Array.Copy(psDxbc, 0, psProgData, 6, psDxbc.Length);

        return (
            BuildSubProgramEntry(StandardBlobVersion, GpuType_DX11VertexSM40, statsALU, statsTEX, statsFlow, statsTempReg, keywordBytes, vsProgData, tail),
            BuildSubProgramEntry(StandardBlobVersion, GpuType_DX11PixelSM40, statsALU, statsTEX, statsFlow, statsTempReg, keywordBytes, psProgData, tail)
        );
    }

    private static byte[] BuildSubProgramEntry(int blobVersion, int programType, int statsALU, int statsTEX, int statsFlow, int statsTempReg, List<byte[]> keywordBytes, byte[] programData, byte[] tail)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(blobVersion); writer.Write(programType); writer.Write(statsALU); writer.Write(statsTEX); writer.Write(statsFlow); writer.Write(statsTempReg);
        writer.Write(keywordBytes.Count);
        foreach (var kw in keywordBytes)
        {
            writer.Write(kw.Length); writer.Write(kw);
            int pad = (4 - ((int)ms.Position % 4)) % 4;
            for (int i = 0; i < pad; i++) writer.Write((byte)0);
        }
        writer.Write(programData.Length); writer.Write(programData);
        int pdPad = (4 - ((int)ms.Position % 4)) % 4;
        for (int i = 0; i < pdPad; i++) writer.Write((byte)0);
        writer.Write(tail);
        return ms.ToArray();
    }

    private static byte[] RebuildBlobBinary(List<byte[]> entries, bool hasSegment)
    {
        int entryFieldSize = hasSegment ? 12 : 8;
        int headerSize = 4 + entries.Count * entryFieldSize;
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.BaseStream.Position = headerSize;
        var entryOffsets = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            entryOffsets[i] = (int)writer.BaseStream.Position;
            writer.Write(entries[i]);
        }
        writer.BaseStream.Position = 0;
        writer.Write(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            writer.Write(entryOffsets[i]);
            writer.Write(entries[i].Length);
            // 这里之前就是硬编码的 0，这也是为什么 D3D11 没有报 Segment 越界的原因
            if (hasSegment) writer.Write(0);
        }
        return ms.ToArray();
    }

    #endregion

    #region PlayerSubPrograms Transform

    private static void TransformPlayerSubPrograms(
        IShader shader,
        Dictionary<int, (int vsIdx, int psIdx)> splitMap,
        Dictionary<int, int> reindexMap)
    {
        dynamic dynShader = shader;
        foreach (var subShader in dynShader.ParsedForm.SubShaders)
        {
            foreach (var pass in subShader.Passes)
            {
                var progVertex = pass.ProgVertex;
                var progFragment = pass.ProgFragment;

                if (progVertex == null) continue;

                var vtxOuter = progVertex.PlayerSubPrograms;
                if (vtxOuter == null || vtxOuter.Count == 0) continue;

                var vtxLastInner = vtxOuter[vtxOuter.Count - 1];
                if (vtxLastInner == null || vtxLastInner.Count == 0) continue;

                var psEntriesToBuild = new List<(uint blobIdx, List<ushort> keywords)>();

                foreach (var entry in vtxLastInner)
                {
                    int oldBlobIndex = (int)entry.BlobIndex;

                    if ((sbyte)entry.GpuProgramType == GpuType_Custom33)
                    {
                        if (!splitMap.TryGetValue(oldBlobIndex, out var mapping)) continue;

                        entry.GpuProgramType = GpuType_DX11VertexSM40;
                        entry.BlobIndex = (uint)mapping.vsIdx;

                        var kws = new List<ushort>();
                        foreach (var kw in entry.KeywordIndices) kws.Add((ushort)kw);
                        psEntriesToBuild.Add(((uint)mapping.psIdx, kws));
                    }
                    else if (reindexMap.TryGetValue(oldBlobIndex, out int newIdx))
                    {
                        entry.BlobIndex = (uint)newIdx;
                    }
                }

                if (progVertex.ParameterBlobIndices != null)
                {
                    ReindexParameterBlobIndices(progVertex.ParameterBlobIndices, reindexMap);
                }

                if (psEntriesToBuild.Count > 0)
                {
                    progFragment.PlayerSubPrograms.Clear();
                    for (int i = 0; i < vtxOuter.Count; i++)
                    {
                        var fragInner = progFragment.PlayerSubPrograms.AddNew();
                        if (i == vtxOuter.Count - 1)
                        {
                            foreach (var psData in psEntriesToBuild)
                            {
                                var newEntry = fragInner.AddNew();
                                newEntry.GpuProgramType = GpuType_DX11PixelSM40;
                                newEntry.BlobIndex = psData.blobIdx;
                                foreach (var kw in psData.keywords) newEntry.KeywordIndices.Add(kw);
                            }
                        }
                    }

                    if (progVertex.ParameterBlobIndices != null)
                    {
                        progFragment.ParameterBlobIndices.Clear();
                        CloneParameterBlobIndices(progVertex.ParameterBlobIndices, progFragment.ParameterBlobIndices);
                    }
                }
            }
        }
    }

    private static void ReindexParameterBlobIndices(dynamic paramIndices, Dictionary<int, int> reindexMap)
    {
        foreach (var innerVec in paramIndices)
        {
            for (int i = 0; i < innerVec.Count; i++)
            {
                if (reindexMap.TryGetValue((int)innerVec[i], out int newIdx))
                {
                    innerVec[i] = (uint)newIdx;
                }
            }
        }
    }

    private static void CloneParameterBlobIndices(dynamic source, dynamic target)
    {
        target.Clear();
        foreach (var innerSrc in source)
        {
            var innerTarget = target.AddNew();
            foreach (var val in innerSrc) innerTarget.Add((uint)val);
        }
    }

    #endregion
}