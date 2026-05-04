using System.Runtime.CompilerServices;
using AssetRipper.IO.Files.BundleFiles.FileStream;

namespace Ruri.RipperHook.HookUtils;

public static class FileStreamBundleWrapper
{
    // 劫持 BlocksInfo 的 setter
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BlocksInfo")]
    extern static void SetBlocksInfoInternal(FileStreamBundleFile file, BlocksInfo value);

    // --- 扩展方法 ---

    /// <summary>
    /// 强制设置新的 BlocksInfo 对象
    /// </summary>
    public static FileStreamBundleFile SetBlocksInfo(this FileStreamBundleFile file, BlocksInfo newBlocksInfo)
    {
        SetBlocksInfoInternal(file, newBlocksInfo);
        return file;
    }
}