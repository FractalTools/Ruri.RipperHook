using AssetRipper.Assets;
using AssetRipper.IO.Endian;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.Core;
using Ruri.SourceGenerated.Classes.ClassID_48;
using System.Reflection;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    public override void Initialize()
    {
        base.Initialize();
    }

    [RetargetMethod(typeof(Shader_2021), "ReadRelease", isBefore: false, isReturn: false)]
    public void Shader_2021_3_1014_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as Shader_2021;
        ConsolidateSubShaderBlobs(_this);
    }

    #region Phase A: Consolidate SubShaderBlobs
    private static void ConsolidateSubShaderBlobs(Shader_2021 shader)
    {
        var compressedBlob = shader.CompressedBlob;
        if (compressedBlob != null && compressedBlob.Length > 0) return;

        var subShaderBlobs = shader.SubShaderBlobs;
        if (subShaderBlobs == null || subShaderBlobs.Count == 0) return;

        var blob0 = subShaderBlobs[0];
        if (blob0 == null) return;

        byte[] blobData = blob0.CompressedBlob;
        if (blobData == null || blobData.Length == 0) return;

        shader.CompressedBlob = blobData;

        CopyNestedArrays(shader.Offsets_AssetList_AssetList_UInt32, blob0.Offsets);
        CopyNestedArrays(shader.CompressedLengths_AssetList_AssetList_UInt32, blob0.CompressedLengths);
        CopyNestedArrays(shader.DecompressedLengths_AssetList_AssetList_UInt32, blob0.DecompressedLengths);

        HookLogger.LogSuccess($"[EndField 1.1.9] Consolidated SubShaderBlobs[0] ({blobData.Length} bytes) → root CompressedBlob");
    }

    private static void CopyNestedArrays(
        dynamic shaderList,
        dynamic blobList)
    {
        if (shaderList == null || blobList == null) return;

        shaderList.Clear();

        int blobCount = blobList.Count;
        for (int i = 0; i < blobCount; i++)
        {
            var innerBlobList = blobList[i];
            if (innerBlobList == null) continue;

            var newInnerList = shaderList.AddNew();
            if (newInnerList == null) continue;

            // 优先走 AddRange
            if (newInnerList is System.Collections.IList && innerBlobList is System.Collections.IEnumerable)
            {
                if (newInnerList.GetType().GetMethod("AddRange") != null)
                {
                    newInnerList.AddRange(innerBlobList);
                    continue;
                }
            }

            // fallback：逐个 Add
            int innerCount = innerBlobList.Count;
            for (int j = 0; j < innerCount; j++)
            {
                newInnerList.Add(innerBlobList[j]);
            }
        }
    }
    #endregion
}
