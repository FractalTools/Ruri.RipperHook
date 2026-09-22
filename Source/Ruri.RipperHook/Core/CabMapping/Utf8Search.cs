using System.Numerics;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

public static class Utf8Search
{
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
                while (offsets[valueId + 1] <= position)
                {
                    valueId++;
                }
                if (position + needle.Length <= offsets[valueId + 1])
                {
                    onValueHit(mask, valueId);
                    cursor = offsets[valueId + 1] - begin;
                }
                else
                {
                    cursor = cursor + found + 1;                }
                if (cursor >= span.Length)
                {
                    break;
                }
            }
        });
    }
}
