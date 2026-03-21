using AssetRipper.IO.Endian;
using Ruri.RipperHook.Crypto;
using System.Reflection;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    public static bool CustomAssetBundlesCheckMagicNum(EndianReader reader, MethodInfo FromSerializedFile)
    {
        if ((bool)FromSerializedFile.Invoke(null, new object[] { reader, "UnityFS" }) ||
            (bool)FromSerializedFile.Invoke(null, new object[] { reader, "UnityWeb" }) ||
            (bool)FromSerializedFile.Invoke(null, new object[] { reader, "UnityRaw" }))
        {
            return true;
        }

        if (Endfield_1_1_9_VFSDecryptor.IsValidHeader(reader))
        {
            return true;
        }

        return false;
    }
}
