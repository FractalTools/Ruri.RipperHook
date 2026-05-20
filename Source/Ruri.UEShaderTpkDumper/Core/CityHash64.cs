namespace Ruri.UEShaderTpkDumper.Core;

// Port of CityHash64 1.1.0 — matches UE's `FNameHash::Compute` /
// `FShaderType::HashedName` math. Kept byte-identical to the C++
// reference (`Engine/Source/Runtime/Core/Public/Hash/CityHash.h`)
// because every cooked TypeHash / VertexFactoryHash / PipelineHash
// depends on it. The Python generator's port (`gen_ub_metadata.py`)
// is the same algorithm — this is a faithful C# rewrite.
public static class CityHash64
{
    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;
    private const ulong KMul = 0x9ddfea08eb382d69UL;

    public static ulong HashWithSeed(string s, ulong seed = 0)
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(s.ToUpperInvariant());
        ulong h = Hash(raw);
        h = unchecked(h - K2);
        return Hash16(h, seed);
    }

    public static ulong Hash(byte[] s)
    {
        int n = s.Length;
        if (n <= 32)
        {
            if (n <= 16) return Hash0To16(s, 0, n);
            return Hash17To32(s, 0, n);
        }
        if (n <= 64) return Hash33To64(s, 0, n);

        ulong x = Fetch64(s, n - 40);
        ulong y = unchecked(Fetch64(s, n - 16) + Fetch64(s, n - 56));
        ulong z = Hash16(unchecked(Fetch64(s, n - 48) + (ulong)n), Fetch64(s, n - 24));
        var (v0, v1) = WeakOffset(s, n - 64, (ulong)n, z);
        var (w0, w1) = WeakOffset(s, n - 32, unchecked(y + K1), x);
        x = unchecked(x * K1 + Fetch64(s, 0));

        int p = 0;
        int remaining = n;
        while (remaining > 64)
        {
            x = unchecked(Ror(x + y + v0 + Fetch64(s, p + 8), 37) * K1);
            y = unchecked(Ror(y + v1 + Fetch64(s, p + 48), 42) * K1);
            x ^= w1;
            y = unchecked(y + v0 + Fetch64(s, p + 40));
            z = unchecked(Ror(z + w0, 33) * K1);
            (v0, v1) = WeakOffset(s, p, unchecked(v1 * K1), unchecked(x + w0));
            (w0, w1) = WeakOffset(s, p + 32, unchecked(z + w1), unchecked(y + Fetch64(s, p + 16)));
            (x, z) = (z, x);
            p += 64;
            remaining -= 64;
        }
        return Hash16(
            unchecked(Hash16(v0, w0) + ShiftMix(y) * K1 + z),
            unchecked(Hash16(v1, w1) + x));
    }

    private static ulong Hash16(ulong u, ulong v) => Hash16Mul(u, v, KMul);

    private static ulong Hash16Mul(ulong u, ulong v, ulong mul)
    {
        ulong a = unchecked((u ^ v) * mul);
        a ^= a >> 47;
        ulong b = unchecked((v ^ a) * mul);
        b ^= b >> 47;
        b = unchecked(b * mul);
        return b;
    }

    private static ulong Hash0To16(byte[] s, int p, int n)
    {
        if (n >= 8)
        {
            ulong mul = unchecked(K2 + (ulong)n * 2);
            ulong a = unchecked(Fetch64(s, p) + K2);
            ulong b = Fetch64(s, p + n - 8);
            ulong c = unchecked(Ror(b, 37) * mul + a);
            ulong d = unchecked((Ror(a, 25) + b) * mul);
            return Hash16Mul(c, d, mul);
        }
        if (n >= 4)
        {
            ulong mul = unchecked(K2 + (ulong)n * 2);
            ulong a = Fetch32(s, p);
            return Hash16Mul(unchecked((ulong)n + (a << 3)), Fetch32(s, p + n - 4), mul);
        }
        if (n > 0)
        {
            byte a = s[p];
            byte b = s[p + (n >> 1)];
            byte c = s[p + n - 1];
            ulong y = unchecked((ulong)a + ((ulong)b << 8));
            ulong z = unchecked((ulong)n + ((ulong)c << 2));
            return unchecked(ShiftMix(y * K2 ^ z * K0) * K2);
        }
        return K2;
    }

    private static ulong Hash17To32(byte[] s, int p, int n)
    {
        ulong mul = unchecked(K2 + (ulong)n * 2);
        ulong a = unchecked(Fetch64(s, p) * K1);
        ulong b = Fetch64(s, p + 8);
        ulong c = unchecked(Fetch64(s, p + n - 8) * mul);
        ulong d = unchecked(Fetch64(s, p + n - 16) * K2);
        return Hash16Mul(
            unchecked(Ror(a + b, 43) + Ror(c, 30) + d),
            unchecked(a + Ror(b + K2, 18) + c),
            mul);
    }

    // CityHash 1.1.0 — matches UE 5.x `Hash/CityHash.cpp::HashLen33to64`.
    // The 1.0.x variant (rotate-only, no bswap) produces different hashes for
    // ≥33-byte strings and was the source of a long-class-name hash mismatch
    // (e.g. TLightMapDensityPSFDummyLightMapPolicy hashed wrong vs cook).
    private static ulong Hash33To64(byte[] s, int p, int n)
    {
        ulong mul = unchecked(K2 + (ulong)n * 2);
        ulong a = unchecked(Fetch64(s, p) * K2);
        ulong b = Fetch64(s, p + 8);
        ulong c = Fetch64(s, p + n - 24);
        ulong d = Fetch64(s, p + n - 32);
        ulong e = unchecked(Fetch64(s, p + 16) * K2);
        ulong f = unchecked(Fetch64(s, p + 24) * 9);
        ulong g = Fetch64(s, p + n - 8);
        ulong h = unchecked(Fetch64(s, p + n - 16) * mul);
        ulong u = unchecked(Ror(a + g, 43) + (Ror(b, 30) + c) * 9);
        ulong v = unchecked(((a + g) ^ d) + f + 1);
        ulong w = unchecked(Bswap64((u + v) * mul) + h);
        ulong x = unchecked(Ror(e + f, 42) + c);
        ulong y = unchecked((Bswap64((v + w) * mul) + g) * mul);
        ulong z = unchecked(e + f + c);
        a = unchecked(Bswap64((x + z) * mul + y) + b);
        b = unchecked(ShiftMix((z + a) * mul + d + h) * mul);
        return unchecked(b + x);
    }

    private static ulong Bswap64(ulong v)
    {
        v = (v << 8 & 0xFF00FF00FF00FF00UL) | (v >> 8 & 0x00FF00FF00FF00FFUL);
        v = (v << 16 & 0xFFFF0000FFFF0000UL) | (v >> 16 & 0x0000FFFF0000FFFFUL);
        return (v << 32) | (v >> 32);
    }

    private static (ulong, ulong) Weak16(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a = unchecked(a + w);
        b = unchecked(Ror(b + a + z, 21));
        ulong c = a;
        a = unchecked(a + x + y);
        b = unchecked(b + Ror(a, 44));
        return (unchecked(a + z), unchecked(b + c));
    }

    private static (ulong, ulong) WeakOffset(byte[] s, int p, ulong a, ulong b)
        => Weak16(Fetch64(s, p), Fetch64(s, p + 8), Fetch64(s, p + 16), Fetch64(s, p + 24), a, b);

    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    private static ulong Ror(ulong x, int n) => (x >> n) | (x << (64 - n));

    private static ulong Fetch64(byte[] s, int p) => BitConverter.ToUInt64(s, p);
    private static ulong Fetch32(byte[] s, int p) => BitConverter.ToUInt32(s, p);
}
