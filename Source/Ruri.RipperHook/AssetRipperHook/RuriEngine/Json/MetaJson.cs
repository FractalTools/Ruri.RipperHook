using System.Text.RegularExpressions;
using AssetRipper.Yaml;

namespace Ruri.RipperHook.RuriEngine;

internal static partial class MetaJson
{
    public static byte[] Write(UnityYamlNode root, RuriEngineMapping mapping)
    {
        return JsonValueWriter.Write(Convert(root, mapping, string.Empty));
    }

    public static UnityYamlNode FromAssetRipper(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                UnityYamlMapping mapping = new();
                foreach (KeyValuePair<YamlNode, YamlNode> pair in map.Children)
                {
                    mapping.Add(((YamlScalarNode)pair.Key).Value, FromAssetRipper(pair.Value));
                }
                return mapping;
            case YamlSequenceNode sequence:
                UnityYamlSequence list = new();
                foreach (YamlNode child in sequence.Children)
                {
                    list.Items.Add(FromAssetRipper(child));
                }
                return list;
            case YamlScalarNode scalar:
                return new UnityYamlScalar(scalar.Value, scalar.Style != ScalarStyle.Plain);
            default:
                throw new NotSupportedException($"{node.GetType().Name} has no meta JSON form.");
        }
    }

    private static Value Convert(UnityYamlNode node, RuriEngineMapping mapping, string key)
    {
        switch (node)
        {
            case UnityYamlMapping map:
                ObjectValue obj = new();
                foreach (KeyValuePair<string, UnityYamlNode> entry in map.Entries)
                {
                    obj.Add(entry.Key, Convert(entry.Value, mapping, entry.Key));
                }
                return obj;
            case UnityYamlSequence sequence:
                ArrayValue array = new();
                foreach (UnityYamlNode item in sequence.Items)
                {
                    array.Items.Add(Convert(item, mapping, key));
                }
                return array;
            case UnityYamlScalar scalar:
                return Scalar(scalar, mapping, key);
            default:
                throw new NotSupportedException($"{node.GetType().Name} has no meta JSON form.");
        }
    }

    private static Value Scalar(UnityYamlScalar scalar, RuriEngineMapping mapping, string key)
    {
        string text = scalar.Text;
        if (scalar.Quoted || mapping.IsMetaTextKey(key))
        {
            return ScalarValue.FromText(text);
        }
        if (IntegerPattern().IsMatch(text))
        {
            if (YamlScalars.TryParseInteger(text, out long signed))
            {
                return ScalarValue.FromInteger(signed);
            }
            if (YamlScalars.TryParseUnsigned(text, out ulong unsigned))
            {
                return ScalarValue.FromUnsigned(unsigned);
            }
        }
        else if (FloatPattern().IsMatch(text) && YamlScalars.TryParseDouble(text, out double floating))
        {
            return ScalarValue.FromDouble(floating);
        }
        return ScalarValue.FromText(text);
    }

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^-?(\d+\.\d+([eE][+-]?\d+)?|\d+[eE][+-]?\d+)$")]
    private static partial Regex FloatPattern();
}
