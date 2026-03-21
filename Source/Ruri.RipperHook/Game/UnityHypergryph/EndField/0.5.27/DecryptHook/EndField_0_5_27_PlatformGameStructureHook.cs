using AssetRipper.IO.Endian;
using Ruri.RipperHook.Crypto;
using System.Reflection;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_5_27_Hook
{
    public static bool CustomAssetBundlesCheck(string filePath)
    {
        if (filePath.Contains("VFS", StringComparison.OrdinalIgnoreCase))
        {
            if (filePath.EndsWith(".chk", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".blc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return filePath.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CustomAssetBundlesCheckMagicNum(EndianReader reader, MethodInfo FromSerializedFile)
    {
        if ((bool)FromSerializedFile.Invoke(null, new object[] { reader, "UnityFS" }) ||
            (bool)FromSerializedFile.Invoke(null, new object[] { reader, "UnityWeb" }) ||
            (bool)FromSerializedFile.Invoke(null, new object[] { reader, "UnityRaw" }))
        {
            return true;
        }

        if (VFSDecryptor.IsValidHeader(reader))
        {
            return true;
        }

        return false;
    }
}