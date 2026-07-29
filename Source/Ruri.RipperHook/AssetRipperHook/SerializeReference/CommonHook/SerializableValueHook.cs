using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    private const string RegistryTypeName = "ManagedReferencesRegistry";
    private const string RegistryFieldName = "references";

    /// <summary>
    /// 条件前缀:只拦 <c>[SerializeReference]</c> 注册表字段,其余返回 false 原样交回原实现。
    /// <para>切在字段这一层而非 <c>SerializableStructure.Read</c>,是因为后者要用
    /// <c>Version</c> / <c>GetMaxDepthLevel</c> 两个私有成员,外部复刻不了 <c>IsAvailable</c>;
    /// 字段层既拿得到全部上下文,又完全不碰私有面,也不必复刻那 400 行 switch。</para>
    /// </summary>
    [RetargetMethod(typeof(SerializableValue), nameof(SerializableValue.Read))]
    public static bool Read(
        ref SerializableValue self,
        ref EndianSpanReader reader,
        UnityVersion version,
        TransferInstructionFlags flags,
        int depth,
        in SerializableType.Field etalon)
    {
        if (etalon.Type.Name is not RegistryTypeName || etalon.Name is not RegistryFieldName)
        {
            return false;
        }
        // 嵌套结构的类型树里也可能带 registry,但只有根资产写一份。
        if (SerializeReferenceContext.ReadingReferencedObject)
        {
            return true;
        }
        self = new SerializableValue(0, ManagedReferencesRegistry.Read(
            ref reader, version, flags, depth,
            SerializeReferenceContext.RefTypes,
            SerializeReferenceContext.AssemblyManager));
        if (etalon.Align)
        {
            reader.Align();
        }
        return true;
    }
}
