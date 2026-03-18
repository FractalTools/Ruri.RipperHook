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

                // 用户要求：先不考虑LOD全部支持，只取最高质量的LOD块（第一个块）回填到Shader中
                // 避免合并多个块导致偏移量(Offsets)和长度计算错误，进而引发解包器报错
                shader.CompressedBlob = subShaderBinaryData.CompressedBlob;

                if (shader.Offsets_AssetList_AssetList_UInt32 != null)
                {
                    shader.Offsets_AssetList_AssetList_UInt32.Clear();
                    shader.CompressedLengths_AssetList_AssetList_UInt32.Clear();
                    shader.DecompressedLengths_AssetList_AssetList_UInt32.Clear();

                    for (int i = 0; i < subShaderBinaryData.Offsets.Count; i++)
                    {
                        shader.Offsets_AssetList_AssetList_UInt32.AddNew().AddRange(subShaderBinaryData.Offsets[i]);
                        shader.CompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(subShaderBinaryData.CompressedLengths[i]);
                        shader.DecompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(subShaderBinaryData.DecompressedLengths[i]);
                    }
                }

                HookLogger.LogSuccess($"    [SubShaderBinaryData] Filled single LOD0 blob ({subShaderBinaryData.CompressedBlob.Length} bytes) from PathID={pathID} into Shader");

                // === 临时调试：Dump blob到文件 ===
                try
                {
                    var shaderName = (shader as AssetRipper.Assets.INamed)?.Name?.ToString() ?? $"PathID_{shader.PathID}";
                    // 清理文件名非法字符
                    foreach (var c in System.IO.Path.GetInvalidFileNameChars()) shaderName = shaderName.Replace(c, '_');
                    var dumpDir = $@"D:\RuriDebug\{shaderName}";
                    System.IO.Directory.CreateDirectory(dumpDir);

                    // 1. Dump原始压缩blob
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dumpDir, "compressed_blob.raw"), subShaderBinaryData.CompressedBlob);

                    // 2. 完整信息dump
                    using (var sw = new System.IO.StreamWriter(System.IO.Path.Combine(dumpDir, "blob_info.txt")))
                    {
                        sw.WriteLine("=== SubShaderBinaryData Info ===");
                        sw.WriteLine($"SubShaderBinaryData.Name = {subShaderBinaryData.Name_R}");
                        sw.WriteLine($"SubShaderBinaryData PathID = {pathID}");
                        sw.WriteLine($"CompressedBlob.Length = {subShaderBinaryData.CompressedBlob.Length}");
                        sw.WriteLine($"Offsets.Count (platforms) = {subShaderBinaryData.Offsets.Count}");

                        for (int pi = 0; pi < subShaderBinaryData.Offsets.Count; pi++)
                        {
                            sw.WriteLine($"\n--- Platform[{pi}] ({subShaderBinaryData.Offsets[pi].Count} segments) ---");
                            for (int si = 0; si < subShaderBinaryData.Offsets[pi].Count; si++)
                            {
                                sw.WriteLine($"  [{si}] offset={subShaderBinaryData.Offsets[pi][si]}, compLen={subShaderBinaryData.CompressedLengths[pi][si]}, decompLen={subShaderBinaryData.DecompressedLengths[pi][si]}");
                            }
                        }

                        sw.WriteLine("\n=== AR Shader State After Fill ===");
                        sw.WriteLine($"Shader.PathID = {shader.PathID}");
                        sw.WriteLine($"Shader.Name = {shaderName}");
                        sw.WriteLine($"Shader.CompressedBlob.Length = {shader.CompressedBlob?.Length ?? -1}");
                        sw.WriteLine($"Has_CompressedLengths_AssetList_AssetList_UInt32 = {shader.Has_CompressedLengths_AssetList_AssetList_UInt32()}");
                        sw.WriteLine($"Has_CompressedLengths_AssetList_UInt32 = {shader.Has_CompressedLengths_AssetList_UInt32()}");
                        sw.WriteLine($"Has_CompressedBlob = {shader.Has_CompressedBlob()}");

                        if (shader.Has_CompressedLengths_AssetList_AssetList_UInt32())
                        {
                            sw.WriteLine($"Offsets_AssetList_AssetList count = {shader.Offsets_AssetList_AssetList_UInt32?.Count ?? -1}");
                            if (shader.Offsets_AssetList_AssetList_UInt32 != null)
                            {
                                for (int pi = 0; pi < shader.Offsets_AssetList_AssetList_UInt32.Count; pi++)
                                {
                                    sw.WriteLine($"  Platform[{pi}]: {shader.Offsets_AssetList_AssetList_UInt32[pi].Count} segments");
                                    for (int si = 0; si < shader.Offsets_AssetList_AssetList_UInt32[pi].Count; si++)
                                    {
                                        sw.WriteLine($"    [{si}] offset={shader.Offsets_AssetList_AssetList_UInt32[pi][si]}, compLen={shader.CompressedLengths_AssetList_AssetList_UInt32[pi][si]}, decompLen={shader.DecompressedLengths_AssetList_AssetList_UInt32[pi][si]}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            sw.WriteLine("WARNING: Has_CompressedLengths_AssetList_AssetList_UInt32 is FALSE!");
                            sw.WriteLine("ReadBlobs will NOT use the nested AssetList path!");
                        }

                        // Dump segment 0 entries table
                        sw.WriteLine("\n=== Segment 0 Entries Table Parse ===");
                        if (subShaderBinaryData.Offsets.Count > 0 && subShaderBinaryData.Offsets[0].Count > 0)
                        {
                            try
                            {
                                uint s0Off = subShaderBinaryData.Offsets[0][0];
                                uint s0CompLen = subShaderBinaryData.CompressedLengths[0][0];
                                uint s0DecompLen = subShaderBinaryData.DecompressedLengths[0][0];
                                var s0Decompressed = new byte[s0DecompLen];
                                K4os.Compression.LZ4.LZ4Codec.Decode(
                                    subShaderBinaryData.CompressedBlob, (int)s0Off, (int)s0CompLen,
                                    s0Decompressed, 0, (int)s0DecompLen);

                                // 读取entries count (第一个int32)
                                int entryCount = BitConverter.ToInt32(s0Decompressed, 0);
                                sw.WriteLine($"Entry count = {entryCount}");
                                // 每个Entry: offset(4) + length(4) + segment(4) = 12 bytes (2019.3+)
                                int entrySize = 12; // offset + length + segment
                                for (int ei = 0; ei < Math.Min(entryCount, 20); ei++)
                                {
                                    int baseOff = 4 + ei * entrySize;
                                    if (baseOff + entrySize > s0Decompressed.Length) break;
                                    int eOffset = BitConverter.ToInt32(s0Decompressed, baseOff);
                                    int eLength = BitConverter.ToInt32(s0Decompressed, baseOff + 4);
                                    int eSegment = BitConverter.ToInt32(s0Decompressed, baseOff + 8);
                                    sw.WriteLine($"  Entry[{ei}]: offset={eOffset}, length={eLength}, segment={eSegment}");
                                }
                                if (entryCount > 20) sw.WriteLine($"  ... ({entryCount - 20} more entries)");
                            }
                            catch (Exception ex)
                            {
                                sw.WriteLine($"Failed to parse: {ex.Message}");
                            }
                        }
                    }

                    // 3. Dump每个解压后的sub-blob
                    for (int pi = 0; pi < subShaderBinaryData.Offsets.Count; pi++)
                    {
                        for (int si = 0; si < subShaderBinaryData.Offsets[pi].Count; si++)
                        {
                            uint off = subShaderBinaryData.Offsets[pi][si];
                            uint compLen = subShaderBinaryData.CompressedLengths[pi][si];
                            uint decompLen = subShaderBinaryData.DecompressedLengths[pi][si];

                            if (off + compLen > (uint)subShaderBinaryData.CompressedBlob.Length)
                            {
                                HookLogger.LogWarning($"    [SubShaderBinaryData] Skipping dump p{pi}_s{si}: offset({off})+compLen({compLen}) > blobLen({subShaderBinaryData.CompressedBlob.Length})");
                                continue;
                            }

                            try
                            {
                                var decompressed = new byte[decompLen];
                                K4os.Compression.LZ4.LZ4Codec.Decode(
                                    subShaderBinaryData.CompressedBlob, (int)off, (int)compLen,
                                    decompressed, 0, (int)decompLen);
                                System.IO.File.WriteAllBytes(
                                    System.IO.Path.Combine(dumpDir, $"decompressed_p{pi}_s{si}.raw"), decompressed);
                            }
                            catch (Exception dex)
                            {
                                HookLogger.LogWarning($"    [SubShaderBinaryData] Decompress failed p{pi}_s{si}: {dex.Message}");
                            }
                        }
                    }

                    HookLogger.LogSuccess($"    [SubShaderBinaryData] Dump complete: {dumpDir}");
                }
                catch (Exception dumpEx)
                {
                    HookLogger.LogFailure($"    [SubShaderBinaryData] Dump failed: {dumpEx.Message}");
                }
                // === 临时调试结束 ===

                return true; // 成功解析并回填第一个高质量LOD块后直接返回
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"    [SubShaderBinaryData] Error resolving PathID={pathID}: {ex.Message}");
            }
        }

        return false;
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
