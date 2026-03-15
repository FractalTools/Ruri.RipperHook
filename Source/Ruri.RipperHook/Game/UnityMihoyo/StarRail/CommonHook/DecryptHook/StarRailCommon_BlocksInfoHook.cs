using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;

namespace Ruri.RipperHook.StarRail;

public partial class StarRailCommon_Hook
{
    public static BlocksInfo CustomBlocksInfoRead(EndianReader reader)
    {
        return new BlocksInfo(new Hash128(), reader.ReadEndianArray<StorageBlock>());
    }
}