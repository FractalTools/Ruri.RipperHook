using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// HashedNamesResolver — FHashedName-equivalent resolver. UE's FHashedName
// (Engine/Source/Runtime/Core/Public/Serialization/MemoryImage.h:850)
// hashes UPPERCASED UTF-8 / ASCII bytes with CityHash64WithSeed where
// the seed is the FName's internal number (0 for shader/struct type
// names). Cooked metadata strips type names but keeps the 64-bit hash;
// to recover names we either:
//   (a) hash everything in TypeDependencies and look up in that map
//       (preferred — purely metadata-driven; what Pass 050 uses), or
//   (b) scan UE source's `IMPLEMENT_*_SHADER_TYPE` macros and hash the
//       captured names (fallback — works without TypeDependencies).
//
// Both paths converge on `HashName()`, the public CityHash entry; the
// `Resolve*` accessors are the (b)-path UE-source-scan fallback.
//
// This is a STATIC UTILITY, not a sequenced pass — it's consumed on
// demand by Pass 050 (BuildStableShaderRecords) when it needs to
// translate an FHashedName 64-bit hash back to a readable type name.
// That's why it lacks a "Pass NNN_" prefix: it has no execution slot
// in the pipeline; the orchestrator never calls it directly.
internal static class HashedNamesResolver
{
    private static readonly object Lock = new();
    private static Dictionary<string, string>? _shaderTypeNames;
    private static Dictionary<string, string>? _vertexFactoryNames;
    private static Dictionary<string, string>? _pipelineNames;

    private static readonly Regex ShaderTypeRegex = new(@"IMPLEMENT_(?:MESH_)?(?:MATERIAL_)?SHADER_TYPE\([^,]*,\s*([A-Za-z_][A-Za-z0-9_:<>]*)", RegexOptions.Compiled);
    private static readonly Regex VertexFactoryRegex = new(@"IMPLEMENT_VERTEX_FACTORY_TYPE\(\s*([A-Za-z_][A-Za-z0-9_:<>]*)", RegexOptions.Compiled);
    private static readonly Regex ShaderPipelineRegex = new(@"IMPLEMENT_SHADERPIPELINE_TYPE(?:_[A-Z]+)?\(\s*([A-Za-z_][A-Za-z0-9_:<>]*)", RegexOptions.Compiled);

    public static string ResolveShaderTypeName(string hash) => Resolve(hash, Ensure().shaderTypes);
    public static string ResolveVertexFactoryTypeName(string hash) => Resolve(hash, Ensure().vertexFactories);
    public static string ResolvePipelineTypeName(string hash) => Resolve(hash, Ensure().pipelines);

    // FHashedName-equivalent hash. Mirror of
    // Engine/Source/Runtime/Core/Private/Serialization/MemoryImage.cpp:1159-1214:
    // input is uppercased, ANSI-direct (or UTF-8 for wide) bytes, hashed
    // with CityHash64WithSeed(InternalNumber). For shader/struct type
    // names the FName has no number suffix so seed=0.
    //
    // Public so other components (notably the unified-metadata exporter)
    // can hash TypeDependencies entries to recover symbolic names without
    // scanning the entire UE source tree.
    public static string HashName(string name) => ComputeHashedName(name);

    private static string Resolve(string hash, Dictionary<string, string> map)
    {
        if (string.IsNullOrWhiteSpace(hash)) return string.Empty;
        return map.TryGetValue(hash, out string? name) ? name : string.Empty;
    }

