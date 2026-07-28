using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Classes.ClassID_114;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    /// <summary>
    /// 1:1 复刻 <c>GameAssetFactory.ReadMonoBehaviour</c>,只去掉「一见
    /// <c>ManagedReferencesRegistry</c> 就把 Structure 置 null」那段提前放弃。
    /// 改为照常 <c>TryRead</c>,真读失败才置 null —— 读得动多少算多少,
    /// 而不是因为末尾多一个注册表字段就把前面所有普通字段一起丢掉。
    /// </summary>
    [RetargetMethod(typeof(GameAssetFactory), "ReadMonoBehaviour")]
    public static IMonoBehaviour ReadMonoBehaviour(
        IMonoBehaviour monoBehaviour,
        ReadOnlyArraySegment<byte> assetData,
        IAssemblyManager assemblyManager,
        SerializedType? type)
    {
        EndianSpanReader reader = new EndianSpanReader(assetData, monoBehaviour.Collection.EndianType);
        try
        {
            monoBehaviour.Read(ref reader);
            if (type is not null && TypeTreeNodeStruct.TryMakeFromTypeTree(type.OldType, out TypeTreeNodeStruct rootNode))
            {
                SerializableStructure structure = SerializableTreeType.FromRootNode(rootNode, true).CreateSerializableStructure();
                monoBehaviour.Structure = structure.TryRead(ref reader, monoBehaviour) ? structure : null;
            }
            else
            {
                monoBehaviour.Structure = new UnloadedStructure(monoBehaviour, assemblyManager, assetData.Slice(reader.Position));
            }
        }
        catch (Exception exception)
        {
            Logger.Error(LogCategory.Import, $"Unable to read MonoBehaviour: {exception}");
        }
        return monoBehaviour;
    }
}
