using System.Runtime.CompilerServices;
using AssetRipper.IO.Files.BundleFiles.FileStream;

namespace Ruri.RipperHook.HookUtils;

public static class StorageBlockWrapper
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UncompressedSize")]
    extern static void SetUncompressedSizeInternal(StorageBlock block, uint value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_CompressedSize")]
    extern static void SetCompressedSizeInternal(StorageBlock block, uint value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Flags")]
    extern static void SetFlagsInternal(StorageBlock block, StorageBlockFlags value);


    public static void SetUncompressedSize(this StorageBlock block, uint value)
        => SetUncompressedSizeInternal(block, value);

    public static void SetCompressedSize(this StorageBlock block, uint value)
        => SetCompressedSizeInternal(block, value);

    public static void SetFlags(this StorageBlock block, StorageBlockFlags value)
        => SetFlagsInternal(block, value);
}
