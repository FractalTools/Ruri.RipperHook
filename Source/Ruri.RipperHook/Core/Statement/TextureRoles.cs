using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ruri.RipperHook.Statements;

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

    public static readonly string[] FloatValueRoles =
        ["metallic", "roughness", "smoothness", "normal_strength", "alpha_cutoff", "blend_mode"];

    private readonly Dictionary<string, JsonObject> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _floats = new(StringComparer.Ordinal);

    public string ProvenKeyword { get; private set; } = string.Empty;

    public List<string> Sources { get; } = [];

    /// <summary>The default layer, then every stated layer path that exists, in order.</summary>
    public static TextureRoles Load(IEnumerable<string> layerPaths)
    {
        TextureRoles table = new();
        table.Merge(DefaultLayer());
        table.Sources.Add(DefaultLayerResource);
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

    private static JsonObject DefaultLayer()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultLayerResource)
            ?? throw new InvalidOperationException($"the kernel ships no embedded '{DefaultLayerResource}'.");
        using StreamReader reader = new(stream);
        return ReadLayer(reader.ReadToEnd(), DefaultLayerResource);
    }

    private static JsonObject ReadLayer(string text, string origin) =>
        JsonNode.Parse(text) as JsonObject
        ?? throw new InvalidDataException($"texture role layer is not an object: {origin}");

    private void Merge(JsonObject layer)
    {
        if (layer["textures"] is JsonObject textures)
        {
            foreach ((string name, JsonNode? entry) in textures)
            {
                if (entry is JsonObject stated)
                {
                    _textures[name] = stated;
                }
            }
        }
        if (layer["colors"] is JsonObject colors)
        {
            foreach ((string name, JsonNode? role) in colors)
            {
                _colors[name] = role?.ToString() ?? string.Empty;
            }
        }
        if (layer["floats"] is JsonObject floats)
        {
            foreach ((string name, JsonNode? role) in floats)
            {
                _floats[name] = role?.ToString() ?? string.Empty;
            }
        }
        if (layer["proven_keyword"] is JsonValue keyword && keyword.TryGetValue(out string? value) && value.Length > 0)
        {
            ProvenKeyword = value;
        }
    }

    public sealed record TextureRole(string Name, string Texture, string? Role,
        IReadOnlyDictionary<string, int> Channels, string Encoding);

    public sealed record Resolution(IReadOnlyList<TextureRole> Textures,
        IReadOnlyDictionary<string, float[]> Colors, IReadOnlyDictionary<string, float> Floats,
        IReadOnlyList<string> Unmapped, bool Proven);

    public Resolution Resolve(UnityMaterialProperties properties)
    {
        bool proven = ProvenKeyword.Length > 0 && properties.Keywords.Contains(ProvenKeyword);
        List<TextureRole> textures = [];
        List<string> unmapped = [];
        foreach ((string name, string textureKey) in properties.Textures)
        {
            if (!_textures.TryGetValue(name, out JsonObject? entry))
            {
                if (!proven)
                {
                    unmapped.Add(name);
                }
                continue;
            }
            string? role = entry["role"]?.ToString();
            Dictionary<string, int> channels = new(StringComparer.Ordinal);
            if (entry["channels"] is JsonObject fixedChannels)
            {
                foreach ((string channelRole, JsonNode? index) in fixedChannels)
                {
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
        Dictionary<string, float[]> colors = new(StringComparer.Ordinal);
        foreach ((string name, float[] value) in properties.Colors)
        {
            if (_colors.TryGetValue(name, out string? role) && Array.IndexOf(ColorValueRoles, role) >= 0)
            {
                colors.TryAdd(role, value);
            }
        }
        Dictionary<string, float> floats = new(StringComparer.Ordinal);
        foreach ((string name, float value) in properties.Floats)
        {
            if (_floats.TryGetValue(name, out string? role) && Array.IndexOf(FloatValueRoles, role) >= 0)
            {
                floats.TryAdd(role, value);
            }
        }
        return new Resolution(textures, colors, floats, unmapped, proven);
    }
}
