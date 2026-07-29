using System.Text.RegularExpressions;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// 把原始 VFS 文件按名转储出来。
/// <para>AssetRipper 只认 bundle;VFS 里还住着非 bundle 的载荷(战斗服务器表就是其中一类),
/// 那些资产路径和 CABMap 都索引不到,只能从这一层直接取。</para>
/// </summary>
internal static class VfsDumper
{
    /// <summary>VFS 根搜索顺序 —— 热更覆盖层在前,基础客户端在后。</summary>
    private static string[] VfsRoots(string gameRoot) =>
    [
        Path.Combine(gameRoot, "Endfield_Data", "Persistent", "VFS"),
        Path.Combine(gameRoot, "Endfield_Data", "StreamingAssets", "VFS"),
    ];

    /// <summary>
    /// 枚举 → 按 <paramref name="namePatterns"/> 过滤 → 落盘。返回 (命中数, 类型直方图)。
    /// 没给 pattern 时只统计不落盘 —— 先看清里面有什么再决定取什么。
    /// </summary>
    internal static (int Matched, SortedDictionary<string, int> BlockTypes) Dump(
        string gameRoot, string outputDirectory, Regex[] filters, string[]? blockTypeFilter)
    {
        if (GameBundleHook.EnumerateVfsFiles is not { } enumerate)
        {
            throw new InvalidOperationException("当前 --hook 没有提供 VFS 访问(需要 EndField 这类 VFS 游戏)。");
        }
        string[] roots = VfsRoots(gameRoot);
        if (GameBundleHook.ExtractVfsFile is not { } extract)
        {
            throw new InvalidOperationException("当前 --hook 没有提供 VFS 提取。");
        }
        var blockTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int matched = 0;

        foreach ((string fileName, long _, string blockType, long _, string _) in enumerate(roots, blockTypeFilter))
        {
            string key = string.IsNullOrEmpty(blockType) ? "(none)" : blockType;
            blockTypes[key] = blockTypes.TryGetValue(key, out int count) ? count + 1 : 1;
            if (filters.Length == 0 || !filters.Any(f => f.IsMatch(fileName)))
            {
                continue;
            }
            matched++;
            byte[] payload = extract(roots, fileName);
            string target = Path.Combine(outputDirectory, SafeName(fileName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, payload);
        }
        return (matched, blockTypes);
    }

    /// <summary>VFS 名可能带路径分隔与非法字符,压成单层安全文件名。</summary>
    private static string SafeName(string fileName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[fileName.Length];
        for (int i = 0; i < fileName.Length; i++)
        {
            buffer[i] = Array.IndexOf(invalid, fileName[i]) >= 0 ? '_' : fileName[i];
        }
        return new string(buffer);
    }
}
