using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Streams;
using AssetRipper.IO.Files.Streams.Smart;
using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.Endfield.VFS;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Buffers.Binary;
using System.Numerics;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_1_1_9_Hook
{
    // Cache parsed .blc metadata per directory to avoid re-parsing
    private static readonly Dictionary<string, VFBlockMainInfo> _blcCache = new();

    public static void CustomFilePreInitialize(GameBundle _this, IEnumerable<string> paths, List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider)
    {
        foreach (var path in paths)
        {
            // Skip .blc metadata files — they are not bundles
            if (path.EndsWith(".blc", StringComparison.OrdinalIgnoreCase))
                continue;

            // Try VFS .blc-guided extraction for .chk files
            if (path.EndsWith(".chk", StringComparison.OrdinalIgnoreCase) &&
                TryLoadViaBlcMetadata(path, fileStack, dependencyProvider))
            {
                continue;
            }

            // Fallback: existing logic for non-VFS files or .chk without .blc
            using var stream = SmartStream.OpenReadMulti(path, fileSystem);
            var fileData = new byte[stream.Length];
            stream.Read(fileData, 0, fileData.Length);

            var span = fileData.AsSpan();
            long position = 0;
            bool isVFSContainer = false;

            long firstBundleSize = GetVFSBundleSize(span);
            if (firstBundleSize > 0 && firstBundleSize < fileData.Length)
            {
                isVFSContainer = true;
            }

            if (isVFSContainer)
            {
                while (position < fileData.Length)
                {
                    var remaining = span.Slice((int)position);

                    if (remaining.Length < 40)
                        break;

                    long bundleSize = GetVFSBundleSize(remaining);

                    if (bundleSize <= 0 || bundleSize > remaining.Length)
                    {
                        Logger.Warning($"[EndField 1.1.9] Invalid bundle size at offset {position}: {bundleSize}. Stopping parse.");
                        break;
                    }

                    var bundleData = new byte[bundleSize];
                    Array.Copy(fileData, (int)position, bundleData, 0, (int)bundleSize);
                    var subName = $"{Path.GetFileName(path)}_0x{position:X}";

                    fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(
                        bundleData,
                        MultiFileStream.GetFilePath(path),
                        subName,
                        dependencyProvider));

                    position += bundleSize;
                }
            }
            else
            {
                fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(
                    fileData,
                    MultiFileStream.GetFilePath(path),
                    MultiFileStream.GetFileName(path),
                    dependencyProvider));
            }
        }
    }

    /// <summary>
    /// Try to extract files from .chk using .blc metadata.
    /// Returns true if .blc was found and extraction succeeded.
    /// </summary>
    private static bool TryLoadViaBlcMetadata(string chkPath, List<FileBase> fileStack, IDependencyProvider? dependencyProvider)
    {
        var directory = Path.GetDirectoryName(chkPath);
        if (directory == null) return false;

        var dirName = Path.GetFileName(directory);
        var blcPath = Path.Combine(directory, dirName + ".blc");
        if (!File.Exists(blcPath)) return false;

        // Parse .blc (cached per directory)
        if (!_blcCache.TryGetValue(directory, out var blockInfo))
        {
            try
            {
                blockInfo = VFSBlockReader.ReadBlockMetadata(blcPath);
                _blcCache[directory] = blockInfo;
                Logger.Info($"[VFS] Parsed .blc: {dirName}.blc — {blockInfo.allChunks.Length} chunks, type={blockInfo.blockType}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[VFS] Failed to parse .blc '{blcPath}': {ex.Message}");
                return false;
            }
        }

        // Extract files from .chk
        int loaded = 0;
        foreach (var (name, data) in VFSBlockReader.ExtractChunkFiles(chkPath, blockInfo))
        {
            try
            {
                fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(
                    data,
                    MultiFileStream.GetFilePath(chkPath),
                    name,
                    dependencyProvider));
                loaded++;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[VFS] Failed to load bundle '{name}': {ex.Message}");
            }
        }

        Logger.Info($"[VFS] Loaded {loaded} bundles from {Path.GetFileName(chkPath)}");
        return loaded > 0;
    }

    private static long GetVFSBundleSize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 40) return -1;

        uint a = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
        uint b = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        // Release magic check
        var c1 = 4 * (a ^ 0x4A92F0CD) & 0xFFFF0000;
        var c2 = BitOperations.RotateRight(a ^ 0x4A92F0CD, 14);
        var c3 = c1 ^ c2 ^ 0xD8B1E637;

        if (b != c3) return -1;

        // Release header field order: after magic (8 bytes):
        // compBlocksInfo2(u16)=8, flags2(u32)=10, encFlags(u32)=14, size2(u32)=18,
        // flags1(u32)=22, uncompBlocksInfo1(u16)=26, unknown(u32)=28, uncompBlocksInfo2(u16)=32,
        // size1(u32)=34, compBlocksInfo1(u16)=38, unknownByte(u8)=40
        uint size2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(18, 4));
        uint size1 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(34, 4));

        ulong size = VFSDecryptor.BitConcat64(32, size1 ^ size2 ^ 0xDAD76848, size2);
        size = BitOperations.RotateRight(size, 18) ^ 0xA4F1A11747816520UL;

        return (long)(uint)size;
    }
}
