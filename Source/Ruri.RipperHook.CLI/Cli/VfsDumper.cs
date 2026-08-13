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
            throw new InvalidOperationException("当前 --hook 没有提供 VFS 访问(需要 EndField 这类 VFS 游戏)。");
        }
        string[] roots = Session.RootsOrThrow("--dump-vfs");
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
