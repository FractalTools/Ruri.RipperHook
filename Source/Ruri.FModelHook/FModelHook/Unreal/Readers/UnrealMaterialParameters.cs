using AssetRipper.Import.Logging;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Material.Parameters;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.FModelHook.ShaderDecompiler.Semantics;
using CUE4Parse.UE4.Versions;
using Ruri.RipperHook.BlenderBridge.Conversion;
using System.Numerics;

namespace Ruri.FModelHook.Unreal.Readers;

/// <summary>
/// The parameter set a material interface resolves to, read the way the engine resolves it:
/// the base material's cached parameter tables state every parameter and its default, each
/// instance down the parent chain overrides by name, and an instance's base-property
/// overrides apply only where it flags them. Textures the graph samples without a parameter
/// are constants of the shader: a material with no texture parameter at all exposes its one
/// colour constant and its one normal-map constant under Unity's base-colour and normal-map
/// property names -- the kind is the texture's own declaration, colour space and compression
/// -- and every other constant under the texture's own name. Parameter kinds follow the
/// engine's declared order for its version; connected material inputs are named by the
/// game's own reflection enum.
/// </summary>
internal sealed class UnrealMaterialParameters
{
    private const string ParametersName = "Parameters";
    private const string RuntimeEntriesName = "RuntimeEntries";
    private const string ParameterInfoSetName = "ParameterInfoSet";
    private const string ParameterInfoName = "ParameterInfo";
    private const string ParameterValueName = "ParameterValue";
    private const string ConnectedMaskName = "PropertyConnectedMask";
    private const string ReferencedTexturesName = "ReferencedTextures";
    private const string EmissionMapName = "_EmissionMap";
    private const string PackedMapName = "_PackedMap";
    private const string PackedMetallicName = "_PackedMapMetallic";
    private const string PackedRoughnessName = "_PackedMapRoughness";
    private const string PackedOcclusionName = "_PackedMapOcclusion";
    private const string PackedSpecularName = "_PackedMapSpecular";
    private const string ModeName = "_Mode";
    private const string CutoffName = "_Cutoff";
    private const string BaseColorName = "_Color";
    private const string EmissionColorName = "_EmissionColor";
    private const string MetallicName = "_Metallic";
    private const string GlossinessName = "_Glossiness";
    private const string RolesKeyword = "RURI_TEXTURE_ROLES_FROM_SHADER";
    private const string BaseColorProperty = "MP_BaseColor";
    private const string EmissiveColorProperty = "MP_EmissiveColor";
    private const string MetallicProperty = "MP_Metallic";
    private const string RoughnessProperty = "MP_Roughness";
    private const float ModeOpaque = 0f;
    private const float ModeCutout = 1f;
    private const float ModeFade = 2f;
    private const string MainTextureName = "_MainTex";
    private const string NormalMapName = "_BumpMap";
    private const string MaterialPropertyEnumName = "EMaterialProperty";
    private const string OverridePrefix = "bOverride_";
    private const string EnumScope = "::";
    private const string ValuesSuffix = "Values";

    /// <summary>
    /// The runtime parameter kinds in the order the engine indexes its cached entries by, per
    /// engine version. EMaterialParameterType is a plain C++ enum with no reflection, and the
    /// cached data does not declare its value tables in that order (5.5 declares StaticSwitchValues
    /// second), so the order is stated per version from Epic's own API reference for that version
    /// and verified against every material's own tables -- the entry count and each kind's value
    /// count -- before a value is read. 5.1 is the six kinds whose value tables the 5.1 schema
    /// declares beside a six-entry RuntimeEntries, in the order those six keep in 5.4, and every
    /// material of a 5.1 build validated against it.
    /// </summary>
    private static readonly IReadOnlyDictionary<EGame, string[]> KindLayouts = new Dictionary<EGame, string[]>
    {
        [EGame.GAME_UE5_1] = ["Scalar", "Vector", "DoubleVector", "Texture", "Font", "RuntimeVirtualTexture"],
        [EGame.GAME_UE5_4] = ["Scalar", "Vector", "DoubleVector", "Texture", "Font", "RuntimeVirtualTexture", "SparseVolumeTexture", "StaticSwitch"],
        [EGame.GAME_UE5_5] = ["Scalar", "Vector", "DoubleVector", "Texture", "TextureCollection", "Font", "RuntimeVirtualTexture", "SparseVolumeTexture", "StaticSwitch"],
    };

