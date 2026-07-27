using System;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// 1:1 port of <c>AssetRipper.AssemblyDumper.Passes.ValidNameGenerator</c>.
///
/// The assembly dumper named every generated field after its type tree node, run through this
/// sanitizer (<c>Pass002_RenameSubnodes.FixNamesRecursively</c> then <c>Pass015_AddFields</c>).
/// Binding a raw tpk node onto an already generated AssetRipper type therefore has to apply the
/// exact same transform -- e.g. <c>m_MeshMetrics[0]</c> only matches the field
/// <c>m_MeshMetrics_0_</c>, and <c>size</c> only matches <c>m_Size</c>.
/// </summary>
public static partial class TypeTreeNameFixer
{
    public static string GetValidFieldName(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
        {
            throw new ArgumentException("Nodes cannot have a null or whitespace name", nameof(originalName));
        }

        string result = ReplaceBadCharacters(originalName);
        if (char.IsDigit(result[0]) || !result.StartsWith("m_", StringComparison.Ordinal))
        {
            result = "m_" + result;
        }
        if (char.IsLower(result[2]))
        {
            string remaining = result.Length > 3 ? result.Substring(3) : "";
            result = $"m_{char.ToUpperInvariant(result[2])}{remaining}";
        }
        return result;
    }

    public static string GetValidTypeName(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
        {
            throw new ArgumentException("Nodes cannot have a null or whitespace type name", nameof(originalName));
        }

        string result = ReplaceBadCharacters(originalName);
        if (char.IsDigit(result[0]))
        {
            result = "_" + result;
        }
        if (char.IsLower(result[0]) && result.Length > 1)
        {
            result = char.ToUpperInvariant(result[0]) + result.Substring(1);
        }
        return result;
    }

    [GeneratedRegex("[<>\\[\\]\\s&\\(\\):\\.-]", RegexOptions.Compiled)]
    private static partial Regex GetBadCharactersRegex();

    private static string ReplaceBadCharacters(string str) => GetBadCharactersRegex().Replace(str, "_");
}
