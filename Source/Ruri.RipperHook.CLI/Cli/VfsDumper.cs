using System.Text.RegularExpressions;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.CLI;

internal static class VfsDumper
{
    internal static (int Matched, SortedDictionary<string, int> BlockTypes) Dump(
        string outputDirectory, Regex[] filters, string[]? blockTypeFilter)
    {
        if (GameBundleHook.EnumerateVfsFiles is not { } enumerate)
        {
            throw new InvalidOperationException("当前 --hook 没有提供 VFS 访问(需要 Endfield 这类 VFS 游戏)。");
        }
        string[] roots = Session.RootsOrThrow("--dump-vfs");
        if (GameBundleHook.ExtractVfsFile is not { } extract)
        {
            throw new InvalidOperationException("当前 --hook 没有提供 VFS 提取。");
        }
        var blockTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int matched = 0;
        Directory.CreateDirectory(outputDirectory);
        // 索引恒写:枚举了几十万条却只回一个计数,调用方无从知道能筛什么。
        using StreamWriter index = new(Path.Combine(outputDirectory, "_vfs_index.tsv"));
        index.WriteLine("fileName\tblockType\tlength\tchkPath");

        foreach ((string fileName, long _, string blockType, long length, string chkPath) in enumerate(roots, blockTypeFilter))
        {
            string key = string.IsNullOrEmpty(blockType) ? "(none)" : blockType;
            blockTypes[key] = blockTypes.TryGetValue(key, out int count) ? count + 1 : 1;
            index.WriteLine($"{fileName}\t{key}\t{length}\t{chkPath}");
            // 名字与所属 .chk 都参与匹配:VFS 条目名是逐 bundle 的哈希,而"这个资产在哪个 chunk 里"
            // 是 CABMap 唯一给得出的坐标(source 列),没有它就只能在几十万条哈希里瞎猜。
            if (filters.Length == 0
                || !filters.Any(f => f.IsMatch(fileName) || (chkPath is { Length: > 0 } && f.IsMatch(chkPath))))
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
