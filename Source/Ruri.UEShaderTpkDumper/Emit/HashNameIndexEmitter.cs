using System.Text.Json;
using Ruri.UEShaderTpkDumper.Core;

namespace Ruri.UEShaderTpkDumper.Emit;

// Writes `_HashToName.json` files for the three hash-keyed indexes:
//   * `_ShaderType/_HashToName.json`        — FShaderType::HashedName → class name
//   * `_VertexFactoryType/_HashToName.json` — FVertexFactoryType::HashedName → class name
//   * `_ShaderPipelineType/_HashToName.json`— FShaderPipelineType::HashedName → pipeline name
//
// The hash math is CityHash64WithSeed(UPPER(name), seed=0). Output is sorted
// by hash key for deterministic re-runs (set iteration is unordered in C#).
public static class HashNameIndexEmitter
{
    public static int Emit(string outRootForVersion, string subfolder, string note, IEnumerable<string> names)
    {
        Dictionary<string, string> hashToName = new();
        foreach (string n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            ulong h = CityHash64.HashWithSeed(n);
            string key = h.ToString("X16");
            hashToName.TryAdd(key, n);
        }
        // Sort for deterministic byte-identical re-runs.
        var sorted = new SortedDictionary<string, string>(hashToName, StringComparer.Ordinal);
        string targetDir = Path.Combine(outRootForVersion, subfolder);
        Directory.CreateDirectory(targetDir);
        string targetFile = Path.Combine(targetDir, "_HashToName.json");
        var obj = new Dictionary<string, object?>
        {
            ["Note"] = note,
            ["EntryCount"] = sorted.Count,
            ["Entries"] = sorted,
        };
        JsonSerializerOptions opts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(targetFile, JsonSerializer.Serialize(obj, opts) + "\n");
        return sorted.Count;
    }
}