    private readonly UnrealFileProvider provider;
    private readonly TypeMappings? mappings;
    // The texture of a parameter is held as its OBJECT PATH, which is what the engine calls it
    // and what every consumer keys by -- the Unity asset table, and a host's own texture source.
    private readonly OrderedDictionary<string, string?> textures = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, float> floats = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, Vector4> colors = new(StringComparer.Ordinal);
    private readonly List<string> keywords = new();
    private readonly HashSet<string> parameterDefaults = new(StringComparer.Ordinal);
    private readonly List<string> referencedNames = new();

    public UnrealMaterialParameters(UnrealFileProvider provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        mappings = provider.MappingsForGame;
    }

    public IEnumerable<string> TextureNames => textures.Keys;

    public IEnumerable<string> FloatNames => floats.Keys;

    public IEnumerable<string> ColorNames => colors.Keys;

    /// <summary>Every texture parameter beside the object path of the texture it names.</summary>
    public IEnumerable<KeyValuePair<string, string?>> Textures => textures;

    /// <summary>Every scalar parameter beside its resolved value.</summary>
    public IEnumerable<KeyValuePair<string, float>> Floats => floats;

    /// <summary>Every vector parameter beside its resolved value.</summary>
    public IEnumerable<KeyValuePair<string, Vector4>> Colors => colors;

    /// <summary>The inputs the base material's graph connects, plus what the semantics pass stated.</summary>
    public IReadOnlyList<string> Keywords => keywords;

    public void Float(string name, float value) => floats[name] = value;

    /// <summary>The base material: its own surface settings, every cached parameter with its default, and the inputs its graph connects.</summary>
    public void ReadRoot(UMaterial root)
    {
        floats[UnrealMaterial.BlendModeName] = (float)root.BlendMode;
        floats[UnrealMaterial.ShadingModelName] = (float)root.ShadingModel;
        floats[UnrealMaterial.TwoSidedName] = root.TwoSided ? 1f : 0f;
        floats[UnrealMaterial.OpacityMaskClipValueName] = root.OpacityMaskClipValue;
        if (root.CachedExpressionData is not { } cached)
        {
            return;
        }
        ReadCached(cached.GetOrDefault<FStructFallback>(ParametersName) ?? cached, root.GetPathName());
        Constants(cached.GetOrDefault<FPackageIndex[]>(ReferencedTexturesName, []));
        ulong connected = cached.GetOrDefault<ulong>(ConnectedMaskName);
        if (connected != 0 && mappings?.Enums.GetValueOrDefault(MaterialPropertyEnumName) is { } properties)
        {
            for (int bit = 0; bit < 64; bit++)
            {
                if ((connected & (1UL << bit)) != 0 && properties.TryGetValue(bit, out string? property))
                {
                    keywords.Add(property);
                }
            }
        }
    }

