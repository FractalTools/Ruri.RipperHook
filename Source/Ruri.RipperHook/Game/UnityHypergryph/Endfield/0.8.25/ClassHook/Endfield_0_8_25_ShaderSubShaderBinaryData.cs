using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.IO.Endian;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using Ruri.SourceGenerated.Classes.ClassID_966519959;
using RuriShader = Ruri.SourceGenerated.Classes.ClassID_48.Shader_2021_3_825;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_8_25_Hook
{
    /// <summary>
    /// Shader ReadRelease后置Hook - 捕获SubShaderBinaryData PPtr引用。
    /// 在Ruri dummy读取完成后、ClassDeepCopy之前运行。
    /// 当UseExternalBlobs=true时，shader自身的m_CompressedBlob为空，
    /// 实际的blob数据存储在SubShaderBinaryData资产中。
    /// 此Hook将PPtr信息存储到静态字典中供后续解析使用。
    /// </summary>
    [RetargetMethod(typeof(RuriShader), "ReadRelease", isBefore: false, isReturn: false)]
    public void Shader_ReadRelease_PostHook(ref EndianSpanReader reader)
    {
        var _this = (object)this as RuriShader;
        if (_this == null) return;
        if (!_this.UseExternalBlobs) return;
        if (_this.SubShaderBinaryData == null || _this.SubShaderBinaryData.Count == 0) return;

        // 提取PPtr信息
        var pptrList = new List<(int FileID, long PathID)>();
        foreach (var pptr in _this.SubShaderBinaryData)
        {
            if (pptr.PathID != 0)
            {
                pptrList.Add((pptr.FileID, pptr.PathID));
            }
        }

        if (pptrList.Count == 0) return;

        // 存储到静态字典: key = shader的pathID (从AssetInfo获取)
        // 同时存储AssetCollection引用用于后续解析
        var shaderPathID = _this.PathID;
        var collection = _this.Collection;

        ShaderBinaryDataStore.Store(shaderPathID, collection, pptrList);
    }
}

/// <summary>
/// 静态存储：保存Shader -> SubShaderBinaryData PPtr映射关系。
/// 在Shader的ReadRelease后置Hook中写入，在Shader导出时读取解析。
/// </summary>
public static class ShaderBinaryDataStore
{
    private struct ShaderBinaryDataInfo
    {
        public AssetCollection Collection;
        public List<(int FileID, long PathID)> SubShaderBinaryDataPPtrs;
    }

    // Key = Shader PathID, Value = SubShaderBinaryData引用信息
    private static readonly ConcurrentDictionary<long, ShaderBinaryDataInfo> _store = new();

    public static void Store(long shaderPathID, AssetCollection collection, List<(int FileID, long PathID)> pptrList)
    {
        _store[shaderPathID] = new ShaderBinaryDataInfo
        {
            Collection = collection,
            SubShaderBinaryDataPPtrs = pptrList
        };
        HookLogger.LogRaw($"    [SubShaderBinaryData] Stored {pptrList.Count} PPtr(s) for Shader PathID={shaderPathID}");
    }

