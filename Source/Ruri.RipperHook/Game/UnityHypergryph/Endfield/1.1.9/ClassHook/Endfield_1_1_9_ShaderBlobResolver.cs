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
    private static void ConsolidateSubShaderBlobs(IUnityObjectBase shader)
    {
        var type = shader.GetType();
        var bindFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var compressedBlobProp = type.GetProperty("CompressedBlob", bindFlags);
        if (compressedBlobProp == null) return;

        var compressedBlob = compressedBlobProp.GetValue(shader) as byte[];
        if (compressedBlob != null && compressedBlob.Length > 0) return; // Already filled

        var subShaderBlobsProp = type.GetProperty("SubShaderBlobs", bindFlags);
        if (subShaderBlobsProp == null) return;

        var subShaderBlobs = subShaderBlobsProp.GetValue(shader);
        if (subShaderBlobs == null) return;

        var countProp = subShaderBlobs.GetType().GetProperty("Count");
        if (countProp == null) return;

        int count = (int)countProp.GetValue(subShaderBlobs)!;
        if (count == 0) return;

        var indexer = subShaderBlobs.GetType().GetProperty("Item");
        if (indexer == null) return;

        var blob0 = indexer.GetValue(subShaderBlobs, new object[] { 0 });
        if (blob0 == null) return;

        var blob0Type = blob0.GetType();

        var blobCompressedBlobProp = blob0Type.GetProperty("CompressedBlob", bindFlags)
            ?? blob0Type.GetProperty("M_CompressedBlob", bindFlags);

        byte[]? blobData = null;
        if (blobCompressedBlobProp != null)
            blobData = blobCompressedBlobProp.GetValue(blob0) as byte[];
        else
        {
            var blobField = blob0Type.GetField("CompressedBlob", bindFlags)
                ?? blob0Type.GetField("m_CompressedBlob", bindFlags);
            if (blobField != null)
                blobData = blobField.GetValue(blob0) as byte[];
        }

        if (blobData == null || blobData.Length == 0) return;

        compressedBlobProp.SetValue(shader, blobData);

        CopyNestedArrays(shader, blob0, type, blob0Type, bindFlags,
            "Offsets_AssetList_AssetList_UInt32", "Offsets");
        CopyNestedArrays(shader, blob0, type, blob0Type, bindFlags,
            "CompressedLengths_AssetList_AssetList_UInt32", "CompressedLengths");
        CopyNestedArrays(shader, blob0, type, blob0Type, bindFlags,
            "DecompressedLengths_AssetList_AssetList_UInt32", "DecompressedLengths");

        HookLogger.LogSuccess($"[EndField 1.1.9] Consolidated SubShaderBlobs[0] ({blobData.Length} bytes) → root CompressedBlob");
    }

    private static void CopyNestedArrays(
        object shader, object blob,
        Type shaderType, Type blobType,
        BindingFlags bindFlags,
        string shaderPropName, string blobPropName)
    {
        var shaderProp = shaderType.GetProperty(shaderPropName, bindFlags)
            ?? shaderType.GetProperty(blobPropName, bindFlags);
        if (shaderProp == null) return;

        var shaderList = shaderProp.GetValue(shader);
        if (shaderList == null) return;

        var blobProp = blobType.GetProperty(blobPropName, bindFlags)
            ?? blobType.GetProperty(shaderPropName, bindFlags)
            ?? blobType.GetProperty($"M_{blobPropName}", bindFlags);
        if (blobProp == null) return;

        var blobList = blobProp.GetValue(blob);
        if (blobList == null) return;

        shaderList.GetType().GetMethod("Clear")?.Invoke(shaderList, null);

        var blobCountProp = blobList.GetType().GetProperty("Count");
        if (blobCountProp == null) return;
        int blobCount = (int)blobCountProp.GetValue(blobList)!;

        var blobIndexer = blobList.GetType().GetProperty("Item");
        if (blobIndexer == null) return;

        var addNewMethod = shaderList.GetType().GetMethod("AddNew");
        if (addNewMethod == null) return;

        for (int i = 0; i < blobCount; i++)
        {
            var innerBlobList = blobIndexer.GetValue(blobList, new object[] { i });
            if (innerBlobList == null) continue;

            var newInnerList = addNewMethod.Invoke(shaderList, null);
            if (newInnerList == null) continue;

            var addRangeMethod = newInnerList.GetType().GetMethod("AddRange");
            if (addRangeMethod != null)
            {
                addRangeMethod.Invoke(newInnerList, new[] { innerBlobList });
            }
            else
            {
                var innerCountProp = innerBlobList.GetType().GetProperty("Count");
                var innerIndexer = innerBlobList.GetType().GetProperty("Item");
                var addMethod = newInnerList.GetType().GetMethod("Add");
                if (innerCountProp != null && innerIndexer != null && addMethod != null)
                {
                    int innerCount = (int)innerCountProp.GetValue(innerBlobList)!;
                    for (int j = 0; j < innerCount; j++)
                    {
                        var val = innerIndexer.GetValue(innerBlobList, new object[] { j });
                        addMethod.Invoke(newInnerList, new[] { val });
                    }
                }
            }
        }
    }
    #endregion
}