    private static (Dictionary<string, string> shaderTypes, Dictionary<string, string> vertexFactories, Dictionary<string, string> pipelines) Ensure()
    {
        lock (Lock)
        {
            if (_shaderTypeNames != null && _vertexFactoryNames != null && _pipelineNames != null)
            {
                return (_shaderTypeNames, _vertexFactoryNames, _pipelineNames);
            }

            // Primary path: load the precomputed _HashToName.json files
            // emitted by Ruri.UEShaderTpkDumper under
            // <exeDir>/EngineUbMetadata/<version>/_<TypeKind>/_HashToName.json.
            // This is the canonical source of (hash → name) now that the
            // TPK dumper has replaced the ad-hoc Python generator — it
            // covers `##`-concatenated wrapper-macro expansions (e.g.
            // TLightMapDensityPSFDummyLightMapPolicy) that a naïve regex
            // scan over IMPLEMENT_*_SHADER_TYPE call sites can't see.
            //
            // We sweep recursively because the deploy holds multiple
            // engine versions side-by-side (5.0.3/, 5.1.1/, …, plus the
            // legacy GAME_UE5_X/ folders left over from before the
            // version-aware migration). CityHash collisions across
            // versions are statistically negligible so cross-version
            // merge is safe.
            _shaderTypeNames = LoadPrecomputedHashIndex("_ShaderType");
            _vertexFactoryNames = LoadPrecomputedHashIndex("_VertexFactoryType");
            _pipelineNames = LoadPrecomputedHashIndex("_ShaderPipelineType");

            // Fallback: if the deploy doesn't carry _HashToName.json
            // (uninstalled or stripped build), fall back to a runtime
            // regex scan of an explicitly configured UE source tree.
            // Empty hash-maps after the precomputed load only happens
            // for misconfigured installs — log it loudly.
            if (_shaderTypeNames.Count == 0 && _vertexFactoryNames.Count == 0 && _pipelineNames.Count == 0)
            {
                string? envRoot = Environment.GetEnvironmentVariable("UE_SOURCE_ROOT");
                if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
                {
                    _shaderTypeNames = BuildMap(envRoot, ShaderTypeRegex);
                    _vertexFactoryNames = BuildMap(envRoot, VertexFactoryRegex);
                    _pipelineNames = BuildMap(envRoot, ShaderPipelineRegex);
                }
            }
            return (_shaderTypeNames, _vertexFactoryNames, _pipelineNames);
        }
    }

    // Walks <exeDir>/EngineUbMetadata/ recursively and merges every
    // `<typeKind>/_HashToName.json` it finds. Each file is a flat
    // `{ "<HEX64>": "<TypeName>", ... }` object; later entries on hash
    // collision are dropped (TryAdd).
    private static Dictionary<string, string> LoadPrecomputedHashIndex(string typeKindFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string exeDir = AppContext.BaseDirectory;
        string root = Path.Combine(exeDir, "EngineUbMetadata");
        if (!Directory.Exists(root)) return map;

        foreach (string file in Directory.EnumerateFiles(root, "_HashToName.json", SearchOption.AllDirectories))
        {
            // Match only the requested type-kind subfolder. Path separators
            // vary across OS so check both directions.
            string folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
            if (!string.Equals(folder, typeKindFolder, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using FileStream stream = File.OpenRead(file);
                using JsonDocument doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                // The TPK emitter wraps the actual map under an `Entries`
                // object alongside `Note` / `EntryCount` metadata. Older
                // hand-organised files use a flat root object — accept
                // either shape so the loader stays robust.
                JsonElement entries = doc.RootElement.TryGetProperty("Entries", out JsonElement nested)
                    ? nested
                    : doc.RootElement;
                if (entries.ValueKind != JsonValueKind.Object) continue;

                foreach (JsonProperty kv in entries.EnumerateObject())
                {
                    string hash = kv.Name;
                    if (kv.Value.ValueKind != JsonValueKind.String) continue;
                    string? name = kv.Value.GetString();
                    if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(name)) continue;
                    map.TryAdd(hash, name!);
                }
            }
            catch
            {
                // Tolerate malformed/partial JSONs — one bad file shouldn't
                // poison the whole hash-to-name index. Silent here because
                // this code path runs lazily on first Resolve* call; the
                // caller has no good place to surface a parse warning.
            }
        }
        return map;
    }

    private static Dictionary<string, string> BuildMap(string root, Regex regex)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return map;

