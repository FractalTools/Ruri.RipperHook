namespace Ruri.RipperHook.RuriEngine;

internal static class EngineValues
{
    public static ObjectValue Vector2(double x, double y)
    {
        ObjectValue value = new();
        value.Add("X", ScalarValue.FromSingle((float)x));
        value.Add("Y", ScalarValue.FromSingle((float)y));
        return value;
    }

    public static ObjectValue Vector3(double x, double y, double z)
    {
        ObjectValue value = Vector2(x, y);
        value.Add("Z", ScalarValue.FromSingle((float)z));
        return value;
    }

    public static ObjectValue Vector4(double x, double y, double z, double w)
    {
        ObjectValue value = Vector3(x, y, z);
        value.Add("W", ScalarValue.FromSingle((float)w));
        return value;
    }

    public static ArrayValue Integers(IEnumerable<int> values)
    {
        ArrayValue array = new();
        foreach (int value in values)
        {
            array.Items.Add(ScalarValue.FromInteger(value));
        }
        return array;
    }

    public static ArrayValue Singles(IEnumerable<float> values)
    {
        ArrayValue array = new();
        foreach (float value in values)
        {
            array.Items.Add(ScalarValue.FromSingle(value));
        }
        return array;
    }

    public static ArrayValue Texts(IEnumerable<string> values)
    {
        ArrayValue array = new();
        foreach (string value in values)
        {
            array.Items.Add(ScalarValue.FromText(value));
        }
        return array;
    }

    public static ObjectValue Primvar(ArrayValue values, string interpolation, int elementSize = 1)
    {
        ObjectValue primvar = new();
        primvar.Add("Values", values);
        primvar.Add("Interpolation", ScalarValue.FromText(interpolation));
        if (elementSize != 1)
        {
            primvar.Add("ElementSize", ScalarValue.FromInteger(elementSize));
        }
        return primvar;
    }
}
