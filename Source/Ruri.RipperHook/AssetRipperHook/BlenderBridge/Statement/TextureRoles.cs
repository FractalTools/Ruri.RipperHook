using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// Which material property is which surface input, as data in layers: the default layer
/// embedded here (Unity's standard-shader vocabulary plus what the converters write when
/// they read a material's roles off its compiled shader), a title's own layer, and the
/// layer a host writes from the choices its user makes. Later layers override earlier ones
/// property by property. A texture property no layer names is UNMAPPED and reported, never
/// guessed at -- unless the material carries the proven keyword, which says a converter
/// already read every role off the shader and what is left unnamed is unused.
/// </summary>
public sealed class TextureRoles
{
    public const string DefaultLayerResource = "TextureRoles.json";

    public static readonly string[] ColorRoles = ["base_color", "normal", "emission"];

    public static readonly string[] ChannelRoles =
        ["metallic", "roughness", "smoothness", "occlusion", "specular", "opacity", "height"];

    public const string NoneRole = "none";

    public static readonly string[] ColorValueRoles = ["base_color", "emission"];

    /// <summary>How a material composites is stated in one of two idioms: the built-in pipeline's one blend
    /// mode, or the scriptable pipelines' surface type and alpha clip -- beside a cutoff either way.</summary>
    public static readonly string[] FloatValueRoles =
        ["metallic", "roughness", "smoothness", "normal_strength", "alpha_cutoff", "blend_mode", "surface_type",
         "alpha_clip"];

    private readonly JsonObject _rules = new();
    private readonly Dictionary<string, JsonObject> _profiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _colorValueRoles = new(ColorValueRoles, StringComparer.Ordinal);
    private readonly HashSet<string> _floatValueRoles = new(FloatValueRoles, StringComparer.Ordinal);

    public string ProvenKeyword { get; private set; } = string.Empty;

    public List<string> Sources { get; } = [];