    /// <summary>
    /// 尝试为指定Shader解析并填充CompressedBlob。
    /// 在导出时调用。从AssetCollection中查找SubShaderBinaryData的UnknownObject，
    /// 使用Ruri的读取器解析其原始二进制数据，提取CompressedBlob并合并。
    /// </summary>
    /// <param name="shader">AR的IShader对象</param>
    /// <returns>如果成功填充了blob则返回true</returns>
    public static bool TryResolveAndFillBlob(IShader shader)
    {
        if (!_store.TryGetValue(shader.PathID, out var info))
            return false;

        // Shader自身已经有blob数据，不需要填充
        if (shader.CompressedBlob != null && shader.CompressedBlob.Length > 0)
            return false;

        // 使用导出时shader的Collection
        var collection = shader.Collection;

        var allCompressedBlobs = new List<byte[]>();
        var allOffsets = new List<List<uint>>();
        var allCompressedLengths = new List<List<uint>>();
        var allDecompressedLengths = new List<List<uint>>();

        foreach (var (fileID, pathID) in info.SubShaderBinaryDataPPtrs)
        {
            try
            {
                // 关键：UnknownObject继承自NullObject，TryGetAsset会过滤NullObject
                // 必须直接访问Assets字典绕过过滤
                IUnityObjectBase? asset = null;

                if (fileID == 0)
                {
                    // 同一文件内引用 - 直接从Assets字典查找
                    collection.Assets.TryGetValue(pathID, out asset);
                }
                else
                {
                    // 跨文件引用 - 从依赖的collection的Assets字典查找
                    if (fileID < collection.Dependencies.Count)
                    {
                        var depCollection = collection.Dependencies[fileID];
                        depCollection?.Assets.TryGetValue(pathID, out asset);
                    }
                }

                if (asset == null)
                {
                    HookLogger.LogWarning($"    [SubShaderBinaryData] Cannot resolve PPtr FileID={fileID} PathID={pathID}");
                    continue;
                }

                // 获取原始二进制数据
                byte[]? rawData = null;

                // 尝试通过反射获取RawData（UnknownObject : RawDataObject）
                var rawDataProp = asset.GetType().GetProperty("RawData");
                if (rawDataProp != null)
                {
                    rawData = rawDataProp.GetValue(asset) as byte[];
                }

                if (rawData == null || rawData.Length == 0)
                {
                    HookLogger.LogWarning($"    [SubShaderBinaryData] No raw data for PathID={pathID}");
                    continue;
                }

                // 使用Ruri的SubShaderBinaryData读取器解析
                var subShaderBinaryData = SubShaderBinaryData.Create(asset.AssetInfo);
                var subReader = new EndianSpanReader(rawData, EndianType.LittleEndian);
                subShaderBinaryData.ReadRelease(ref subReader);

                if (subShaderBinaryData.CompressedBlob == null || subShaderBinaryData.CompressedBlob.Length == 0)
                {
                    HookLogger.LogWarning($"    [SubShaderBinaryData] Empty CompressedBlob for PathID={pathID}");
                    continue;
                }

                allCompressedBlobs.Add(subShaderBinaryData.CompressedBlob);

                // 收集offsets/lengths
                for (int i = 0; i < subShaderBinaryData.Offsets.Count; i++)
                {
                    var offsets = new List<uint>();
                    var compLengths = new List<uint>();
                    var decompLengths = new List<uint>();

                    foreach (var o in subShaderBinaryData.Offsets[i]) offsets.Add(o);
                    foreach (var l in subShaderBinaryData.CompressedLengths[i]) compLengths.Add(l);
                    foreach (var l in subShaderBinaryData.DecompressedLengths[i]) decompLengths.Add(l);

                    allOffsets.Add(offsets);
                    allCompressedLengths.Add(compLengths);
                    allDecompressedLengths.Add(decompLengths);
                }

                HookLogger.LogSuccess($"    [SubShaderBinaryData] Resolved PathID={pathID}, blob size={subShaderBinaryData.CompressedBlob.Length}");
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"    [SubShaderBinaryData] Error resolving PathID={pathID}: {ex.Message}");
            }
        }

        if (allCompressedBlobs.Count == 0)
            return false;

        // 合并所有blob为一个master blob，调整offsets
        using var masterStream = new System.IO.MemoryStream();
        uint currentOffset = 0;

        for (int blobIdx = 0; blobIdx < allCompressedBlobs.Count; blobIdx++)
        {
            var blobData = allCompressedBlobs[blobIdx];
            uint blobStartOffset = (uint)masterStream.Position;

            masterStream.Write(blobData, 0, blobData.Length);

            // 调整这个blob对应的所有offset组
            // 因为每个SubShaderBinaryData的offsets是相对于自己的CompressedBlob的起始位置
            // 合并后需要加上当前在master blob中的偏移
            // 但是offsets/lengths是按平台分组的，一个SubShaderBinaryData可能对应多个平台
            // 我们需要知道这个blobIdx对应的offset组范围
        }