    /// <summary>An instance: the parameter values it overrides by name, its static switches, and the base properties it flags as overridden.</summary>
    public void ReadInstance(UMaterialInstance instance)
    {
        foreach (FTextureParameterValue value in instance.GetOrDefault<FTextureParameterValue[]>("TextureParameterValues", []))
        {
            textures[value.Name] = value.ParameterValue is { IsNull: false } pointer
                ? pointer.ResolvedObject?.GetPathName()
                : null;
        }
        foreach (FScalarParameterValue value in instance.GetOrDefault<FScalarParameterValue[]>("ScalarParameterValues", []))
        {
            floats[value.Name] = value.ParameterValue;
        }
        foreach (FVectorParameterValue value in instance.GetOrDefault<FVectorParameterValue[]>("VectorParameterValues", []))
        {
            if (value.ParameterValue is { } color)
            {
                colors[value.Name] = new Vector4(color.R, color.G, color.B, color.A);
            }
        }
        foreach (FStructFallback value in instance.GetOrDefault<FStructFallback[]>("DoubleVectorParameterValues", []))
        {
            if (value.GetOrDefault<FMaterialParameterInfo>(ParameterInfoName) is { } info)
            {
                colors[info.Name.Text] = Vector(value.GetOrDefault<TIntVector4<double>>(ParameterValueName));
            }
        }
        if (instance.StaticParameters is { } statics)
        {
            foreach (FStaticSwitchParameter parameter in statics.StaticSwitchParameters)
            {
                floats[parameter.Name] = parameter.Value ? 1f : 0f;
            }
        }
        if (instance.GetOrDefault<FStructFallback>("BasePropertyOverrides") is { } overrides)
        {
            ReadOverrides(overrides);
        }
    }

    /// <summary>
    /// The parts the compiled base pass gave each slot, stated on the material in Unity's
    /// vocabulary beside the slot's own name: the base colour, normal map and emission slots
    /// under the Standard shader's property names, and the packed masks slot under a declared
    /// map whose channel of metallic, roughness, occlusion and specular each rides as a float.
    /// Where several slots feed one part, the slot feeding it with the most channels wins.
    /// </summary>
    public void Apply(MaterialSemantics semantics)
    {
        MaterialSlotSemantics? baseColor = null;
        MaterialSlotSemantics? normal = null;
        MaterialSlotSemantics? emissive = null;
        MaterialSlotSemantics? packed = null;
        foreach (MaterialSlotSemantics slot in semantics.Slots)
        {
            if (SlotKey(slot) is null)
            {
                continue;
            }
            if (slot.IsBaseColor && (baseColor is null || slot.BaseColorChannels.Count > baseColor.BaseColorChannels.Count))
            {
                baseColor = slot;
            }
            if (slot.IsNormal && (normal is null || slot.NormalChannels.Count > normal.NormalChannels.Count))
            {
                normal = slot;
            }
            if (slot.IsEmissive && (emissive is null || slot.EmissiveChannels.Count > emissive.EmissiveChannels.Count))
            {
                emissive = slot;
            }
            if (slot.IsPacked && (packed is null || PackedParts(slot) > PackedParts(packed)))
            {
                packed = slot;
            }
        }
        Alias(baseColor, MainTextureName);
        Alias(normal, NormalMapName);
        if (keywords.Contains(EmissiveColorProperty))
        {
            Alias(emissive, EmissionMapName);
        }
        if (Alias(packed, PackedMapName) && packed is not null)
        {
            Channel(PackedMetallicName, packed.MetallicChannels);
            Channel(PackedRoughnessName, packed.RoughnessChannels);
            Channel(PackedOcclusionName, packed.OcclusionChannels);
            Channel(PackedSpecularName, packed.SpecularChannels);
        }
        ApplyValues(semantics);
        keywords.Add(RolesKeyword);
    }

