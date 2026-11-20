using System.Globalization;

namespace Ruri.RipperHook.RuriEngine;

internal static class YamlScalars
{
    private const string HexPrefix = "0x";

    public static bool TryParseInteger(string text, out long value)
    {
        return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseUnsigned(string text, out ulong value)
    {
        return ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseBoolean(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    public static bool TryParseSingle(string text, out float value)
    {
        string trimmed = text.Trim();
        if (TryParseHexBits(trimmed, out ulong bits, out int digits))
        {
            value = digits <= 8 ? BitConverter.UInt32BitsToSingle((uint)bits) : (float)BitConverter.UInt64BitsToDouble(bits);
            return true;
        }
        if (TryParseSpecial(trimmed, out double special))
        {
            value = (float)special;
            return true;
        }
        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseDouble(string text, out double value)
    {
        string trimmed = text.Trim();
        if (TryParseHexBits(trimmed, out ulong bits, out int digits))
        {
            value = digits <= 8 ? BitConverter.UInt32BitsToSingle((uint)bits) : BitConverter.UInt64BitsToDouble(bits);
            return true;
        }
        if (TryParseSpecial(trimmed, out value))
        {
            return true;
        }
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryDecodeCompact(string text, int elementSize, out byte[] bytes)
    {
        bytes = [];
        string data = text.Trim();
        if (data.Length == 0)
        {
            return true;
        }
        if (data.Length % 2 == 1)
        {
            int suffixSize = SuffixSize(data[^1]);
            if (suffixSize == 0 || suffixSize != elementSize)
            {
                return false;
            }
            data = data[..^1];
        }
        if (data.Length / 2 % elementSize != 0)
        {
            return false;
        }
        bytes = new byte[data.Length / 2];
        for (int index = 0; index < bytes.Length; index++)
        {
            int high = HexValue(data[index * 2]);
            int low = HexValue(data[index * 2 + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }
            bytes[index] = (byte)(high << 4 | low);
        }
        return true;
    }

    public static bool IsCompact(string text, int elementSize)
    {
        return TryDecodeCompact(text, elementSize, out _);
    }

    public static T[] ToArray<T>(byte[] bytes) where T : unmanaged
    {
        T[] values = new T[bytes.Length / System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
        if (BitConverter.IsLittleEndian)
        {
            bytes.AsSpan(0, values.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
            return values;
        }
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        for (int index = 0; index < values.Length; index++)
        {
            byte[] element = bytes.AsSpan(index * size, size).ToArray();
            Array.Reverse(element);
            values[index] = System.Runtime.InteropServices.MemoryMarshal.Read<T>(element);
        }
        return values;
    }

    private static int SuffixSize(char suffix)
    {
        return suffix switch
        {
            'n' or 'y' or 'k' or 'o' => 1,
            's' or 't' => 2,
            'i' or 'u' => 4,
            'l' or 'q' => 8,
            _ => 0,
        };
    }

    private static int HexValue(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    private static bool TryParseHexBits(string text, out ulong bits, out int digits)
    {
        bits = 0;
        digits = 0;
        if (!text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int end = text.IndexOf('(');
        string hex = end < 0 ? text[HexPrefix.Length..] : text[HexPrefix.Length..end];
        digits = hex.Length;
        return digits is > 0 and <= 16 && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bits);
    }

    private static bool TryParseSpecial(string text, out double value)
    {
        switch (text.ToLowerInvariant())
        {
            case "inf":
            case "+inf":
            case ".inf":
            case "+.inf":
            case "infinity":
            case "+infinity":
                value = double.PositiveInfinity;
                return true;
            case "-inf":
            case "-.inf":
            case "-infinity":
                value = double.NegativeInfinity;
                return true;
            case "nan":
            case ".nan":
                value = double.NaN;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
