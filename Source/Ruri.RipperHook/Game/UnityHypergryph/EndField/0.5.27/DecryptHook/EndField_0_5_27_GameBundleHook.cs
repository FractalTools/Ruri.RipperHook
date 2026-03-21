using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.Streams;
using AssetRipper.IO.Files.Streams.Smart;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Buffers.Binary;
using System.Numerics;
using Ruri.RipperHook.Crypto;

namespace Ruri.RipperHook.Endfield;

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
            bool anyLoaded = false;

            // 循环读取拼接的 Bundle (.chk / .blc / .bundle)
            while (position < fileData.Length)
            {
                var remaining = span.Slice((int)position);

                // 至少需要足够字节读取 Header 信息
                if (remaining.Length < 20)
                    break;

                long bundleSize = -1;

                // 1. 尝试检测标准 UnityFS Header (Endfield 0.5 .chk 通常是这种情况)
                if (IsUnityFSHeader(remaining))
                {
                    bundleSize = GetUnityFSBundleSize(remaining);
                }
                // 2. 尝试检测 VFS Header (Endfield 0.8+ 逻辑，保留以兼容混用情况)
                else
                {
                    bundleSize = GetVFSBundleSize(remaining);
                }

                // 如果无法识别 Header 或计算出的大小异常，则停止
                if (bundleSize <= 0 || bundleSize > remaining.Length)
                {
                    // 如果已经解析过至少一个包，说明是尾部填充数据或未知数据，停止解析
                    if (anyLoaded)
                    {
                        Logger.Verbose($"[EndField] End of recognizable bundles at offset {position:X}.");
                    }
                    else
                    {
                        fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(
                            fileData,
                            MultiFileStream.GetFilePath(path),
                            MultiFileStream.GetFileName(path),
                            dependencyProvider));
                    }
                    break;
                }

                // 切分数据
                var bundleData = fileData.Skip((int)position).Take((int)bundleSize).ToArray();

                // 为子包生成唯一名称 (例如: file.chk_sub0, file.chk_sub1)
                // 如果是第一个包且文件本身就是 .bundle，通常保留原名，但对于 .chk 建议区分
                var subName = $"{Path.GetFileName(path)}_sub{index++}";

                fileStack.AddRange(GameBundleHook.LoadFilesAndDependencies(
                    bundleData,
                    MultiFileStream.GetFilePath(path),
                    subName,
                    dependencyProvider));

                anyLoaded = true;
                position += bundleSize;
            }
        }
    }

    /// <summary>
    /// 检测是否为标准 UnityFS 头
    /// </summary>
    private static bool IsUnityFSHeader(ReadOnlySpan<byte> buffer)
    {
        // "UnityFS\0"
        return buffer.Length >= 8 &&
               buffer[0] == 0x55 && buffer[1] == 0x6E && buffer[2] == 0x69 &&
               buffer[3] == 0x74 && buffer[4] == 0x79 && buffer[5] == 0x46 &&
               buffer[6] == 0x53 && buffer[7] == 0x00;
    }

    /// <summary>
    /// 从标准 UnityFS Header 中读取大小
    /// </summary>
    private static long GetUnityFSBundleSize(ReadOnlySpan<byte> buffer)
    {
        // UnityFS Header format:
        // Signature (String) + Version (UInt32) + UnityVersion (String) + UnityRevision (String) + Size (Int64)

        int ptr = 8; // Skip "UnityFS\0"

        if (ptr + 4 > buffer.Length) return -1;
        ptr += 4; // Skip Version (UInt32)

        // Skip UnityVersion string (null terminated)
        while (ptr < buffer.Length && buffer[ptr] != 0) ptr++;
        ptr++; // skip null

        // Skip UnityRevision string (null terminated)
        while (ptr < buffer.Length && buffer[ptr] != 0) ptr++;
        ptr++; // skip null

        if (ptr + 8 > buffer.Length) return -1;

        // Size is Int64 BigEndian
        long size = BinaryPrimitives.ReadInt64BigEndian(buffer.Slice(ptr));
        return size;
    }

    /// <summary>
    /// 解析 VFS Obfuscated Header 并计算 Bundle 大小 (针对 0.8+ 加密格式)
    /// </summary>
    private static long GetVFSBundleSize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 40) return -1;

        uint a = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
        uint b = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        var c1 = ((a ^ 0x91A64750) >> 3) ^ ((a ^ 0x91A64750) << 29);
        var c2 = (c1 << 16) ^ 0xD5F9BECC;
        var c3 = (c1 ^ c2) & 0xFFFFFFFF;

        if (b != c3) return -1;

        uint size1 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(26, 4));
        uint flags2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(32, 4));
        uint size2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(36, 4));

        ulong size = VFSDecryptor.BitConcat64(32, size1 ^ size2 ^ 0x342D983F, size2);
        size = (BitOperations.RotateLeft(size, 3)) ^ 0x5B4FA98A430D0E62UL;

        return (long)size;
    }
}