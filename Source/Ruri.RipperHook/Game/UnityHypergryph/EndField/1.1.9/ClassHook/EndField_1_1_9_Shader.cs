using AssetRipper.Assets.Generics;
using AssetRipper.IO.Endian;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using Shader_119 = Ruri.SourceGenerated.Classes.ClassID_48.Shader_2021;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    public override void Initialize()
    {
        base.Initialize();
    }

    [RetargetMethod(typeof(Shader_2021_3_12), "ReadRelease")]
    public void Shader_2021_3_1109_ReadRelease(ref EndianSpanReader reader)
    {
        var _this = (object)this as Shader_2021_3_12;

        var dummyThis = new Shader_119(_this.AssetInfo);
        dummyThis.ReadRelease(ref reader);

        ConsolidateSubShaderBlobs(dummyThis);

        ReflectionExtensions.ClassDeepCopy(dummyThis, _this);

        Endfield_1_1_9_GpuType33Transform.Apply(_this, _this.Collection.Version);
    }

    private static void ConsolidateSubShaderBlobs(Shader_119 shader)
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
