using System.Runtime.CompilerServices;
using AssetRipper.IO.Files.BundleFiles.FileStream;

namespace Ruri.RipperHook.HookUtils;

public static class StorageBlockWrapper
{
    // 劫持 UncompressedSize 的 setter
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UncompressedSize")]
    extern static void SetUncompressedSizeInternal(StorageBlock block, uint value);

    // 劫持 CompressedSize 的 setter
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_CompressedSize")]
    extern static void SetCompressedSizeInternal(StorageBlock block, uint value);

    // 劫持 Flags 的 setter
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Flags")]
    extern static void SetFlagsInternal(StorageBlock block, StorageBlockFlags value);

    // --- 方便调用的扩展方法 ---

    public static void SetUncompressedSize(this StorageBlock block, uint value)
        => SetUncompressedSizeInternal(block, value);

    public static void SetCompressedSize(this StorageBlock block, uint value)
        => SetCompressedSizeInternal(block, value);

    public static void SetFlags(this StorageBlock block, StorageBlockFlags value)
        => SetFlagsInternal(block, value);
}