    /// <summary>The default layer, then every stated layer path that exists, in order.</summary>
    public static TextureRoles Load(IEnumerable<string> layerPaths, string? embeddedLayer = null)
    {
        TextureRoles table = new();
        table.Merge(ResourceLayer(DefaultLayerResource));
        table.Sources.Add(DefaultLayerResource);
        if (embeddedLayer is not null)
        {
            table.Merge(ResourceLayer(embeddedLayer));
            table.Sources.Add(embeddedLayer);
        }
        foreach (string path in layerPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }
            table.Merge(ReadLayer(File.ReadAllText(path), path));
            table.Sources.Add(path);
        }
        return table;
    }

    private static JsonObject ResourceLayer(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"the reader ships no embedded '{name}'.");
        using StreamReader reader = new(stream);
        return ReadLayer(reader.ReadToEnd(), name);
    }

    private static JsonObject ReadLayer(string text, string origin) =>
        JsonNode.Parse(text) as JsonObject
        ?? throw new InvalidDataException($"texture role layer is not an object: {origin}");

    private void Merge(JsonObject layer)
    {
        AddRoles(layer["ColorValueRoles"], _colorValueRoles);
        AddRoles(layer["FloatValueRoles"], _floatValueRoles);
        if (layer["shader_profiles"] is JsonObject profiles)
        {
            foreach ((string name, JsonNode? entry) in profiles)
            {
                if (entry is JsonObject profile)
                {
                    _profiles[name] = (JsonObject)profile.DeepClone();
                }
            }
        }
        foreach (string section in new[] { "textures", "colors", "floats" })
        {
            if (layer[section] is not JsonObject entries)
            {
                continue;
            }
            JsonObject target = _rules[section] as JsonObject ?? new JsonObject();
            _rules[section] = target;
            foreach ((string name, JsonNode? value) in entries)
            {
                if (section == "textures" && value is not JsonObject)
                {
                    continue;
                }
                target[name] = value?.DeepClone();
                foreach (JsonObject profile in _profiles.Values)
                {
                    if (profile[section] is JsonObject selected)
                    {
                        selected[name] = value?.DeepClone();
                    }
                }
            }
        }
        if (layer["proven_keyword"] is JsonValue keyword && keyword.TryGetValue(out string? keywordValue) && keywordValue.Length > 0)
        {
            ProvenKeyword = keywordValue;
        }
    }

    private static void AddRoles(JsonNode? node, HashSet<string> roles)
    {
        if (node is JsonArray entries)
        {
            foreach (JsonNode? entry in entries)
            {
                roles.Add(entry!.GetValue<string>());
            }
        }
    }

    public sealed record TextureRole(string Name, string Texture, string? Role,
        IReadOnlyDictionary<string, int> Channels, string Encoding);

    public sealed record Resolution(IReadOnlyList<TextureRole> Textures,
        IReadOnlyDictionary<string, float[]> Colors, IReadOnlyDictionary<string, float> Floats,
        IReadOnlyList<string> Unmapped, bool Proven);

    public Resolution Resolve(UnityMaterialProperties properties)
    {
        _profiles.TryGetValue(properties.ShaderName, out JsonObject? profile);
        JsonObject Rules(string section) => profile?[section] as JsonObject
            ?? _rules[section] as JsonObject ?? new JsonObject();
        JsonObject textureRules = Rules("textures");
        bool proven = ProvenKeyword.Length > 0 && properties.Keywords.Contains(ProvenKeyword);
        List<TextureRole> textures = [];
        List<string> unmapped = [];
        foreach ((string name, string textureKey) in properties.Textures)
        {
            if (textureRules[name] is not JsonObject entry)
            {
                bool unused = _rules["textures"]?[name]?["role"]?.ToString() == NoneRole;
                if (!proven && !unused)
                {
                    unmapped.Add(name);
                }
                continue;
            }
            if (!Allowed(entry, properties))
            {
                continue;
            }
            string? role = entry["role"]?.ToString();
            Dictionary<string, int> channels = new(StringComparer.Ordinal);
            if (entry["channels"] is JsonObject fixedChannels)
            {
                foreach ((string channelRole, JsonNode? node) in fixedChannels)
                {
                    JsonNode? index = node is JsonObject conditional
                        ? Allowed(conditional, properties) ? conditional["channel"] : null : node;
                    if (Array.IndexOf(ChannelRoles, channelRole) >= 0 && index is JsonValue value
                        && value.TryGetValue(out int channel))
                    {
                        channels[channelRole] = channel;
                    }
                }
            }
            if (entry["channels_from"] is JsonObject statedChannels)
            {
                foreach ((string channelRole, JsonNode? floatName) in statedChannels)
                {
                    if (Array.IndexOf(ChannelRoles, channelRole) >= 0
                        && properties.Floats.TryGetValue(floatName?.ToString() ?? string.Empty, out float value))
                    {
                        channels[channelRole] = (int)value;
                    }
                }
            }
            if (role == NoneRole || (role is not null && Array.IndexOf(ColorRoles, role) < 0))
            {
                role = null;
            }
            if (role is null && channels.Count == 0)
            {
                continue;
            }
            textures.Add(new TextureRole(name, textureKey, role, channels, entry["encoding"]?.ToString() ?? string.Empty));
        }
        Dictionary<string, float[]> colors = Map(properties.Colors, Rules("colors"), _colorValueRoles, properties);
        Dictionary<string, float> floats = Map(properties.Floats, Rules("floats"), _floatValueRoles, properties);
        ApplyValues(profile?["constants"], properties, colors, floats, replace: true);
        ApplyValues(profile?["Defaults"], properties, colors, floats, replace: false);
        if (profile?["TextureTransforms"] is JsonObject transforms)
        {
            string prefix = transforms["RolePrefix"]!.GetValue<string>();
            string source = transforms["Source"]!.GetValue<string>();
            string namePrefix = transforms["NamePrefix"]?.GetValue<string>() ?? string.Empty;
            IEnumerable<string> names = source.Length == 0
                ? transforms["Properties"]!.AsObject().Select(pair => pair.Key)
                : properties.Textures.Select(pair => pair.Key);
            foreach (string name in names)
            {
                if (!name.StartsWith(namePrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string selected = transforms["Properties"]?[name]?.GetValue<string>() ?? source;
                if (selected.Length == 0)
                {
                    continue;
                }
                colors[prefix + name] = properties.TextureScaleOffset.TryGetValue(selected, out float[]? value)
                    ? value.ToArray() : transforms["Default"]!.AsArray().Select(value => value!.GetValue<float>()).ToArray();
            }
        }
        return new Resolution(textures, colors, floats, unmapped, proven);
    }

    private static Dictionary<string, T> Map<T>(IReadOnlyDictionary<string, T> values, JsonObject rules,
        HashSet<string> allowed, UnityMaterialProperties properties)
    {
        Dictionary<string, T> result = new(StringComparer.Ordinal);
        foreach ((string name, T value) in values)
        {
            JsonNode? mapping = rules[name];
            string? role = mapping is JsonObject rule
                ? Allowed(rule, properties) ? rule["role"]?.ToString() : null : mapping?.ToString();
            if (role is not null && allowed.Contains(role))
            {
                result.TryAdd(role, value);
            }
        }
        return result;
    }

    private void ApplyValues(JsonNode? node, UnityMaterialProperties properties,
        Dictionary<string, float[]> colors, Dictionary<string, float> floats, bool replace)
    {
        if (node is not JsonObject values)
        {
            return;
        }
        foreach ((string role, JsonNode? entry) in values)
        {
            JsonNode? value = entry;
            if (entry is JsonObject rule)
            {
                if (!Allowed(rule, properties))
                {
                    continue;
                }
                value = rule["value"] ?? rule["Default"];
                if (rule["value_from"] is JsonValue source
                    && properties.Floats.TryGetValue(source.ToString(), out float scalar))
                {
                    value = JsonValue.Create(scalar);
                }
                if (_colorValueRoles.Contains(role) && rule["ColorFrom"] is JsonValue colorSource
                    && properties.Colors.TryGetValue(colorSource.ToString(), out float[]? vector))
                {
                    if (replace) colors[role] = vector.ToArray();
                    else colors.TryAdd(role, vector.ToArray());
                    continue;
                }
            }
            if (_floatValueRoles.Contains(role) && value is JsonValue number && number.TryGetValue(out float scalarValue))
            {
                if (replace) floats[role] = scalarValue;
                else floats.TryAdd(role, scalarValue);
            }
            else if (_colorValueRoles.Contains(role) && value is JsonArray vectorValue)
            {
                float[] vector = vectorValue.Select(item => item!.GetValue<float>()).ToArray();
                if (replace) colors[role] = vector;
                else colors.TryAdd(role, vector);
            }
        }
    }

    private static bool Allowed(JsonObject rule, UnityMaterialProperties properties) =>
        (rule["if_keyword"] is null || properties.Keywords.Contains(rule["if_keyword"]!.ToString()))
        && (rule["if_nonzero"] is null || properties.Floats.GetValueOrDefault(rule["if_nonzero"]!.ToString()) != 0)
        && (rule["unless_keyword"] is null || !properties.Keywords.Contains(rule["unless_keyword"]!.ToString()))
        && (rule["unless_texture"] is null || !properties.Textures.Any(pair => pair.Key == rule["unless_texture"]!.ToString()));
}
