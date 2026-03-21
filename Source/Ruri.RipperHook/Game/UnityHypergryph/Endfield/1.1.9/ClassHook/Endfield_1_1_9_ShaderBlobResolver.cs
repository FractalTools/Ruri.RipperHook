using AssetRipper.Assets.Generics;
using AssetRipper.IO.Endian;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.Core;
using Ruri.SourceGenerated.Classes.ClassID_48;

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

        CopyNestedUIntArrays(shader.Offsets_AssetList_AssetList_UInt32, blob0.Offsets);
        CopyNestedUIntArrays(shader.CompressedLengths_AssetList_AssetList_UInt32, blob0.CompressedLengths);
        CopyNestedUIntArrays(shader.DecompressedLengths_AssetList_AssetList_UInt32, blob0.DecompressedLengths);

        HookLogger.LogSuccess($"[EndField 1.1.9] Consolidated SubShaderBlobs[0] ({blobData.Length} bytes) → root CompressedBlob");
    }

    private static void CopyNestedUIntArrays(AssetList<AssetList<uint>> target, AssetList<AssetList<uint>> source)
    {
        target.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            var inner = target.AddNew();
            inner.AddRange(source[i]);
        }
    }
}