    /// <summary>
    /// The parts the material's constant buffer feeds, each field computed for this material's
    /// own parameter values, stated only for the inputs the graph connects: a base colour or
    /// emissive field becomes the colour Unity's standard shader multiplies the map by, a
    /// metallic or roughness field the scalar it uses without a map; roughness is stated as
    /// Unity's smoothness. A field reading a parameter the material does not declare is the
    /// engine's own (the editor's selection colour) and states nothing. Where several fields
    /// feed one part, the part is stated only when they all compute the same value: which of
    /// two blended constants shows is decided per pixel by the shader, and nothing here says
    /// which.
    /// </summary>
    private void ApplyValues(MaterialSemantics semantics)
    {
        Dictionary<string, float[]> stated = new(StringComparer.Ordinal);
        foreach ((string floatName, float value) in floats)
        {
            stated[floatName] = [value, value, value, value];
        }
        foreach ((string colorName, Vector4 color) in colors)
        {
            stated[colorName] = [color.X, color.Y, color.Z, color.W];
        }
        List<Vector4> baseColors = new();
        List<Vector4> emissives = new();
        List<float> metallics = new();
        List<float> glossinesses = new();
        foreach (MaterialValueSemantics value in semantics.Values)
        {
            if (!value.Field.Parameters.All(stated.ContainsKey) || semantics.Evaluate(value, stated) is not { } evaluated)
            {
                continue;
            }
            if (value.IsBaseColor)
            {
                baseColors.Add(Color(evaluated, value.BaseColorLanes));
            }
            if (value.IsEmissive)
            {
                emissives.Add(Color(evaluated, value.EmissiveLanes));
            }
            if (value.MetallicLane is { } metallic && metallic < evaluated.Length)
            {
                metallics.Add(evaluated[metallic]);
            }
            if (value.RoughnessLane is { } roughness && roughness < evaluated.Length)
            {
                glossinesses.Add(1f - evaluated[roughness]);
            }
        }
        if (keywords.Contains(BaseColorProperty) && Agreed(baseColors) is { } baseColor)
        {
            colors[BaseColorName] = baseColor;
        }
        if (keywords.Contains(EmissiveColorProperty) && Agreed(emissives) is { } emissive)
        {
            colors[EmissionColorName] = emissive;
        }
        if (keywords.Contains(MetallicProperty) && Agreed(metallics) is { } metal)
        {
            floats[MetallicName] = metal;
        }
        if (keywords.Contains(RoughnessProperty) && Agreed(glossinesses) is { } glossiness)
        {
            floats[GlossinessName] = glossiness;
        }
    }

    /// <summary>The value every field computed, or null when there is none or they differ.</summary>
    private static T? Agreed<T>(List<T> values) where T : struct, IEquatable<T> =>
        values.Count > 0 && values.All(value => value.Equals(values[0])) ? values[0] : null;

    private static Vector4 Color(float[] evaluated, IReadOnlyList<int?> lanes) =>
        new(Lane(evaluated, lanes[0]), Lane(evaluated, lanes[1]), Lane(evaluated, lanes[2]), 1f);

    private static float Lane(float[] evaluated, int? lane) => lane is { } index && index < evaluated.Length ? evaluated[index] : 0f;

    private static int PackedParts(MaterialSlotSemantics slot) =>
        (slot.MetallicChannels.Count > 0 ? 1 : 0) + (slot.RoughnessChannels.Count > 0 ? 1 : 0) + (slot.OcclusionChannels.Count > 0 ? 1 : 0) + (slot.SpecularChannels.Count > 0 ? 1 : 0);

    /// <summary>The material's own key for a slot: its parameter name, else the referenced texture it samples as a constant.</summary>
    private string? SlotKey(MaterialSlotSemantics slot)
    {
        string key = slot.ParameterName.Length > 0
            ? slot.ParameterName
            : slot.TextureIndex >= 0 && slot.TextureIndex < referencedNames.Count ? referencedNames[slot.TextureIndex] : string.Empty;
        return key.Length > 0 && textures.ContainsKey(key) ? key : null;
    }

    private bool Alias(MaterialSlotSemantics? slot, string property)
    {
        if (slot is null || SlotKey(slot) is not { } key)
        {
            return false;
        }
        textures[property] = textures[key];
        return true;
    }

    /// <summary>A part's channel float is stated only when the shader reads the part from exactly one channel.</summary>
    private void Channel(string property, IReadOnlySet<int> channels)
    {
        if (MaterialSlotSemantics.Single(channels) is { } index)
        {
            floats[property] = index;
        }
    }

