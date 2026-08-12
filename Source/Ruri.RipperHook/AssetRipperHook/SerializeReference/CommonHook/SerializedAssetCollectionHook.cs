using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.AR;

public partial class AR_SerializeReference_Hook
{
    [RetargetMethod(typeof(SerializedAssetCollection), "FromSerializedFile", isBefore: true, isReturn: false)]
    public static void FromSerializedFile(Bundle bundle, SerializedFile file, AssetFactoryBase factory, UnityVersion defaultVersion)
    {
        SerializeReferenceContext.RegisterRefTypes(file.NameFixed, file.RefTypes.ToArray());
    }
}
