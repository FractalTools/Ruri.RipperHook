using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// Which byte of a cooked preshader program means which operation, for the engine that cooked it.
///
/// A material's numeric members are filled by little stack programs, one byte per instruction,
/// numbered by the engine's own opcode enum -- and the engine renumbers that enum whenever it
/// inserts an instruction. 4.26 reads parameters with TWO instructions, one per parameter table,
/// and calls addition five; 5.x reads them with one and calls addition four; 5.5 inserted two
/// more in the middle again. Carrying one hand-written renumbering per engine version is the
/// same class of mistake as carrying one texture-bucket order per version: it is an engine fact,
/// so it is read from the engine, dumped by Ruri.UEShaderTpkDumper beside the other ones.
///
/// This states only what the EVALUATOR implements, in its own order, and looks each name up in
/// whatever the build's enum called it. A build whose enum names an operation this does not
/// implement yields <see cref="Unknown"/>, which stops a program rather than silently running
/// somebody else's instruction -- and an evaluator reading a pre-UE5 program with no translation
/// at all ran exactly that: every opcode from addition onward off by one, for every such title.
/// </summary>
internal sealed class MaterialPreshaderOpcodes
{
    public const byte Unknown = 255;

    /// <summary>
    /// The operations the evaluator implements, in the order it numbers them. A program's byte is
    /// translated into a position in this list; the list is the evaluator's own vocabulary, and
    /// the only thing in this lane allowed to state an opcode number.
    /// </summary>
    private static readonly string[] Implemented =
    [
        "Nop", "ConstantZero", "Constant", "Parameter",
        "Add", "Sub", "Mul", "Div", "Fmod",
        "Min", "Max", "Clamp",
        "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2",
        "Dot", "Cross", "Sqrt", "Rcp", "Length", "Normalize", "Saturate",
        "Abs", "Floor", "Ceil", "Round", "Trunc", "Sign", "Frac", "Fractional",
        "Log2", "Log10", "ComponentSwizzle", "AppendVector",
        "TextureSize", "TexelSize",
        "ExternalTextureCoordinateScaleRotation", "ExternalTextureCoordinateOffset",
        "RuntimeVirtualTextureUniform",
        "GetField", "SetField", "Neg",
        "Jump", "JumpIfFalse", "PushValue",
        "Less", "Assign", "Greater", "LessEqual", "GreaterEqual",
        "Exp", "Exp2", "Log",
    ];

    /// <summary>
    /// What an engine calls the one operation it spells differently. Before UE5 a program said
    /// which TABLE it was reading from, and the table is already known from the program that
    /// carries the opcode, so both spellings are the same instruction here.
    /// </summary>
    private static readonly Dictionary<string, string> SpelledDifferently = new(StringComparer.Ordinal)
    {
        ["ScalarParameter"] = "Parameter",
        ["VectorParameter"] = "Parameter",
    };

    public const string FolderName = "_MaterialPreshader";
    public const string FileName = "_Opcodes.json";

    private readonly byte[] _translated;

    private MaterialPreshaderOpcodes(byte[] translated, string source, int stated, int unimplemented)
    {
        _translated = translated;
        Source = source;
        Stated = stated;
        Unimplemented = unimplemented;
    }

    /// <summary>The instruction set every engine byte maps straight through -- what a build with no dump gets.</summary>
    public static MaterialPreshaderOpcodes Identity { get; } = BuildIdentity();

    public string Source { get; }

    /// <summary>How many opcodes the engine's own enum stated.</summary>
    public int Stated { get; }

    /// <summary>How many of those this evaluator does not implement.</summary>
    public int Unimplemented { get; }

    /// <summary>The byte this build writes for one of the operations the evaluator implements, or <see cref="Unknown"/>.</summary>
    public byte Of(string operation)
    {
        for (int raw = 0; raw < _translated.Length; raw++)
        {
            if (_translated[raw] != Unknown && Implemented[_translated[raw]] == operation)
            {
                return (byte)raw;
            }
        }
        return Unknown;
    }

    /// <summary>What one byte of a cooked program means, as this evaluator numbers its operations.</summary>
    public byte Translate(byte raw) => _translated[raw];

    public static MaterialPreshaderOpcodes LoadForGame(string? directory, string? gameVersion, bool tryBaseFallback,
        Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[MaterialPreshader] No engine metadata at '{directory ?? "<null>"}' — opcodes read as the evaluator's own numbering.");
            return Identity;
        }

        foreach (string root in EngineUbMetadataRegistry.BuildScanRoots(directory, gameVersion, tryBaseFallback))
        {
            string file = Path.Combine(root, FolderName, FileName);
            if (!File.Exists(file))
            {
                continue;
            }
            try
            {
                return Read(file, log);
            }
            catch (Exception exception)
            {
                logError?.Invoke($"[MaterialPreshader] {file}: {exception.GetType().Name}: {exception.Message}");
                return Identity;
            }
        }

        log?.Invoke($"[MaterialPreshader] No opcode dump for game={gameVersion ?? "<none>"} — opcodes read as the evaluator's own numbering.");
        return Identity;
    }

    private static MaterialPreshaderOpcodes Read(string file, Action<string>? log)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        if (!document.RootElement.TryGetProperty("Opcodes", out JsonElement stated) || stated.ValueKind != JsonValueKind.Array)
        {
            return Identity;
        }

        byte[] translated = new byte[256];
        Array.Fill(translated, Unknown);
        int count = 0, unimplemented = 0;
        foreach (JsonElement entry in stated.EnumerateArray())
        {
            string name = entry.GetString() ?? string.Empty;
            if (count >= translated.Length)
            {
                break;
            }
            string operation = SpelledDifferently.TryGetValue(name, out string? spelling) ? spelling : name;
            int position = Array.IndexOf(Implemented, operation);
            if (position < 0)
            {
                unimplemented++;
            }
            else
            {
                translated[count] = (byte)position;
            }
            count++;
        }

        log?.Invoke($"[MaterialPreshader] {count} opcode(s) from '{file}'"
                    + (unimplemented > 0 ? $", {unimplemented} the evaluator does not implement." : "."));
        return new MaterialPreshaderOpcodes(translated, file, count, unimplemented);
    }

    private static MaterialPreshaderOpcodes BuildIdentity()
    {
        byte[] translated = new byte[256];
        Array.Fill(translated, Unknown);
        for (int i = 0; i < Implemented.Length; i++)
        {
            translated[i] = (byte)i;
        }
        return new MaterialPreshaderOpcodes(translated, string.Empty, Implemented.Length, 0);
    }
}
