using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

/// <summary>The opcodes one engine's preshader programs are written in, in the order it numbers them.</summary>
public sealed record PreshaderOpcodeSet(string SourceFile, string EnumName, IReadOnlyList<string> Opcodes);

/// <summary>
/// The preshader instruction set as the ENGINE numbers it.
///
/// A material's numeric members are filled by little stack programs whose every instruction is
/// one byte of this enum, and the engine renumbers it whenever it inserts an opcode: 4.26 reads
/// parameters with two instructions (one per table) and calls addition five, 5.x reads them with
/// one and calls addition four. Reading the numbering here turns "which byte means what" into
/// data, so the evaluator states only which operations it implements and never which engine
/// wrote the program.
/// </summary>
public static class PreshaderOpcodeScanner
{
    /// <summary>Where an engine states its preshader instruction set, newest spelling first.</summary>
    private static readonly (string File, string Enum)[] Declarations =
    {
        ("Engine/Source/Runtime/Engine/Public/Shader/Preshader.h", "EPreshaderOpcode"),
        ("Engine/Source/Runtime/Engine/Public/MaterialShared.h", "EMaterialPreshaderOpcode"),
    };

    public static PreshaderOpcodeSet? Scan(string engineRoot)
    {
        foreach ((string relative, string enumName) in Declarations)
        {
            string file = Path.Combine(engineRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                continue;
            }
            Match block = Regex.Match(File.ReadAllText(file),
                @"enum\s+class\s+" + enumName + @"\s*:\s*\w+\s*\{(?<body>[^}]*)\}",
                RegexOptions.Singleline);
            if (!block.Success)
            {
                continue;
            }
            List<string> opcodes = new();
            foreach (string entry in block.Groups["body"].Value.Split(','))
            {
                string name = Regex.Replace(entry, @"//[^\r\n]*", string.Empty).Trim();
                if (name.Length == 0 || name.Contains('=', StringComparison.Ordinal))
                {
                    continue;
                }
                opcodes.Add(name);
            }
            if (opcodes.Count > 0)
            {
                return new PreshaderOpcodeSet(relative, enumName, opcodes);
            }
        }
        return null;
    }
}
