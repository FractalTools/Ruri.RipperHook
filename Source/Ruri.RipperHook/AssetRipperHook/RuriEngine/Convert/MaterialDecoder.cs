namespace Ruri.RipperHook.RuriEngine;

internal static class MaterialDecoder
{
    private static readonly HashSet<string> KnownProperties = new(["serializedVersion", "m_TexEnvs", "m_Ints", "m_Floats", "m_Colors"], StringComparer.Ordinal);

    public static ObjectValue? Decode(DecodeContext context)
    {
        ObjectValue material = new();
        material.Add("SourceAsset", context.Pointer(context.Take("m_Shader"), "m_Shader"));
        if (context.Pointer(context.Take("m_Parent"), "m_Parent") is ReferenceValue { IsNull: false } parent)
        {
            material.Add("BaseMaterial", parent);
        }
        List<string> keywords = new();
        AddTexts(keywords, context.Take("m_ValidKeywords"), "m_ValidKeywords");
        AddTexts(keywords, context.Take("m_InvalidKeywords"), "m_InvalidKeywords");
        if (keywords.Count > 0)
        {
            material.Add("Keywords", EngineValues.Texts(keywords));
        }
        if (YamlNodes.Items(context.Take("m_BuildTextureStacks"), "m_BuildTextureStacks").Count > 0)
        {
            context.Unsupported("virtual texture stacks (m_BuildTextureStacks)");
        }
        List<string> passes = new();
        AddTexts(passes, context.Take("disabledShaderPasses"), "disabledShaderPasses");
        if (passes.Count > 0)
        {
            material.Add("DisabledShaderPasses", EngineValues.Texts(passes));
        }
        ReadProperties(context, material);
        return material;
    }

    private static void ReadProperties(DecodeContext context, ObjectValue material)
    {
        UnityYamlMapping saved = YamlNodes.Mapping(context.Take("m_SavedProperties"), "m_SavedProperties");
        foreach (KeyValuePair<string, UnityYamlNode> entry in saved.Entries)
        {
            if (!KnownProperties.Contains(entry.Key))
            {
                context.Ignored("m_SavedProperties." + entry.Key);
            }
        }
        AddRows(material, "FloatInputs", Named(YamlNodes.Optional(saved, "m_Floats"), "m_Floats"), static (name, value, path) =>
        {
            ObjectValue row = Row(name);
            row.Add("Value", ScalarValue.FromSingle((float)YamlNodes.Number(value, path)));
            return row;
        });
        AddRows(material, "IntInputs", Named(YamlNodes.Optional(saved, "m_Ints"), "m_Ints"), static (name, value, path) =>
        {
            ObjectValue row = Row(name);
            row.Add("Value", ScalarValue.FromInteger(YamlNodes.Integer(value, path)));
            return row;
        });
        AddRows(material, "ColorInputs", Named(YamlNodes.Optional(saved, "m_Colors"), "m_Colors"), static (name, value, path) =>
        {
            (double R, double G, double B, double A) color = YamlNodes.Color(value, path);
            ObjectValue row = Row(name);
            row.Add("Value", EngineValues.Vector4(color.R, color.G, color.B, color.A));
            return row;
        });
        AddRows(material, "TextureInputs", Named(YamlNodes.Optional(saved, "m_TexEnvs"), "m_TexEnvs"), (name, value, path) => TextureRow(context, name, value, path));
    }

    private static ObjectValue? TextureRow(DecodeContext context, string name, UnityYamlNode value, string path)
    {
        UnityYamlMapping environment = YamlNodes.Mapping(value, path);
        Value file = context.Pointer(YamlNodes.Optional(environment, "m_Texture"), path + ".m_Texture");
        (double X, double Y) scale = YamlNodes.Vector2(YamlNodes.Field(environment, "m_Scale", path), path + ".m_Scale");
        (double X, double Y) offset = YamlNodes.Vector2(YamlNodes.Field(environment, "m_Offset", path), path + ".m_Offset");
        bool hasFile = file is ReferenceValue { IsNull: false };
        if (!hasFile && scale == (1.0, 1.0) && offset == (0.0, 0.0))
        {
            return null;
        }
        ObjectValue row = Row(name);
        if (hasFile)
        {
            row.Add("File", file);
        }
        row.Add("Scale", EngineValues.Vector2(scale.X, scale.Y));
        row.Add("Translation", EngineValues.Vector2(offset.X, offset.Y));
        return row;
    }

    private static void AddRows(ObjectValue material, string field, IEnumerable<KeyValuePair<string, UnityYamlNode>> entries, Func<string, UnityYamlNode, string, ObjectValue?> build)
    {
        ArrayValue rows = new();
        foreach (KeyValuePair<string, UnityYamlNode> entry in entries)
        {
            if (build(entry.Key, entry.Value, $"{field}.{entry.Key}") is { } row)
            {
                rows.Items.Add(row);
            }
        }
        if (rows.Items.Count > 0)
        {
            material.Add(field, rows);
        }
    }

    private static ObjectValue Row(string name)
    {
        ObjectValue row = new();
        row.Add("Name", ScalarValue.FromText(name));
        return row;
    }

    private static IEnumerable<KeyValuePair<string, UnityYamlNode>> Named(UnityYamlNode? node, string path)
    {
        foreach (UnityYamlNode item in YamlNodes.Items(node, path))
        {
            UnityYamlMapping entry = YamlNodes.Mapping(item, path);
            if (entry.Entries.Count != 1)
            {
                throw new FormatException($"{path}: expected exactly one property per list entry");
            }
            yield return entry.Entries[0];
        }
    }

    private static void AddTexts(List<string> destination, UnityYamlNode? node, string path)
    {
        foreach (UnityYamlNode item in YamlNodes.Items(node, path))
        {
            string text = YamlNodes.Text(item, path);
            if (!destination.Contains(text))
            {
                destination.Add(text);
            }
        }
    }
}