        // 由于每个SubShaderBinaryData有独立的offset体系，
        // 最简单的方式是取第一个（通常只有一个）SubShaderBinaryData的数据直接使用
        if (allCompressedBlobs.Count == 1)
        {
            shader.CompressedBlob = allCompressedBlobs[0];

            // 填充offsets/lengths到shader
            if (shader.Offsets_AssetList_AssetList_UInt32 != null)
            {
                shader.Offsets_AssetList_AssetList_UInt32.Clear();
                shader.CompressedLengths_AssetList_AssetList_UInt32.Clear();
                shader.DecompressedLengths_AssetList_AssetList_UInt32.Clear();

                for (int i = 0; i < allOffsets.Count; i++)
                {
                    var offsetList = new AssetList<uint>();
                    foreach (var o in allOffsets[i]) offsetList.Add(o);
                    shader.Offsets_AssetList_AssetList_UInt32.AddNew().AddRange(allOffsets[i]);

                    var compList = new AssetList<uint>();
                    foreach (var l in allCompressedLengths[i]) compList.Add(l);
                    shader.CompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(allCompressedLengths[i]);

                    var decompList = new AssetList<uint>();
                    foreach (var l in allDecompressedLengths[i]) decompList.Add(l);
                    shader.DecompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(allDecompressedLengths[i]);
                }
            }

            HookLogger.LogSuccess($"    [SubShaderBinaryData] Filled single blob ({allCompressedBlobs[0].Length} bytes) into Shader");
            return true;
        }

        // 多个SubShaderBinaryData: 合并blobs并调整offsets
        byte[] mergedBlob;
        using (var mergeStream = new System.IO.MemoryStream())
        {
            var mergedOffsets = new List<List<uint>>();
            var mergedCompLengths = new List<List<uint>>();
            var mergedDecompLengths = new List<List<uint>>();

            int offsetGroupIdx = 0;
            for (int blobIdx = 0; blobIdx < allCompressedBlobs.Count; blobIdx++)
            {
                uint blobBaseOffset = (uint)mergeStream.Position;
                mergeStream.Write(allCompressedBlobs[blobIdx], 0, allCompressedBlobs[blobIdx].Length);

                // 找出这个blob对应的offset组数量
                // 每个SubShaderBinaryData的Offsets.Count就是平台数
                // 但我们在收集时是按顺序flat添加的，需要重新调整
                // 由于SubShaderBinaryData的offsets是相对于其自身CompressedBlob起始
                // 而合并后需要相对于mergedBlob起始，所以需要加blobBaseOffset
                while (offsetGroupIdx < allOffsets.Count)
                {
                    var adjustedOffsets = new List<uint>();
                    foreach (var o in allOffsets[offsetGroupIdx])
                    {
                        adjustedOffsets.Add(o + blobBaseOffset);
                    }
                    mergedOffsets.Add(adjustedOffsets);
                    mergedCompLengths.Add(allCompressedLengths[offsetGroupIdx]);
                    mergedDecompLengths.Add(allDecompressedLengths[offsetGroupIdx]);
                    offsetGroupIdx++;

                    // 检查是否到了下一个blob的范围（这里需要更精确的逻辑）
                    // 简化处理：假设offset groups均匀分布
                    break; // TODO: 需要根据实际SubShaderBinaryData的offset组数来分
                }
            }

            mergedBlob = mergeStream.ToArray();

            shader.CompressedBlob = mergedBlob;

            if (shader.Offsets_AssetList_AssetList_UInt32 != null)
            {
                shader.Offsets_AssetList_AssetList_UInt32.Clear();
                shader.CompressedLengths_AssetList_AssetList_UInt32.Clear();
                shader.DecompressedLengths_AssetList_AssetList_UInt32.Clear();

                for (int i = 0; i < mergedOffsets.Count; i++)
                {
                    shader.Offsets_AssetList_AssetList_UInt32.AddNew().AddRange(mergedOffsets[i]);
                    shader.CompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(mergedCompLengths[i]);
                    shader.DecompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(mergedDecompLengths[i]);
                }
            }
        }

        HookLogger.LogSuccess($"    [SubShaderBinaryData] Merged {allCompressedBlobs.Count} blobs ({mergedBlob.Length} bytes) into Shader");
        return true;
    }

    /// <summary>
    /// 检查指定shader是否有待解析的SubShaderBinaryData引用
    /// </summary>
    public static bool HasPendingData(long shaderPathID)
    {
        return _store.ContainsKey(shaderPathID);
    }

    public static void Clear()
    {
        _store.Clear();
    }
}
