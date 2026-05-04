using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.BlocksInfoHook;

public class BlocksInfoHook : CommonHook, IHookModule
{
    public delegate BlocksInfo ReadBlocksInfoDelegate(EndianReader reader);

    // Static callback used by the hooked method
    public static ReadBlocksInfoDelegate CustomReadBlocksInfo;

    private readonly ReadBlocksInfoDelegate _moduleCallback;

    public BlocksInfoHook(ReadBlocksInfoDelegate callback)
    {
        _moduleCallback = callback;
    }

    public void OnApply()
    {
        CustomReadBlocksInfo = _moduleCallback;
    }

    [RetargetMethod(typeof(BlocksInfo), "Read")]
    public static BlocksInfo BlocksInfo_Read(EndianReader reader)
    {
        return CustomReadBlocksInfo(reader);
    }
}
