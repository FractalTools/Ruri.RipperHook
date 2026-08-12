using System.Runtime.CompilerServices;
using AssetRipper.IO.Files.BundleFiles.FileStream;

namespace Ruri.RipperHook.HookUtils;

public static class FileStreamBundleWrapper
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BlocksInfo")]
    extern static void SetBlocksInfoInternal(FileStreamBundleFile file, BlocksInfo value);


    public static FileStreamBundleFile SetBlocksInfo(this FileStreamBundleFile file, BlocksInfo newBlocksInfo)
    {
        SetBlocksInfoInternal(file, newBlocksInfo);
        return file;
    }
}