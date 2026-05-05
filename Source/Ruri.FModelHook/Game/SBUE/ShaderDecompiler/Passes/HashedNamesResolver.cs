using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
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

            string runtimeRoot = @"E:\UnrealEngine-5.2.1-release\Engine\Source\Runtime";
            _shaderTypeNames = BuildMap(runtimeRoot, ShaderTypeRegex);
            _vertexFactoryNames = BuildMap(runtimeRoot, VertexFactoryRegex);
            _pipelineNames = BuildMap(runtimeRoot, ShaderPipelineRegex);
            return (_shaderTypeNames, _vertexFactoryNames, _pipelineNames);
        }
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

    // Minimal CityHash64WithSeed port for FHashedName(name, number=0).
    private static ulong CityHash64WithSeed(byte[] s, ulong seed) => unchecked(
        HashLen16(CityHash64(s) - seed, seed ^ 0x9ae16a3b2f90404fUL));

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
        (ulong low, ulong high) w = WeakHashLen32WithSeeds(s, len - 32, y + 0x9ddfea08eb382d69UL, x);
        x = x * 0x9ddfea08eb382d69UL + Fetch64(s, 0);

        int offset = 0;
        len = (len - 1) & ~63;
        do
        {
            x = RotateRight(x + y + v.low + Fetch64(s, offset + 8), 37) * 0x9ddfea08eb382d69UL;
            y = RotateRight(y + v.high + Fetch64(s, offset + 48), 42) * 0x9ddfea08eb382d69UL;
            x ^= w.high;
            y += v.low + Fetch64(s, offset + 40);
            z = RotateRight(z + w.low, 33) * 0x9ddfea08eb382d69UL;
            v = WeakHashLen32WithSeeds(s, offset, v.high * 0x9ddfea08eb382d69UL, x + w.low);
            w = WeakHashLen32WithSeeds(s, offset + 32, z + w.high, y + Fetch64(s, offset + 16));
            (x, z) = (z, x);
            offset += 64;
            len -= 64;
        } while (len != 0);

        return HashLen16(HashLen16(v.low, w.low) + ShiftMix(y) * 0x9ddfea08eb382d69UL + z, HashLen16(v.high, w.high) + x);
        }
    }

    private static ulong HashLen0to16(byte[] s)
    {
        unchecked
        {
        int len = s.Length;
        if (len >= 8)
        {
            ulong mul = 0x9ddfea08eb382d69UL + (ulong)len * 2UL;
            ulong a = Fetch64(s, 0) + 0x9ae16a3b2f90404fUL;
            ulong b = Fetch64(s, len - 8);
            ulong c = RotateRight(b, 37) * mul + a;
            ulong d = (RotateRight(a, 25) + b) * mul;
            return HashLen16(c, d, mul);
        }
        if (len >= 4)
        {
            ulong mul = 0x9ddfea08eb382d69UL + (ulong)len * 2UL;
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
            return ShiftMix(y * 0x9ddfea08eb382d69UL ^ z * 0xc3a5c85c97cb3127UL) * 0x9ddfea08eb382d69UL;
        }
        return 0x9ae16a3b2f90404fUL;
        }
    }

    private static ulong HashLen17to32(byte[] s)
    {
        unchecked
        {
        int len = s.Length;
        ulong mul = 0x9ddfea08eb382d69UL + (ulong)len * 2UL;
        ulong a = Fetch64(s, 0) * 0xc3a5c85c97cb3127UL;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, len - 8) * mul;
        ulong d = Fetch64(s, len - 16) * 0x9ddfea08eb382d69UL;
        return HashLen16(RotateRight(a + b, 43) + RotateRight(c, 30) + d, a + RotateRight(b + 0x9ae16a3b2f90404fUL, 18) + c, mul);
        }
    }

    private static ulong HashLen33to64(byte[] s)
    {
        unchecked
        {
        int len = s.Length;
        ulong mul = 0x9ddfea08eb382d69UL + (ulong)len * 2UL;
        ulong a = Fetch64(s, 0) * 0x9ddfea08eb382d69UL;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, len - 24);
        ulong d = Fetch64(s, len - 32);
        ulong e = Fetch64(s, 16) * 0x9ddfea08eb382d69UL;
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

    private static ulong HashLen16(ulong u, ulong v) => HashLen16(u, v, 0x9ddfea08eb382d69UL);

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
