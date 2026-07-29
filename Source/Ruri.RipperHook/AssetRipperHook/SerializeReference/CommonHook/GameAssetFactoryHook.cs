using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using System.Reflection;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    /// <summary>
    /// 整体替换 <c>ReadAsset</c>:三个分支逐条照抄,只把 MonoBehaviour 那支换成支持
    /// <c>[SerializeReference]</c> 的读法(去掉原版的提前放弃)。
    /// <para>不切它调的 <c>ReadMonoBehaviour</c>:那是 private static 且只有一处调用,
    /// Release 下被内联进这里,hook 上去一次都不会被调到(实测命中 0 次)。</para>
    /// <para>也不用条件前缀:那条只对 void 源方法成立,本方法有返回值。</para>
    /// </summary>
    [RetargetMethod(typeof(GameAssetFactory), nameof(GameAssetFactory.ReadAsset))]
    public static IUnityObjectBase? ReadAsset(
        GameAssetFactory self,
        AssetInfo assetInfo,
        ReadOnlyArraySegment<byte> assetData,
        SerializedType? assetType)
    {
        if (assetInfo.Collection.Version.LessThan(3, 5))
        {
            // 版本被剥/低于 3.5 的资产读不了(源同)。
            return new UnreadableObject(assetInfo, assetData.ToArray());
        }
        if (assetInfo.ClassID != (int)ClassIDType.MonoBehaviour)
        {
            return ReadNormalObject(self, assetInfo, assetData);
        }

        IMonoBehaviour monoBehaviour = MonoBehaviour.Create(assetInfo);
        IAssemblyManager? assemblyManager = GetAssemblyManager(self);
        EndianSpanReader reader = new(assetData, monoBehaviour.Collection.EndianType);
        try
        {
            monoBehaviour.Read(ref reader);
            if (assetType is not null && TypeTreeNodeStruct.TryMakeFromTypeTree(assetType.OldType, out TypeTreeNodeStruct rootNode))
            {
                SerializableStructure structure = SerializableTreeType.FromRootNode(rootNode, true).CreateSerializableStructure();
                using (SerializeReferenceContext.Enter(
                    SerializeReferenceContext.GetRefTypes(monoBehaviour.Collection), assemblyManager))
                {
                    monoBehaviour.Structure = TryReadStructure(structure, ref reader, monoBehaviour) ? structure : null;
                }
            }
            else if (assemblyManager is not null)
            {
                monoBehaviour.Structure = new UnloadedStructure(monoBehaviour, assemblyManager, assetData.Slice(reader.Position));
            }
        }
        catch (Exception exception)
        {
            Logger.Error(LogCategory.Import, $"Unable to read MonoBehaviour: {exception.GetType().Name}: {exception.Message}");
        }
        return monoBehaviour;
    }

    private static MethodInfo? _readNormalObjectMethod;

    /// <summary><c>ReadNormalObject</c> 是 private static;取一次缓存后反射调用,行为与源一致。</summary>
    private static IUnityObjectBase? ReadNormalObject(
        GameAssetFactory self, AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData)
    {
        _readNormalObjectMethod ??= typeof(GameAssetFactory)
            .GetMethod("ReadNormalObject", BindingFlags.Static | BindingFlags.NonPublic);
        return _readNormalObjectMethod?.Invoke(null, new object[] { assetInfo, assetData }) as IUnityObjectBase;
    }

    /// <summary>读结构;尾部多余字节保留已读字段并告警(URP/包升级后的常态,不算失败)。</summary>
    private static bool TryReadStructure(
        SerializableStructure structure,
        ref EndianSpanReader reader,
        IMonoBehaviour monoBehaviour)
    {
        try
        {
            structure.Read(ref reader, monoBehaviour.Collection.Version, monoBehaviour.Collection.Flags);
        }
        catch (Exception exception)
        {
            Logger.Error(LogCategory.Import,
                $"Unable to read MonoBehaviour Structure ({exception.GetType().Name}: {exception.Message}).");
            return false;
        }
        if (reader.Position < reader.Length)
        {
            Logger.Warning(LogCategory.Import,
                $"MonoBehaviour has {reader.Length - reader.Position} unread trailing bytes; keeping partial data.");
            return true;
        }
        if (reader.Position > reader.Length)
        {
            Logger.Error(LogCategory.Import,
                $"MonoBehaviour layout overran binary content (read {reader.Position}, total {reader.Length}).");
            return false;
        }
        return true;
    }

    private static PropertyInfo? _assemblyManagerProperty;

    /// <summary><c>GameAssetFactory.AssemblyManager</c> 是私有属性;取一次缓存起来。</summary>
    private static IAssemblyManager? GetAssemblyManager(GameAssetFactory factory)
    {
        _assemblyManagerProperty ??= typeof(GameAssetFactory)
            .GetProperty("AssemblyManager", BindingFlags.Instance | BindingFlags.NonPublic);
        return _assemblyManagerProperty?.GetValue(factory) as IAssemblyManager;
    }
}
