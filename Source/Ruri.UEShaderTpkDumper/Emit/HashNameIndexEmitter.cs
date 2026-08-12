using System.Text.Json;
using Ruri.UEShaderTpkDumper.Core;

namespace Ruri.UEShaderTpkDumper.Emit;

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
