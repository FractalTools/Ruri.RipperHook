using System.Text.Json;
using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper.Emit;

/// <summary>
/// Writes one engine version's preshader instruction set where the decompiler reads its other
/// engine facts. What lands is the opcode ORDER -- a name's position IS its byte -- so a reader
/// states which operations it implements and looks up the byte, instead of carrying one
/// renumbering table per engine version.
/// </summary>
public static class PreshaderOpcodeEmitter
{
    public const string FolderName = "_MaterialPreshader";
    public const string FileName = "_Opcodes.json";

    public static int Emit(string outRootForVersion, PreshaderOpcodeSet opcodes, string engineVersion)
    {
        string targetDir = Path.Combine(outRootForVersion, FolderName);
        Directory.CreateDirectory(targetDir);

        var payload = new Dictionary<string, object?>
        {
            ["Note"] = "The engine's own preshader opcode enum. A name's INDEX in Opcodes is the byte "
                     + "the cooked program carries for it, and that numbering shifts whenever the engine "
                     + "inserts an instruction, so nothing downstream may assume a value.",
            ["EngineVersion"] = engineVersion,
            ["SourceFile"] = opcodes.SourceFile,
            ["EnumName"] = opcodes.EnumName,
            ["OpcodeCount"] = opcodes.Opcodes.Count,
            ["Opcodes"] = opcodes.Opcodes,
        };

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(Path.Combine(targetDir, FileName), JsonSerializer.Serialize(payload, options) + "\n");
        return opcodes.Opcodes.Count;
    }
}
