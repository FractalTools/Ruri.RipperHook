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

// 类名你可以根据需要保留为 EndField_0_5_27_Hook 或改成你想要的名字
public partial class EndField_0_5_27_Hook
{
    public static void CustomFilePreInitialize(GameBundle _this, IEnumerable<string> paths, List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider)
    {
        foreach (var path in paths)
        {
            using var stream = SmartStream.OpenReadMulti(path, fileSystem);
            // 读取整个文件到内存
            var fileData = new byte[stream.Length];
            stream.Read(fileData, 0, fileData.Length);

            var span = fileData.AsSpan();
            long position = 0;
            int index = 0;

            // 1. 检测是否为 VFS 容器（判断头部）
            long firstBundleSize = GetVFSBundleSize(span);
            bool isVFSContainer = firstBundleSize > 0 && firstBundleSize < fileData.Length;

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
                        Logger.Warning($"[EndField] End of recognizable VFS bundles or invalid size at offset {position:X}. Stopping parse.");
                        break;
                    }

                    // 【核心优化】弃用 Skip().Take()，直接使用 Span 切片生成数组，极大降低内存占用和 GC 压力
                    var bundleData = span.Slice((int)position, (int)bundleSize).ToArray();

                    // 采用旧版更直观的子包命名方式
                    var subName = $"{Path.GetFileName(path)}_sub{index++}";

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
                // 兜底模式：如果文件头连 VFS 都不是（或者不是拼接包），整个文件作为一个 Bundle 直接交由底层处理
                fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(
                    fileData,
                    MultiFileStream.GetFilePath(path),
                    MultiFileStream.GetFileName(path),
                    dependencyProvider));
            }
        }
    }

    /// <summary>
    /// 解析 VFS Obfuscated Header 并计算 Bundle 大小
    /// 返回 -1 表示不是有效的 VFS Header
    /// </summary>
    private static long GetVFSBundleSize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 40) return -1;

        // 快速 Magic Check
        uint a = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
        uint b = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        var c1 = ((a ^ 0x91A64750) >> 3) ^ ((a ^ 0x91A64750) << 29);
        var c2 = (c1 << 16) ^ 0xD5F9BECC;
        var c3 = (c1 ^ c2) & 0xFFFFFFFF;

        if (b != c3) return -1;

        // 解析 Header 获取 Size
        uint size1 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(26, 4));
        uint flags2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(32, 4));
        uint size2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(36, 4));

        ulong size = VFSDecryptor.BitConcat64(32, size1 ^ size2 ^ 0x342D983F, size2);
        size = (BitOperations.RotateLeft(size, 3)) ^ 0x5B4FA98A430D0E62UL;

        return (long)size;
    }
}