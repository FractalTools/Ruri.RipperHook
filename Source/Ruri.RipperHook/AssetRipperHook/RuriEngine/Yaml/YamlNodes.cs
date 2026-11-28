using System.Runtime.CompilerServices;

namespace Ruri.RipperHook.RuriEngine;

internal static class YamlNodes
{
    private const string MatrixElementPrefix = "e";
    private const int MatrixSize = 4;

    public static IReadOnlyList<UnityYamlNode> Items(UnityYamlNode? node, string path)
    {
        switch (node)
        {
            case null:
            case UnityYamlScalar { IsEmpty: true }:
                return [];
            case UnityYamlSequence sequence:
                return sequence.Items;
            default:
                throw new FormatException($"{path}: expected a sequence");
        }
    }

    public static UnityYamlMapping Mapping(UnityYamlNode? node, string path)
    {
        return node as UnityYamlMapping ?? throw new FormatException($"{path}: expected a mapping");
    }

    public static UnityYamlNode Field(UnityYamlMapping mapping, string key, string path)
    {
        return mapping.TryGet(key, out UnityYamlNode? value) ? value : throw new FormatException($"{path}: missing {key}");
    }

    public static UnityYamlNode? Optional(UnityYamlMapping mapping, string key)
    {
        return mapping.TryGet(key, out UnityYamlNode? value) ? value : null;
    }

    public static long FileId(UnityYamlNode? pointer)
    {
        return pointer is UnityYamlMapping mapping && mapping.TryGetText("fileID") is { } text && YamlScalars.TryParseInteger(text, out long id) ? id : 0;
    }

    public static long Integer(UnityYamlNode? node, string path)
    {
        return node is UnityYamlScalar scalar && YamlScalars.TryParseInteger(scalar.Text, out long value)
            ? value
            : throw new FormatException($"{path}: expected an integer");
    }

    public static double Number(UnityYamlNode? node, string path)
    {
        return node is UnityYamlScalar scalar && YamlScalars.TryParseDouble(scalar.Text, out double value)
            ? value
            : throw new FormatException($"{path}: expected a number");
    }

    public static bool Boolean(UnityYamlNode? node, string path)
    {
        return node is UnityYamlScalar scalar && YamlScalars.TryParseBoolean(scalar.Text, out bool value)
            ? value
            : throw new FormatException($"{path}: expected a boolean");
    }

    public static string Text(UnityYamlNode? node, string path)
    {
        return node is UnityYamlScalar scalar ? scalar.Text : throw new FormatException($"{path}: expected text");
    }

    public static byte[] Bytes(UnityYamlNode? node, string path)
    {
        return Packed(node, path, item => (byte)Integer(item, path));
    }

    public static uint[] UnsignedIntegers(UnityYamlNode? node, string path)
    {
        return Packed(node, path, item => (uint)Integer(item, path));
    }

    public static float[] Singles(UnityYamlNode? node, string path)
    {
        return Packed(node, path, item => (float)Number(item, path));
    }

    public static (double X, double Y, double Z) Vector(UnityYamlNode? node, string path)
    {
        UnityYamlMapping mapping = Mapping(node, path);
        return (Number(Field(mapping, "x", path), path + ".x"), Number(Field(mapping, "y", path), path + ".y"), Number(Field(mapping, "z", path), path + ".z"));
    }

    public static (double X, double Y) Vector2(UnityYamlNode? node, string path)
    {
        UnityYamlMapping mapping = Mapping(node, path);
        return (Number(Field(mapping, "x", path), path + ".x"), Number(Field(mapping, "y", path), path + ".y"));
    }

    public static (double R, double G, double B, double A) Color(UnityYamlNode? node, string path)
    {
        UnityYamlMapping mapping = Mapping(node, path);
        return (Number(Field(mapping, "r", path), path + ".r"), Number(Field(mapping, "g", path), path + ".g"), Number(Field(mapping, "b", path), path + ".b"), Number(Field(mapping, "a", path), path + ".a"));
    }

    public static double[] Matrix(UnityYamlNode? node, string path)
    {
        UnityYamlMapping mapping = Mapping(node, path);
        double[] matrix = new double[MatrixSize * MatrixSize];
        for (int row = 0; row < MatrixSize; row++)
        {
            for (int column = 0; column < MatrixSize; column++)
            {
                string key = MatrixElementPrefix + row + column;
                matrix[row * MatrixSize + column] = Number(Field(mapping, key, path), $"{path}.{key}");
            }
        }
        return matrix;
    }

    private static T[] Packed<T>(UnityYamlNode? node, string path, Func<UnityYamlNode, T> element) where T : unmanaged
    {
        switch (node)
        {
            case null:
            case UnityYamlScalar { IsEmpty: true }:
                return [];
            case UnityYamlScalar compact:
                return YamlScalars.TryDecodeCompact(compact.Text, Unsafe.SizeOf<T>(), out byte[] bytes)
                    ? YamlScalars.ToArray<T>(bytes)
                    : throw new FormatException($"{path}: not a packed {typeof(T).Name} array");
            case UnityYamlSequence sequence:
                T[] result = new T[sequence.Items.Count];
                for (int index = 0; index < result.Length; index++)
                {
                    result[index] = element(sequence.Items[index]);
                }
                return result;
            default:
                throw new FormatException($"{path}: expected an array");
        }
    }
}
