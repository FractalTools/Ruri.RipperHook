using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ruri.FModelHook.ShaderDecompiler.Semantics;

/// <summary>
/// What one texture slot of a material feeds, channel by channel: which of its channels reach
/// the base colour, the normal, the metallic, specular, roughness and occlusion components of
/// the GBuffer, which reach scene colour alone (emissive), and which decide the opacity mask.
/// Each part lists every channel the shader lets reach it; a consumer that needs one channel
/// takes the part only when exactly one does. The slot is named the way the material names it:
/// its parameter, or the referenced texture it samples as a constant.
/// </summary>
public sealed record MaterialSlotSemantics(
    int Group,
    int Index,
    int TextureIndex,
    string ParameterName,
    IReadOnlySet<int> BaseColorChannels,
    IReadOnlySet<int> NormalChannels,
    IReadOnlySet<int> MetallicChannels,
    IReadOnlySet<int> SpecularChannels,
    IReadOnlySet<int> RoughnessChannels,
    IReadOnlySet<int> OcclusionChannels,
    IReadOnlySet<int> EmissiveChannels,
    IReadOnlySet<int> OpacityMaskChannels)
{
    public bool IsBaseColor => BaseColorChannels.Count > 0;

    public bool IsNormal => NormalChannels.Count > 0;

    public bool IsPacked => MetallicChannels.Count > 0 || RoughnessChannels.Count > 0 || OcclusionChannels.Count > 0 || SpecularChannels.Count > 0;

    public bool IsEmissive => EmissiveChannels.Count > 0;

    public bool PlaysAPart => IsBaseColor || IsNormal || IsPacked || IsEmissive || OpacityMaskChannels.Count > 0;

    /// <summary>The one channel a part is read from, or null when no channel or several reach it.</summary>
    public static int? Single(IReadOnlySet<int> channels) => channels.Count == 1 ? channels.First() : null;
}

/// <summary>
/// What one preshader-filled field of the material's uniform buffer feeds, lane by lane: for
/// each of the three colour components of base colour and of emissive, the one lane of the
/// field that reaches it; for metallic, specular, roughness and occlusion, the one lane that
/// reaches that GBuffer component and no other component the GBuffer holds. A colour is a
/// field of three or more lanes feeding the three components one lane each -- a single lane
/// reaching all three is a factor, not a colour. The field's value for any material instance
/// comes from its own preshader program.
/// </summary>
public sealed record MaterialValueSemantics(
    PreshaderField Field,
    IReadOnlyList<int?> BaseColorLanes,
    IReadOnlyList<int?> EmissiveLanes,
    int? MetallicLane,
    int? SpecularLane,
    int? RoughnessLane,
    int? OcclusionLane)
{
    public bool IsBaseColor => IsColor(BaseColorLanes);

    public bool IsEmissive => IsColor(EmissiveLanes);

    public bool PlaysAPart => IsBaseColor || IsEmissive || MetallicLane is not null || SpecularLane is not null || RoughnessLane is not null || OcclusionLane is not null;

    private bool IsColor(IReadOnlyList<int?> lanes) =>
        Field.Rows >= lanes.Count && lanes.All(static lane => lane is not null) && lanes.Distinct().Count() == lanes.Count;
}

/// <summary>
/// The resolved parts of every texture slot and every uniform field a material's compiled base
/// pass reads, or the reason none could be read. The uniform expression set the map was
/// compiled with stays with it, so a field's value can be computed for any instance's
/// parameters.
/// </summary>
public sealed record MaterialSemantics(string ShaderMapHash, string Status, IReadOnlyList<MaterialSlotSemantics> Slots, IReadOnlyList<MaterialValueSemantics> Values, string? ExpressionSet)
{
    public const string Resolved = "resolved";
    private const string NumericParametersName = "UniformNumericParameters";
    private const string ParameterInfoName = "ParameterInfo";
    private const string NameName = "Name";
    private const string ParameterNameName = "ParameterName";
    private const string ParameterTypeName = "ParameterType";
    private const string ScalarType = "Scalar";
    private const string ValueName = "Value";
    private static readonly string[] ComponentNames = ["R", "G", "B", "A"];

    public bool IsResolved => string.Equals(Status, Resolved, StringComparison.Ordinal);

    public static MaterialSemantics Unresolved(string shaderMapHash, string status) =>
        new(shaderMapHash, status, Array.Empty<MaterialSlotSemantics>(), Array.Empty<MaterialValueSemantics>(), null);

    /// <summary>
    /// A field's value for one material: the map's numeric parameters with every one the
    /// material states replaced by the material's own value (a scalar as four lanes, a vector
    /// as its components), run through the field's preshader program.
    /// </summary>
    public float[]? Evaluate(MaterialValueSemantics value, IReadOnlyDictionary<string, float[]> parameters)
    {
        if (ExpressionSet is null)
        {
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(ExpressionSet);
        if (!document.RootElement.TryGetProperty(NumericParametersName, out JsonElement numeric) || numeric.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        if (JsonNode.Parse(numeric.GetRawText()) is not JsonArray patched)
        {
            return null;
        }
        foreach (JsonNode? entry in patched)
        {
            if (entry is not JsonObject parameter || ParameterName(parameter) is not { } name || !parameters.TryGetValue(name, out float[]? stated))
            {
                continue;
            }
            bool scalar = string.Equals(parameter[ParameterTypeName]?.GetValue<string>(), ScalarType, StringComparison.Ordinal);
            parameter[ValueName] = scalar ? JsonValue.Create(stated[0]) : Vector(stated);
        }
        return MaterialConstantBufferReader.Evaluate(document.RootElement, value.Field, JsonSerializer.SerializeToElement(patched));
    }

    private static string? ParameterName(JsonObject parameter)
    {
        if (parameter[ParameterInfoName] is JsonObject info)
        {
            return info[NameName]?.GetValue<string>();
        }
        return parameter[ParameterNameName]?.GetValue<string>() ?? parameter[NameName]?.GetValue<string>();
    }

    private static JsonObject Vector(float[] stated)
    {
        JsonObject vector = new();
        for (int component = 0; component < ComponentNames.Length; component++)
        {
            vector[ComponentNames[component]] = component < stated.Length ? stated[component] : 0f;
        }
        return vector;
    }
}
