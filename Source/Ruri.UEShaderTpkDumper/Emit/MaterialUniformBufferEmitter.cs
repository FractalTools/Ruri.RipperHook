using System.Text.Json;
using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper.Emit;

/// <summary>
/// Writes one engine version's material uniform buffer recipe where the decompiler reads its
/// other engine facts. What lands is the member ORDER with name templates and element counts
/// stated as what they count -- never concrete offsets, because those are a material's own and
/// are replayed from its counts at read time.
/// </summary>
public static class MaterialUniformBufferEmitter
{
    public const string FolderName = "_MaterialUniformBuffer";
    public const string FileName = "_Layout.json";

    public static int Emit(string outRootForVersion, MaterialBufferRecipe recipe, string engineVersion)
    {
        string targetDir = Path.Combine(outRootForVersion, FolderName);
        Directory.CreateDirectory(targetDir);

        var members = recipe.Members.Select(member => new Dictionary<string, object?>
        {
            ["Name"] = member.Name,
            ["NameTemplate"] = member.NameTemplate,
            ["ShaderType"] = member.ShaderType,
            ["Ubmt"] = member.Ubmt,
            ["Rows"] = member.Rows,
            ["Columns"] = member.Columns,
            ["RepeatOver"] = member.RepeatOver,
            ["Condition"] = member.Condition,
            ["CountSource"] = member.Count.Source,
            ["CountMultiply"] = member.Count.Multiply,
            ["CountDivideRoundUp"] = member.Count.DivideRoundUp,
            ["CountExpression"] = member.Count.Expression,
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["Note"] = "FUniformExpressionSet::CreateBufferStruct(), read from engine source. The member "
                     + "order IS the buffer's layout; a member's offset is replayed from the material's own "
                     + "counts, one element of Rows x Columns floats for a numeric member and one pointer "
                     + "slot for a resource. Nothing here is version-specific in the reader: this file is.",
            ["EngineVersion"] = engineVersion,
            ["SourceFile"] = recipe.SourceFile,
            ["TextureParameterTypes"] = recipe.TextureParameterTypes,
            ["StructAlignment"] = recipe.StructAlignment,
            ["PointerAlignment"] = recipe.PointerAlignment,
            ["MemberCount"] = members.Count,
            ["Members"] = members,
            ["Unresolved"] = recipe.Unresolved,
        };

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(Path.Combine(targetDir, FileName), JsonSerializer.Serialize(payload, options) + "\n");
        return members.Count;
    }
}
