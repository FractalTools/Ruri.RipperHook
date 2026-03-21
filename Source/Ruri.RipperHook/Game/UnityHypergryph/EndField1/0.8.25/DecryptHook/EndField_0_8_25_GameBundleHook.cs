using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Streams;
using AssetRipper.IO.Files.Streams.Smart;
using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Buffers.Binary;
using System.Numerics;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_8_25_Hook
{
    public static void CustomFilePreInitialize(GameBundle _this, IEnumerable<string> paths, List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider)
    {
        foreach (var path in paths)
        {
            using var stream = SmartStream.OpenReadMulti(path, fileSystem);
            // 读取整个文件到内存，因为需要切分，内存操作比流Seek更稳健
            var fileData = new byte[stream.Length];
            stream.Read(fileData, 0, fileData.Length);

            var span = fileData.AsSpan();
            long position = 0;
            bool isVFSContainer = false;

            // 1. 尝试检测第一个包是否为 VFS 格式
            long firstBundleSize = GetVFSBundleSize(span);
            if (firstBundleSize > 0 && firstBundleSize < fileData.Length)
            {
                isVFSContainer = true;
                //Logger.Info($"[EndField] Detected VFS Container: {Path.GetFileName(path)}");
            }

            if (isVFSContainer)
            {
                // VFS 容器模式：循环切分
                while (position < fileData.Length)
                {
                    var remaining = span.Slice((int)position);

                    // 至少需要 40 字节读取 Header
                    if (remaining.Length < 40)
                        break;

                    long bundleSize = GetVFSBundleSize(remaining);

                    if (bundleSize <= 0 || bundleSize > remaining.Length)
                    {
                        Logger.Warning($"[EndField] Invalid bundle size at offset {position}: {bundleSize}. Stopping parse.");
                        break;
                    }

                    // 切分数据
                    var bundleData = fileData.Skip((int)position).Take((int)bundleSize).ToArray();
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
                // 普通模式：整个文件作为一个 Bundle
                fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(
                    fileData,
                    MultiFileStream.GetFilePath(path),
                    MultiFileStream.GetFileName(path),
                    dependencyProvider));
            }
        }
    }

    /// <summary>
    /// 解析 VFS Header 并计算 Bundle 大小
    /// 返回 -1 表示不是有效的 VFS Header
    /// </summary>
    private static long GetVFSBundleSize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 40) return -1;

        // 1. 快速 Magic Check (IsValidHeader logic)
        // 注意：这里手动实现 BigEndian 读取
        uint a = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
        uint b = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        var c1 = ((a ^ 0x91A64750) >> 3) ^ ((a ^ 0x91A64750) << 29);
        var c2 = (c1 << 16) ^ 0xD5F9BECC;
        var c3 = (c1 ^ c2) & 0xFFFFFFFF;

        if (b != c3) return -1;

        // 2. 解析 Header 获取 Size
        // Offsets (0-based, BigEndian):
        // 26: size1 (4 bytes)
        // 32: flags2 (4 bytes)
        // 36: size2 (4 bytes)

        uint size1 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(26, 4));
        uint flags2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(32, 4));
        uint size2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(36, 4));

        ulong size = VFSDecryptor.BitConcat64(32, size1 ^ size2 ^ 0x342D983F, size2);
        size = (BitOperations.RotateLeft(size, 3)) ^ 0x5B4FA98A430D0E62UL;

        // 检查 encFlags 是否 >= 7 需要 padding (可选，如果 Size 包含 padding 则不需要额外处理)
        // 但根据 VFSFileHook 的逻辑，Header 读取后如果 flag >= 7 会跳过 8 字节，这不影响 Bundle 的总 Size。
        // Bundle 的 Size 字段通常指的是从 Header 开始的总字节数。

        return (long)size;
    }
}