        foreach (string file in Directory.EnumerateFiles(root, "*.cpp", SearchOption.AllDirectories))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            foreach (Match match in regex.Matches(text))
            {
                if (!match.Success || match.Groups.Count < 2) continue;
                string rawName = match.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(rawName)) continue;

                string hash = ComputeHashedName(rawName);
                map.TryAdd(hash, rawName);
            }
        }

        return map;
    }

    private static string ComputeHashedName(string name)
    {
        string upper = name.ToUpperInvariant();
        byte[] bytes = Encoding.UTF8.GetBytes(upper);
        ulong hash = CityHash64WithSeed(bytes, 0UL);
        return hash.ToString("X16");
    }

    // CityHash relies on naturally-wrapping ulong arithmetic, but this
    // project compiles with `<CheckForOverflowUnderflow>true</...>` which
    // makes EVERY ulong * ulong / ulong - ulong throw OverflowException
    // the first time a result wraps. Mark the full hash machinery
    // `unchecked` so the CityHash-spec-correct wrapping is preserved.
    //
    // Constants from UE's canonical CityHash 1.1.0:
    //   D:/GameStudy/UnrealEngine-5.1.1-release/Engine/Source/Runtime/Core/Private/Hash/CityHash.cpp:122-124
    //   k0 = 0xc3a5c85c97cb3127  k1 = 0xb492b66fbe98f273  k2 = 0x9ae16a3b2f90404f
    // The Murmur-inspired final-mix uses a DIFFERENT constant `kMul` that
    // only appears in the 2-arg HashLen16(u,v) form (Hash128to64::kMul).
    // Earlier versions of this file substituted kMul for k1/k2 in places
    // and silently produced wrong hashes — the issue was caught when the
    // Python port at `_generator/gen_ub_metadata.py` was cross-checked
    // against cooked TypeDependency hashes.
    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;
    private const ulong KMulHash16 = 0x9ddfea08eb382d69UL;

    // CityHash64WithSeed(s, len, seed) per CityHash.cpp:430-440:
    //   CityHash64WithSeeds(s, len, k2, seed) = HashLen16(CityHash64(s) - k2, seed)
    private static ulong CityHash64WithSeed(byte[] s, ulong seed) => unchecked(
        HashLen16(CityHash64(s) - K2, seed));

    private static ulong CityHash64(byte[] s)
    {
        unchecked
        {
        int len = s.Length;
        if (len <= 16) return HashLen0to16(s);
        if (len <= 32) return HashLen17to32(s);
        if (len <= 64) return HashLen33to64(s);

        ulong x = Fetch64(s, len - 40);
        ulong y = Fetch64(s, len - 16) + Fetch64(s, len - 56);
        ulong z = HashLen16(Fetch64(s, len - 48) + (ulong)len, Fetch64(s, len - 24));
        (ulong low, ulong high) v = WeakHashLen32WithSeeds(s, len - 64, (ulong)len, z);
        (ulong low, ulong high) w = WeakHashLen32WithSeeds(s, len - 32, y + K1, x);
        x = x * K1 + Fetch64(s, 0);

        int offset = 0;
        len = (len - 1) & ~63;
        do
        {
            x = RotateRight(x + y + v.low + Fetch64(s, offset + 8), 37) * K1;
            y = RotateRight(y + v.high + Fetch64(s, offset + 48), 42) * K1;
            x ^= w.high;
            y += v.low + Fetch64(s, offset + 40);
            z = RotateRight(z + w.low, 33) * K1;
            v = WeakHashLen32WithSeeds(s, offset, v.high * K1, x + w.low);
            w = WeakHashLen32WithSeeds(s, offset + 32, z + w.high, y + Fetch64(s, offset + 16));
            (x, z) = (z, x);
            offset += 64;
            len -= 64;
        } while (len != 0);

        return HashLen16(HashLen16(v.low, w.low) + ShiftMix(y) * K1 + z, HashLen16(v.high, w.high) + x);
        }
    }

    private static ulong HashLen0to16(byte[] s)
    {
        unchecked
        {
        int len = s.Length;
        if (len >= 8)
        {
            ulong mul = K2 + (ulong)len * 2UL;
            ulong a = Fetch64(s, 0) + K2;
            ulong b = Fetch64(s, len - 8);
            ulong c = RotateRight(b, 37) * mul + a;
            ulong d = (RotateRight(a, 25) + b) * mul;
            return HashLen16(c, d, mul);
        }
        if (len >= 4)
        {
            ulong mul = K2 + (ulong)len * 2UL;
            ulong a = Fetch32(s, 0);
            return HashLen16((ulong)len + (a << 3), Fetch32(s, len - 4), mul);
        }
        if (len > 0)
        {
            uint a = s[0];
            uint b = s[len >> 1];
            uint c = s[len - 1];
            uint y = a + (b << 8);
            uint z = (uint)len + (c << 2);
            return ShiftMix(y * K2 ^ z * K0) * K2;
        }
        return K2;
        }
    }

    private static ulong HashLen17to32(byte[] s)
    {
        unchecked
        {
        int len = s.Length;
        ulong mul = K2 + (ulong)len * 2UL;
        ulong a = Fetch64(s, 0) * K1;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, len - 8) * mul;
        ulong d = Fetch64(s, len - 16) * K2;
        return HashLen16(RotateRight(a + b, 43) + RotateRight(c, 30) + d, a + RotateRight(b + K2, 18) + c, mul);
        }
    }

    private static ulong HashLen33to64(byte[] s)
    {
        unchecked
        {
        int len = s.Length;
        ulong mul = K2 + (ulong)len * 2UL;
        ulong a = Fetch64(s, 0) * K2;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, len - 24);
        ulong d = Fetch64(s, len - 32);
        ulong e = Fetch64(s, 16) * K2;
        ulong f = Fetch64(s, 24) * 9UL;
        ulong g = Fetch64(s, len - 8);
        ulong h = Fetch64(s, len - 16) * mul;
        ulong u = RotateRight(a + g, 43) + (RotateRight(b, 30) + c) * 9UL;
        ulong v = ((a + g) ^ d) + f + 1UL;
        ulong w = ReverseBytes((u + v) * mul) + h;
        ulong x = RotateRight(e + f, 42) + c;
        ulong y = (ReverseBytes((v + w) * mul) + g) * mul;
        ulong z = e + f + c;
        a = ReverseBytes((x + z) * mul + y) + b;
        b = ShiftMix((z + a) * mul + d + h) * mul;
        return b + x;
        }
    }

    private static (ulong low, ulong high) WeakHashLen32WithSeeds(byte[] s, int offset, ulong a, ulong b)
    {
        unchecked
        {
        ulong w = Fetch64(s, offset);
        ulong x = Fetch64(s, offset + 8);
        ulong y = Fetch64(s, offset + 16);
        ulong z = Fetch64(s, offset + 24);
        a += w;
        b = RotateRight(b + a + z, 21);
        ulong c = a;
        a += x + y;
        b += RotateRight(a, 44);
        return (a + z, b + c);
        }
    }

    private static ulong HashLen16(ulong u, ulong v) => HashLen16(u, v, KMulHash16);

    private static ulong HashLen16(ulong u, ulong v, ulong mul)
    {
        unchecked
        {
        ulong a = (u ^ v) * mul;
        a ^= a >> 47;
        ulong b = (v ^ a) * mul;
        b ^= b >> 47;
        b *= mul;
        return b;
        }
    }

    private static ulong ShiftMix(ulong val) => unchecked(val ^ (val >> 47));
    private static ulong RotateRight(ulong val, int shift) => unchecked((val >> shift) | (val << (64 - shift)));
    private static ulong ReverseBytes(ulong value) => BinaryPrimitives.ReverseEndianness(value);
    private static uint Fetch32(byte[] s, int pos) => BitConverter.ToUInt32(s, pos);
    private static ulong Fetch64(byte[] s, int pos) => BitConverter.ToUInt64(s, pos);
}
