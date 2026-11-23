namespace Ruri.RipperHook.RuriEngine;

internal static class GraphicsSettingsDecoder
{
    public static ObjectValue? Decode(DecodeContext context)
    {
        ObjectValue settings = new();
        AddPointer(settings, "DefaultRenderPipeline", context.Pointer(context.Take("m_CustomRenderPipeline"), "m_CustomRenderPipeline"));
        AddGlobalSettings(context, settings);
        return settings;
    }

    private static void AddGlobalSettings(DecodeContext context, ObjectValue settings)
    {
        const string source = "m_RenderPipelineGlobalSettingsMap";
        if (context.Take(source) is not { } node)
        {
            return;
        }
        UnityYamlMapping map = YamlNodes.Mapping(node, source);
        if (map.Entries.Count > 1)
        {
            context.Unsupported($"{source} names {map.Entries.Count} render pipelines, the engine has one fixed global settings reference");
            return;
        }
        foreach (KeyValuePair<string, UnityYamlNode> entry in map.Entries)
        {
            AddPointer(settings, "GlobalSettings", context.Pointer(entry.Value, $"{source}.{entry.Key}"));
        }
    }

    private static void AddPointer(ObjectValue settings, string field, Value pointer)
    {
        if (pointer is ReferenceValue { IsNull: false })
        {
            settings.Add(field, pointer);
        }
    }
}
