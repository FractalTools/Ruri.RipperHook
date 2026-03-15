using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.FileStreamBundleHeaderHook;

public class FileStreamBundleHeaderHook : CommonHook, IHookModule
{
    public delegate void ReadHeaderDelegate(FileStreamBundleHeader header, EndianReader reader);

    // Static callback used by the hooked method
    public static ReadHeaderDelegate CustomReadHeader;

    private readonly ReadHeaderDelegate _moduleCallback;

    public FileStreamBundleHeaderHook(ReadHeaderDelegate callback)
    {
        _moduleCallback = callback;
    }

    public void OnApply()
    {
        CustomReadHeader = _moduleCallback;
    }

    [RetargetMethod(typeof(FileStreamBundleHeader), nameof(Read), typeof(EndianReader))]
    public void Read(EndianReader reader)
    {
        var _this = (object)this as FileStreamBundleHeader;
        CustomReadHeader(_this, reader);
    }
}
