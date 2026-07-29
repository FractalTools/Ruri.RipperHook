using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    /// <summary>
    /// 登记该序列化文件自带的 <c>[SerializeReference]</c> 载荷类型树 —— 它才是引用对象布局的正源,
    /// 程序集反射只是兜底。
    /// <para>上游 PR 是给 <c>SerializedAssetCollection</c> 加一个 <c>RefTypes</c> 属性;AOP 加不了属性,
    /// 故按**文件名**显式索引(集合与文件同名),读取时按 <c>Collection.Name</c> 取回。
    /// 不用"记住上一个解析的文件"那种顺序配对 —— 那是隐式耦合,漏一次就静默出错。</para>
    /// </summary>
    [RetargetMethod(typeof(SerializedAssetCollection), "FromSerializedFile", isBefore: true, isReturn: false)]
    public static void FromSerializedFile(Bundle bundle, SerializedFile file, AssetFactoryBase factory, UnityVersion defaultVersion)
    {
        SerializeReferenceContext.RegisterRefTypes(file.NameFixed, file.RefTypes.ToArray());
    }
}
