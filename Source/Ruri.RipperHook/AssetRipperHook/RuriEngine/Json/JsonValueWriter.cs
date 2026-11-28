using System.Globalization;
using System.Text;

namespace Ruri.RipperHook.RuriEngine;

internal static class JsonValueWriter
{
    private const int InlineFieldLimit = 4;
    private const string Indentation = "  ";

    public static byte[] Write(Value root)
    {
        StringBuilder builder = new();
        Write(builder, root, 0);
        builder.Append('\n');
        return new UTF8Encoding(false).GetBytes(builder.ToString());
    }

    private static void Write(StringBuilder builder, Value value, int depth)
    {
        switch (value)
        {
            case NullValue:
                builder.Append("null");
                break;
            case ScalarValue scalar:
                WriteScalar(builder, scalar);
                break;
            case ReferenceValue reference:
                WriteReference(builder, reference);
                break;
            case ArrayValue array:
                WriteArray(builder, array, depth);
                break;
            case ObjectValue entity:
                WriteObject(builder, entity, depth);
                break;
            default:
                throw new InvalidOperationException($"{value.GetType().Name} has no JSON form.");
        }
    }

    private static void WriteObject(StringBuilder builder, ObjectValue value, int depth)
    {
        if (value.Count == 0)
        {
            builder.Append("{}");
            return;
        }
        if (IsInlineObject(value))
        {
            builder.Append('{');
            for (int index = 0; index < value.Fields.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }
                WriteString(builder, value.Fields[index].Key);
                builder.Append(": ");
                Write(builder, value.Fields[index].Value, depth);
            }
            builder.Append('}');
            return;
        }
        builder.Append("{\n");
        for (int index = 0; index < value.Fields.Count; index++)
        {
            Indent(builder, depth + 1);
            WriteString(builder, value.Fields[index].Key);
            builder.Append(": ");
            Write(builder, value.Fields[index].Value, depth + 1);
            builder.Append(index + 1 < value.Fields.Count ? ",\n" : "\n");
        }
        Indent(builder, depth);
        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, ArrayValue value, int depth)
    {
        if (value.Items.Count == 0)
        {
            builder.Append("[]");
            return;
        }
        if (value.Items.TrueForAll(IsLeaf))
        {
            builder.Append('[');
            for (int index = 0; index < value.Items.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }
                Write(builder, value.Items[index], depth);
            }
            builder.Append(']');
            return;
        }
        builder.Append("[\n");
        for (int index = 0; index < value.Items.Count; index++)
        {
            Indent(builder, depth + 1);
            Write(builder, value.Items[index], depth + 1);
            builder.Append(index + 1 < value.Items.Count ? ",\n" : "\n");
        }
        Indent(builder, depth);
        builder.Append(']');
    }

    private static bool IsInlineObject(ObjectValue value)
    {
        if (value.Count > InlineFieldLimit)
        {
            return false;
        }
        foreach (KeyValuePair<string, Value> field in value.Fields)
        {
            if (!IsLeaf(field.Value) && field.Value is not ReferenceValue)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLeaf(Value value) => value is NullValue or ScalarValue;

    private static void WriteReference(StringBuilder builder, ReferenceValue value)
    {
        if (value.IsNull)
        {
            builder.Append("null");
            return;
        }
        builder.Append('"').Append(value.Target.ToString("N", CultureInfo.InvariantCulture)).Append('"');
    }

    private static void WriteScalar(StringBuilder builder, ScalarValue value)
    {
        switch (value.Kind)
        {
            case ScalarKind.Boolean:
                builder.Append(value.AsBoolean ? "true" : "false");
                break;
            case ScalarKind.Integer:
                builder.Append(value.Signed.ToString(CultureInfo.InvariantCulture));
                break;
            case ScalarKind.Unsigned:
                builder.Append(value.Unsigned.ToString(CultureInfo.InvariantCulture));
                break;
            case ScalarKind.Single:
                WriteFloating(builder, (float)value.Floating, value.Floating);
                break;
            case ScalarKind.Double:
                WriteFloating(builder, value.Floating, value.Floating);
                break;
            case ScalarKind.Text:
                WriteString(builder, value.Text);
                break;
            case ScalarKind.Bytes:
                builder.Append('[');
                for (int index = 0; index < value.Bytes.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }
                    builder.Append(value.Bytes[index].ToString(CultureInfo.InvariantCulture));
                }
                builder.Append(']');
                break;
        }
    }

    private static void WriteFloating(StringBuilder builder, IFormattable text, double value)
    {
        if (double.IsNaN(value))
        {
            builder.Append("\"NaN\"");
        }
        else if (double.IsPositiveInfinity(value))
        {
            builder.Append("\"Infinity\"");
        }
        else if (double.IsNegativeInfinity(value))
        {
            builder.Append("\"-Infinity\"");
        }
        else
        {
            builder.Append(text.ToString(null, CultureInfo.InvariantCulture));
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    private static void Indent(StringBuilder builder, int depth)
    {
        for (int level = 0; level < depth; level++)
        {
            builder.Append(Indentation);
        }
    }
}
