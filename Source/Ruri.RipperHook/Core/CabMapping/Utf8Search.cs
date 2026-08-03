using System.Numerics;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// Substring search over a UTF-8 blob + ascending offsets -- the shape every column table stores
/// its text as. Case folding is done ONCE per blob, then the scan is a vectorized
/// <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/> partitioned across
/// cores on value boundaries.
///
/// Deliberately typed in buffers, not in any table type: the cabmap's row table and a game's own
/// config table are both "blob + offsets", so they get the same engine instead of one each. This
/// file therefore lives outside the game-hook tree and survives the Pure build.
/// </summary>
public static class Utf8Search
{
    /// <summary>ASCII-lowercase a UTF-8 blob with full-width SIMD: every 'A'..'Z' lane ORs in
    /// 0x20, everything else (including all non-ASCII UTF-8 bytes, whose high bit keeps them out
    /// of the A-Z window) passes through untouched.</summary>
    public static byte[] FoldBlob(byte[] blob, int length)
    {
        byte[] folded = new byte[length];
        int i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            Vector<byte> lowerA = new((byte)('A' - 1));
            Vector<byte> upperZ = new((byte)('Z' + 1));
            Vector<byte> caseBit = new((byte)0x20);
            int lastBlock = length - Vector<byte>.Count;
            for (; i <= lastBlock; i += Vector<byte>.Count)
            {
                Vector<byte> lanes = new(blob.AsSpan(i, Vector<byte>.Count));
                Vector<byte> isUpper = Vector.BitwiseAnd(
                    Vector.GreaterThan(lanes, lowerA),
                    Vector.LessThan(lanes, upperZ));
                Vector<byte> foldedLanes = Vector.BitwiseOr(lanes, Vector.BitwiseAnd(isUpper, caseBit));
                foldedLanes.CopyTo(folded.AsSpan(i));
            }
        }
        for (; i < length; i++)
        {
            byte b = blob[i];
            folded[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
        }
        return folded;
    }

    public static byte[] FoldNeedle(string needle)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(needle);
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] is >= (byte)'A' and <= (byte)'Z')
            {
                utf8[i] |= 0x20;
            }
        }
        return utf8;
    }

    public static string FoldString(string value)
    {
        Span<char> folded = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            folded[i] = c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
        }
        return new string(folded);
    }

    /// <summary>Vectorized sweep of one folded blob for the needle, partitioned across cores on
    /// value boundaries; every hit that does not straddle a value boundary reports its value id.</summary>
    public static void ScanColumn(byte[] foldedBlob, int[] offsets, byte[] needle, bool[] mask,
        Action<bool[], int> onValueHit)
    {
        int valueCount = offsets.Length - 1;
        if (valueCount <= 0 || foldedBlob.Length == 0 || needle.Length > foldedBlob.Length)
        {
            return;
        }
        int partitions = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, valueCount));
        int valuesPerPartition = (valueCount + partitions - 1) / partitions;
        Parallel.For(0, partitions, partition =>
        {
            int firstValue = partition * valuesPerPartition;
            if (firstValue >= valueCount)
            {
                return;
            }
            int lastValue = Math.Min(firstValue + valuesPerPartition, valueCount);
            int begin = offsets[firstValue];
            int end = offsets[lastValue];
            ReadOnlySpan<byte> span = foldedBlob.AsSpan(begin, end - begin);
            int cursor = 0;
            int valueId = firstValue;
            while (true)
            {
                int found = span[cursor..].IndexOf(needle);
                if (found < 0)
                {
                    break;
                }
                int position = begin + cursor + found;
                // Map the hit position to its value id (offsets ascending; hits arrive in
                // ascending position, so advance the cached cursor instead of re-bisecting).
                while (offsets[valueId + 1] <= position)
                {
                    valueId++;
                }
                if (position + needle.Length <= offsets[valueId + 1])
                {
                    onValueHit(mask, valueId);
                    // Whole value already matched: skip straight past it.
                    cursor = offsets[valueId + 1] - begin;
                }
                else
                {
                    cursor = cursor + found + 1; // straddles a boundary -- not a match in either value
                }
                if (cursor >= span.Length)
                {
                    break;
                }
            }
        });
    }
}
