using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    // Cached reflection info for Ruri Shader_2021_3_1014
    private static MethodInfo? _shaderCreateMethod;
    private static Type? _shaderConcreteType;
    private static bool _shaderReflectionReady;
    private static int _shaderReadCount;

    // Cached AR factory reflection (resolved once in SetupShaderReflection)
    private static MethodInfo? _arShaderCreateMethod;

    public override void Initialize()
    {
        base.Initialize();
        SetupShaderReflection();
    }

    private void SetupShaderReflection()
    {
        try
        {
            var ruriAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Ruri.SourceGenerated");
            if (ruriAssembly == null)
            {
                HookLogger.LogFailure("[EndField 1.1.9] Cannot find Ruri.SourceGenerated assembly");
                return;
            }

            var ruriFactoryType = ruriAssembly.GetType("Ruri.SourceGenerated.Classes.ClassID_48.Shader");
            if (ruriFactoryType == null)
            {
                HookLogger.LogFailure("[EndField 1.1.9] Cannot find Ruri Shader factory type");
                return;
            }

            _shaderCreateMethod = ruriFactoryType.GetMethod("Create",
                BindingFlags.Public | BindingFlags.Static, null,
                new Type[] { typeof(AssetInfo), typeof(UnityVersion) }, null);

            if (_shaderCreateMethod == null)
            {
                HookLogger.LogFailure("[EndField 1.1.9] Cannot find Ruri Shader.Create method");
                return;
            }

            var probeVersion = new UnityVersion(2021, 3, 1014, UnityVersionType.Experimental, (byte)CustomEngineType.EndField);
            var probeInstance = _shaderCreateMethod.Invoke(null, new object[] { null!, probeVersion });
            _shaderConcreteType = probeInstance?.GetType();

            if (_shaderConcreteType == null)
            {
                HookLogger.LogFailure("[EndField 1.1.9] Failed to resolve Ruri Shader concrete type");
                return;
            }

            // Cache AR Shader factory method
            var arFactoryType = typeof(ClassIDType).Assembly
                .GetType("AssetRipper.SourceGenerated.Classes.ClassID_48.Shader");
            _arShaderCreateMethod = arFactoryType?.GetMethod("Create", new[] { typeof(AssetInfo), typeof(UnityVersion) });

            _shaderReflectionReady = true;
            HookLogger.LogSuccess($"[EndField 1.1.9] Shader reflection ready → {_shaderConcreteType.Name} (SubShaderBlobs={_shaderConcreteType.GetProperty("SubShaderBlobs") != null})");
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[EndField 1.1.9] Shader reflection setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// IL hook on GameAssetFactory.ReadAsset to intercept Shader (ClassID 48) reading.
    /// Injects a check at method start: if classID==48, use our custom reader and return.
    /// </summary>
    [RetargetMethodFunc(typeof(GameAssetFactory), "ReadAsset")]
    public static bool HookReadAsset(ILContext il)
    {
        var cursor = new ILCursor(il);

        // Insert at method beginning:
        //   var result = TryCustomShaderRead(assetInfo, assetData);
        //   if (result != null) return result;
        cursor.Emit(OpCodes.Ldarg_1); // assetInfo
        cursor.Emit(OpCodes.Ldarg_2); // assetData (ReadOnlyArraySegment<byte>)
        cursor.EmitDelegate<Func<AssetInfo, ReadOnlyArraySegment<byte>, IUnityObjectBase?>>(TryCustomShaderRead);

        var skipLabel = cursor.DefineLabel();
        cursor.Emit(OpCodes.Dup);
        cursor.Emit(OpCodes.Brfalse_S, skipLabel);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(skipLabel);
        cursor.Emit(OpCodes.Pop);

        HookLogger.LogSuccess("[EndField 1.1.9] Hooked GameAssetFactory.ReadAsset for Shader interception");
        return true;
    }

    /// <summary>
    /// Called for every asset read. Returns non-null only for ClassID 48 (Shader).
    /// Creates a Ruri Shader_2021_3_1014, reads the v1.1.9 data, consolidates blobs,
    /// and deep-copies into an AR Shader_2021_3_12 object.
    /// </summary>
    private static IUnityObjectBase? TryCustomShaderRead(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData)
    {
        if (assetInfo.ClassID != 48) return null;

        if (!_shaderReflectionReady || _shaderCreateMethod == null) return null;

        try
        {
            // Create AR Shader object (the target that AR knows how to export)
            if (_arShaderCreateMethod == null) return null;

            var arShader = (IUnityObjectBase)_arShaderCreateMethod.Invoke(null,
                new object[] { assetInfo, assetInfo.Collection.Version })!;

            // Create Ruri dummy with v1.1.9 TypeTree layout (Shader_2021_3_1014)
            var probeVersion = new UnityVersion(2021, 3, 1014, UnityVersionType.Experimental, (byte)CustomEngineType.EndField);
            var ruriShader = (IUnityObjectBase)_shaderCreateMethod.Invoke(null,
                new object[] { assetInfo, probeVersion })!;

            // Read binary data using Ruri's generated ReadRelease
            var reader = new EndianSpanReader(assetData, arShader.Collection.EndianType);
            ruriShader.ReadRelease(ref reader);

            _shaderReadCount++;
            if (_shaderReadCount <= 3)
            {
                var nameProp = ruriShader.GetType().GetProperty("Name_Utf8String",
                    BindingFlags.Public | BindingFlags.Instance);
                Console.WriteLine($"[EndField 1.1.9] Shader #{_shaderReadCount}: {nameProp?.GetValue(ruriShader)}, reader={reader.Position}/{reader.Length}");
            }

            // Phase A: Consolidate SubShaderBlobs[0] → root CompressedBlob
            ConsolidateSubShaderBlobs(ruriShader);
            // Note: CompressionType=3 (LZ4HC) is left as-is.
            // ReadBlobs() uses LZ4Codec.Decode unconditionally, which handles LZ4HC natively.

            // Deep copy processed Ruri shader → AR shader
            ReflectionExtensions.ClassDeepCopy(ruriShader, arShader);

            return arShader;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EndField 1.1.9] Shader read error (PathID={assetInfo.PathID}): {ex.Message}");
            return null; // Fall through to original AR reading
        }
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
