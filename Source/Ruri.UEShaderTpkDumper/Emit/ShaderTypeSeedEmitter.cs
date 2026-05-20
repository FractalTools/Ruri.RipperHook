using System.Text.Json;
using Ruri.UEShaderTpkDumper.Core;
using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper.Emit;

// Emits one `<ClassName>_<HashedName:016X>_MetaData.json` per FShader subclass
// that declares LAYOUT_FIELD parameters. Mirrors the Python generator's
// `emit_shader_type_seeds`. Each seed lists the parameter NAMES in source
// declaration order with placeholder offsets — the decompile-side
// `TryReconcileGlobalsCB` pairs the names with cook-side real offsets at
// runtime.
public static class ShaderTypeSeedEmitter
{
    public static int Emit(string outRootForVersion, IEnumerable<ShaderTypeClass> classes, string engineVersion)
    {
        string targetDir = Path.Combine(outRootForVersion, "_ShaderType");
        Directory.CreateDirectory(targetDir);
        int written = 0;
        foreach (ShaderTypeClass cls in classes)
        {
            ulong hash = CityHash64.HashWithSeed(cls.CppName);
            string fileName = $"{cls.CppName}_{hash:X16}_MetaData.json";
            string filePath = Path.Combine(targetDir, fileName);

            // Build ConstantBuffer payload — VectorParameters carry the
            // source-declared names with PLACEHOLDER offsets (sequential 0,
            // 16, 32, ... — the decompile-side `Pass165` joins these with
            // cook-side real offsets at runtime).
            var vectorParams = new List<Dictionary<string, object?>>();
            var resources = new List<Dictionary<string, object?>>();
            int slot = 0;
            int placeholderResourceIndex = 0;
            foreach (LayoutField field in cls.Fields)
            {
                if (field.Kind == "Parameter")
                {
                    vectorParams.Add(new Dictionary<string, object?>
                    {
                        ["Name"] = field.Name,
                        ["NameIndex"] = -1,
                        ["Index"] = slot * 16,
                        ["ArraySize"] = 0,
                        ["Type"] = "Float",
                        ["RowCount"] = 4,
                        ["ColumnCount"] = 1,
                        ["IsMatrix"] = false,
                    });
                    slot++;
                }
                else if (field.Kind == "Resource")
                {
                    resources.Add(new Dictionary<string, object?>
                    {
                        ["Index"] = placeholderResourceIndex,
                        ["Offset"] = placeholderResourceIndex * 8,
                        ["Name"] = field.Name,
                        ["UbmtType"] = "UBMT_TEXTURE",
                    });
                    placeholderResourceIndex++;
                }
            }

            var obj = new Dictionary<string, object?>
            {
                ["Name"] = cls.CppName,
                ["EngineVersion"] = engineVersion,
                ["EngineSource"] = Path.GetRelativePath(Path.GetDirectoryName(cls.SourceFile) ?? "", cls.SourceFile),
                ["LayoutHash"] = $"0x{hash:X16}",
                ["BindingFlags"] = "Shader",
                ["ConstantBuffer"] = new Dictionary<string, object?>
                {
                    ["Name"] = "$Globals",
                    ["NameIndex"] = -1,
                    ["MatrixParameters"] = new List<object>(),
                    ["VectorParameters"] = vectorParams,
                    ["StructParameters"] = new List<object>(),
                    ["Size"] = vectorParams.Count * 16,
                    ["IsPartialCB"] = false,
                },
                ["Textures"] = resources.Where(r => (string?)r["UbmtType"] == "UBMT_TEXTURE").ToList(),
                ["Samplers"] = new List<object>(),
                ["Buffers"] = new List<object>(),
                ["UAVs"] = new List<object>(),
                ["Resources"] = resources,
                ["Debug"] = new Dictionary<string, object?>
                {
                    ["Note"] = "Source-declared LAYOUT_FIELD names with placeholder offsets. "
                            + "Decompile-side TryReconcileGlobalsCB pairs these names with cook-side "
                            + "ParameterMapInfo.LooseParameterBuffers[0].Parameters[] real byte offsets.",
                },
            };

            JsonSerializerOptions opts = new()
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(obj, opts) + "\n");
            written++;
        }
        return written;
    }
}
