using System.Runtime.CompilerServices;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;

namespace Ruri.RipperHook.HookUtils;

public static class SerializedFileWrapper
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_objects")]
    extern static ref ObjectInfo[]? ObjectsInternal(SerializedFile file);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_types")]
    extern static ref SerializedType[]? TypesInternal(SerializedFile file);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_HasTypeTree")]
    extern static void SetHasTypeTreeInternal(SerializedFile file, bool value);

    public static ObjectInfo[] Objects(this SerializedFile file) => ObjectsInternal(file) ?? [];

    public static SerializedType[] Types(this SerializedFile file) => TypesInternal(file) ?? [];

    public static void SetHasTypeTree(this SerializedFile file, bool value) =>
        SetHasTypeTreeInternal(file, value);
}
