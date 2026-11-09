using System;
using System.IO;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_72;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;
using Ruri.RipperHook.Core.TypeTree;

namespace Ruri.RipperHook.HookUtils;

public sealed class SerializedComputeShaderHook : CommonHook, IHookModule
{
    public void OnApply()
    {
    }

    [RetargetMethodFunc(typeof(GameAssetFactory), nameof(GameAssetFactory.ReadAsset))]
    public static bool ReadAsset(ILContext il)
    {
        ILCursor cursor = new(il);
        ILLabel original = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Ldarg_2);
        cursor.Emit(OpCodes.Ldarg_3);
        cursor.EmitDelegate<Func<AssetInfo, ReadOnlyArraySegment<byte>, SerializedType?, IUnityObjectBase?>>(ReadSerialized);
        cursor.Emit(OpCodes.Dup);
        cursor.Emit(OpCodes.Brfalse, original);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(original);
        cursor.Emit(OpCodes.Pop);
        return true;
    }

    private static IUnityObjectBase? ReadSerialized(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData, SerializedType? assetType)
    {
        if (assetInfo.ClassID != (int)ClassIDType.ComputeShader
            || assetInfo.Collection.Platform != BuildTarget.NoTarget
            || assetInfo.Collection.Flags.IsRelease()
            || assetType is null
            || !TypeTreeNodeStruct.TryMakeFromTypeTree(assetType.OldType, out TypeTreeNodeStruct root))
        {
            return null;
        }

        IComputeShader asset = (IComputeShader)AssetFactory.CreateSerialized(assetInfo, assetInfo.Collection.Version)!;
        TypeTreeReadPlan plan = TypeTreeReadPlan.Create(ClassIDType.ComputeShader, asset.GetType(), TypeTreeNode.FromSerialized(root));
        EndianSpanReader reader = new(assetData, assetInfo.Collection.EndianType);
        plan.Read(asset, ref reader);
        if (reader.Position != reader.Length)
        {
            throw new InvalidDataException($"ComputeShader {assetInfo.PathID} consumed {reader.Position} of {reader.Length} serialized bytes.");
        }
        return asset;
    }
}
