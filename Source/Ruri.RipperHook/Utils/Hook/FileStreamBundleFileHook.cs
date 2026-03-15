using AssetRipper.IO.Files.BundleFiles.FileStream;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.FileStreamBundleFileHook;

public class FileStreamBundleFileHook : CommonHook, IHookModule
{
    public delegate void ReadFileStreamMetadataDelegate(FileStreamBundleFile file, Stream stream, long basePosition);

    // Static callback used by the hooked method
    public static ReadFileStreamMetadataDelegate CustomReadFileStreamMetadata;

    private readonly ReadFileStreamMetadataDelegate _moduleCallback;

    public FileStreamBundleFileHook(ReadFileStreamMetadataDelegate callback)
    {
        _moduleCallback = callback;
    }

    public void OnApply()
    {
        CustomReadFileStreamMetadata = _moduleCallback;
    }

    [RetargetMethod(typeof(FileStreamBundleFile), "ReadFileStreamMetadata")]
    public void ReadFileStreamMetadata(Stream stream, long basePosition)
    {
        var _this = (object)this as FileStreamBundleFile;
        CustomReadFileStreamMetadata(_this, stream, basePosition);
    }
}