    /// <summary>
    /// Unity's own spelling of the surface the base material declares: the mode, and the cutoff
    /// a masked material clips at. Stated on the parameter set itself, so a consumer that never
    /// builds a Unity asset reads the same two properties.
    ///
    /// Only the BASE material declares a surface, so a set whose parent chain never reached one
    /// (its parent package is not in this build) states neither -- the surface is then the
    /// consumer's own default rather than a value invented here.
    /// </summary>
    public void StateSurfaceMode()
    {
        if (!floats.TryGetValue(UnrealMaterial.BlendModeName, out float declared))
        {
            return;
        }
        int blend = (int)declared;
        bool masked = blend == (int)EBlendMode.BLEND_Masked;
        floats[ModeName] = blend == (int)EBlendMode.BLEND_Opaque ? ModeOpaque : masked ? ModeCutout : ModeFade;
        if (masked && floats.TryGetValue(UnrealMaterial.OpacityMaskClipValueName, out float cutoff))
        {
            floats[CutoffName] = cutoff;
        }
    }

    /// <summary>
    /// The cached parameter tables -- on the cached data itself, or under its Parameters member
    /// in the engines that nest them: one entry per runtime parameter kind, in the engine's
    /// declared order, each listing its parameters beside a value table.
    /// </summary>
    private void ReadCached(FStructFallback parameters, string owner)
    {
        EGame game = provider.Versions.Game;
        if (!KindLayouts.TryGetValue(game, out string[]? layout))
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {owner}: no cached-parameter kind layout is declared for {game}; parameter defaults not read.");
            return;
        }
        FMaterialParameterInfo[]?[] entries = new FMaterialParameterInfo[]?[layout.Length];
        int count = 0;
        foreach (FPropertyTag tag in parameters.Properties)
        {
            if (!string.Equals(tag.Name.Text, RuntimeEntriesName, StringComparison.Ordinal)
                || tag.Tag?.GenericValue is not FScriptStruct { StructType: FStructFallback entry })
            {
                continue;
            }
            count++;
            if (tag.ArrayIndex < layout.Length)
            {
                entries[tag.ArrayIndex] = entry.GetOrDefault<FMaterialParameterInfo[]>(ParameterInfoSetName, []);
            }
        }
        if (count != layout.Length)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {owner}: {count} cached parameter entries beside the {layout.Length} kinds declared for {game}; parameter defaults not read.");
            return;
        }
        if (Logger.AllowVerbose)
        {
            Logger.Verbose(LogCategory.Import, $"[Unreal] {owner}: cached parameter counts {string.Join(", ", layout.Select((kind, index) => kind + "=" + (entries[index]?.Length ?? 0)))}.");
        }
        for (int kindIndex = 0; kindIndex < layout.Length; kindIndex++)
        {
            string kind = layout[kindIndex];
            FMaterialParameterInfo[] infos = entries[kindIndex] ?? [];
            string table = kind + ValuesSuffix;
            switch (kind)
            {
                case "Scalar":
                    Read(owner, kind, infos, parameters.GetOrDefault<float[]>(table, []), (name, value) => floats[name] = value);
                    break;
                case "Vector":
                    Read(owner, kind, infos, parameters.GetOrDefault<FLinearColor[]>(table, []), (name, value) => colors[name] = new Vector4(value.R, value.G, value.B, value.A));
                    break;
                case "DoubleVector":
                    Read(owner, kind, infos, parameters.GetOrDefault<TIntVector4<double>[]>(table, []), (name, value) => colors[name] = Vector(value));
                    break;
                case "Texture":
                    Read(owner, kind, infos, parameters.GetOrDefault<FSoftObjectPath[]>(table, []), (name, value) =>
                    {
                        parameterDefaults.Add(value.AssetPathName.Text);
                        textures[name] = value.AssetPathName.Text;
                    });
                    break;
                case "StaticSwitch":
                    Read(owner, kind, infos, parameters.GetOrDefault<bool[]>(table, []), (name, value) => floats[name] = value ? 1f : 0f);
                    break;
            }
        }
    }

    /// <summary>One kind's parameters beside its value table; a count mismatch means the layout is not this cook's, and nothing is read.</summary>
    private static void Read<T>(string owner, string kind, FMaterialParameterInfo[] infos, T[] values, Action<string, T> assign)
    {
        if (infos.Length != values.Length)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] {owner}: {infos.Length} {kind} parameter(s) beside {values.Length} value(s); this kind's defaults not read.");
            return;
        }
        for (int i = 0; i < infos.Length; i++)
        {
            assign(infos[i].Name.Text, values[i]);
        }
    }

    /// <summary>
    /// The graph's constant textures -- referenced, yet the default of no parameter -- grouped by
    /// the kind each texture declares for itself.
    /// </summary>
    private void Constants(FPackageIndex[] referenced)
    {
        List<UTexture> colors = new();
        List<UTexture> normals = new();
        List<UTexture> others = new();
        foreach (FPackageIndex pointer in referenced)
        {
            UTexture? loaded = pointer.Load() as UTexture;
            referencedNames.Add(loaded?.Name ?? string.Empty);
            if (loaded is not { } texture || parameterDefaults.Contains(texture.GetPathName()))
            {
                continue;
            }
            (texture.IsNormalMap ? normals : texture.SRGB ? colors : others).Add(texture);
        }
        bool parameterized = textures.Count > 0;
        Constant(colors, parameterized ? null : MainTextureName);
        Constant(normals, parameterized ? null : NormalMapName);
        Constant(others, null);
    }

    private void Constant(List<UTexture> group, string? role)
    {
        if (role is not null && group.Count == 1)
        {
            textures[role] = group[0].GetPathName();
            return;
        }
        foreach (UTexture texture in group)
        {
            textures[texture.Name] = texture.GetPathName();
        }
    }

    /// <summary>Every flagged override, its value read by its own property's kind: a switch, a number, or an enum by the game's enum order.</summary>
    private void ReadOverrides(FStructFallback overrides)
    {
        foreach (FPropertyTag flag in overrides.Properties)
        {
            string flagName = flag.Name.Text;
            if (!flagName.StartsWith(OverridePrefix, StringComparison.Ordinal) || flag.Tag?.GenericValue is not true)
            {
                continue;
            }
            string name = flagName[OverridePrefix.Length..];
            FPropertyTag? value = overrides.Properties.Find(tag => string.Equals(tag.Name.Text, name, StringComparison.Ordinal));
            if (value is not null && Scalar(value) is { } scalar)
            {
                floats[name] = scalar;
            }
        }
    }

    private float? Scalar(FPropertyTag tag) => tag.Tag?.GenericValue switch
    {
        bool value => value ? 1f : 0f,
        float value => value,
        double value => (float)value,
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToSingle(tag.Tag.GenericValue),
        FName value => Ordinal(tag.TagData?.EnumName, value.Text),
        _ => null,
    };

    /// <summary>An enum entry's ordinal in the game's reflection, from its qualified or bare name.</summary>
    private float? Ordinal(string? enumName, string entry)
    {
        if (enumName is null || mappings?.Enums.GetValueOrDefault(enumName) is not { } entries)
        {
            return null;
        }
        string bare = Bare(entry);
        foreach ((long ordinal, string name) in entries)
        {
            if (string.Equals(Bare(name), bare, StringComparison.Ordinal))
            {
                return ordinal;
            }
        }
        return null;
    }

    /// <summary>An enum entry without its enum-class scope: the identifier the engine declares.</summary>
    private static string Bare(string entry)
    {
        int scope = entry.IndexOf(EnumScope, StringComparison.Ordinal);
        return scope >= 0 ? entry[(scope + EnumScope.Length)..] : entry;
    }

    private static Vector4 Vector(TIntVector4<double> value) => new((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);
}
