using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.ColorRGBAf;
using AssetRipper.SourceGenerated.Subclasses.FastPropertyName;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.UnityPropertySheet;
using AssetRipper.SourceGenerated.Subclasses.UnityTexEnv;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// A Unity Material's property tables, normalised. Unity has spelled these several ways:
/// integer-typed shader properties moved out of m_Floats into m_Ints in 2021 (a toggle that
/// moved reads as absent otherwise), and shader keywords have had three serialisations, of
/// which only the valid list is enabled. Which property is which surface input is not decided
/// here -- that is a mapping, and a mapping is configuration (<see cref="TextureRoles"/>).
/// </summary>
public sealed class UnityMaterialProperties
{
    public required string Name { get; init; }

    public required string ShaderName { get; init; }

    /// <summary>Property name -> texture key, in the order the material declares them; an
    /// unset slot is absent.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Textures { get; init; }

    public required IReadOnlyList<KeyValuePair<string, ITexture2D>> TextureAssets { get; init; }

    public required IReadOnlyDictionary<string, float[]> TextureScaleOffset { get; init; }

    public required IReadOnlyList<KeyValuePair<string, float>> FloatEntries { get; init; }

    public required IReadOnlyList<KeyValuePair<string, float[]>> ColorEntries { get; init; }

    public required IReadOnlyList<string> KeywordList { get; init; }

    public required IReadOnlyList<string> DisabledPasses { get; init; }

    /// <summary>The passes its shader draws with (<see cref="UnityMaterials.PassesOf"/>); empty
    /// where the source engine has no shader passes to state.</summary>
    public required IReadOnlyList<UnityShaderPass> ShaderPasses { get; init; }

    public IReadOnlyDictionary<string, float> Floats => _floats ??= Fold(FloatEntries);

    public IReadOnlyDictionary<string, float[]> Colors => _colors ??= Fold(ColorEntries);

    public IReadOnlySet<string> Keywords => _keywords ??= new HashSet<string>(KeywordList, StringComparer.Ordinal);

    private Dictionary<string, float>? _floats;
    private Dictionary<string, float[]>? _colors;
    private HashSet<string>? _keywords;

    private static Dictionary<string, T> Fold<T>(IReadOnlyList<KeyValuePair<string, T>> entries)
    {
        Dictionary<string, T> folded = new(entries.Count, StringComparer.Ordinal);
        foreach ((string name, T value) in entries)
        {
            folded[name] = value;
        }
        return folded;
    }
}

/// <summary>One pass of a shader: the name it declares and its LightMode tag, empty when it
/// declares none. The engine finds a tagged pass by the tag and an untagged one by its name;
/// a material disables either by that same word.</summary>
public readonly record struct UnityShaderPass(string Name, string LightMode);

public static class UnityMaterials
{
    private const string LightModeTag = "LightMode";

    /// <summary>The passes of the SubShader the engine draws with, which is the first: the engine
    /// takes the first SubShader the target can run, and every SubShader these shaders ship runs on
    /// the desktop target. A pass borrowed from another shader (<c>UsePass "Shader/NAME"</c>) is
    /// stated by the name it borrows.</summary>
    public static IReadOnlyList<UnityShaderPass> PassesOf(IShader? shader)
    {
        if (shader is null || !shader.Has_ParsedForm() || shader.ParsedForm.SubShaders.Count == 0)
        {
            return [];
        }
        List<UnityShaderPass> passes = [];
        foreach (ISerializedPass pass in shader.ParsedForm.SubShaders[0].Passes)
        {
            string borrowed = pass.UseName.String;
            if (borrowed.Length > 0)
            {
                passes.Add(new UnityShaderPass(borrowed[(borrowed.LastIndexOf('/') + 1)..], string.Empty));
                continue;
            }
            passes.Add(new UnityShaderPass(pass.State.Name.String, LightModeOf(pass)));
        }
        return passes;
    }

    private static string LightModeOf(ISerializedPass pass)
    {
        foreach ((Utf8String key, Utf8String value) in pass.State.Tags.Tags)
        {
            if (key.String == LightModeTag)
            {
                return value.String;
            }
        }
        foreach ((Utf8String key, Utf8String value) in pass.Tags.Tags)
        {
            if (key.String == LightModeTag)
            {
                return value.String;
            }
        }
        return string.Empty;
    }

    public static string ShaderNameOf(IShader? shader)
    {
        if (shader is null)
        {
            return string.Empty;
        }
        if (shader.Has_ParsedForm() && shader.ParsedForm.Name.String.Length > 0)
        {
            return shader.ParsedForm.Name.String;
        }
        return shader.Name.String;
    }

    public static UnityMaterialProperties Read(IMaterial material, Func<IUnityObjectBase, string> keyOf)
    {
        IUnityPropertySheet sheet = material.SavedProperties_C21;
        List<KeyValuePair<string, string>> textures = [];
        List<KeyValuePair<string, ITexture2D>> textureAssets = [];
        Dictionary<string, float[]> scaleOffset = new(StringComparer.Ordinal);
        foreach ((AssetRipper.Primitives.Utf8String name, IUnityTexEnv environment) in material.GetTextureProperties())
        {
            string property = name.String;
            if (environment.Texture.TryGetAsset(material.Collection) is ITexture2D texture)
            {
                textures.Add(new KeyValuePair<string, string>(property, keyOf(texture)));
                textureAssets.Add(new KeyValuePair<string, ITexture2D>(property, texture));
            }
            scaleOffset[property] =
            [
                environment.Scale.X, environment.Scale.Y, environment.Offset.X, environment.Offset.Y,
            ];
        }

        Dictionary<string, float> merged = new(StringComparer.Ordinal);
        List<string> order = [];
        if (sheet.Has_Ints())
        {
            foreach (AccessPairBase<AssetRipper.Primitives.Utf8String, int> pair in sheet.Ints)
            {
                Put(merged, order, pair.Key.String, pair.Value);
            }
        }
        if (sheet.Has_Floats_AssetDictionary_Utf8String_Single())
        {
            foreach (AccessPairBase<AssetRipper.Primitives.Utf8String, float> pair in sheet.Floats_AssetDictionary_Utf8String_Single)
            {
                Put(merged, order, pair.Key.String, pair.Value);
            }
        }
        else if (sheet.Has_Floats_AssetDictionary_FastPropertyName_Single())
        {
            foreach (AccessPairBase<FastPropertyName, float> pair in sheet.Floats_AssetDictionary_FastPropertyName_Single)
            {
                Put(merged, order, pair.Key.Name.String, pair.Value);
            }
        }
        List<KeyValuePair<string, float>> floats = new(order.Count);
        foreach (string name in order)
        {
            floats.Add(new KeyValuePair<string, float>(name, merged[name]));
        }

        List<KeyValuePair<string, float[]>> colors = [];
        if (sheet.Has_Colors_AssetDictionary_Utf8String_ColorRGBAf())
        {
            foreach (AccessPairBase<AssetRipper.Primitives.Utf8String, ColorRGBAf> pair in sheet.Colors_AssetDictionary_Utf8String_ColorRGBAf)
            {
                colors.Add(new KeyValuePair<string, float[]>(pair.Key.String, [pair.Value.R, pair.Value.G, pair.Value.B, pair.Value.A]));
            }
        }
        else if (sheet.Has_Colors_AssetDictionary_FastPropertyName_ColorRGBAf())
        {
            foreach (AccessPairBase<FastPropertyName, ColorRGBAf> pair in sheet.Colors_AssetDictionary_FastPropertyName_ColorRGBAf)
            {
                colors.Add(new KeyValuePair<string, float[]>(pair.Key.Name.String, [pair.Value.R, pair.Value.G, pair.Value.B, pair.Value.A]));
            }
        }

        List<string> keywords = [];
        if (material.Has_ShaderKeywords_C21_Utf8String())
        {
            foreach (string keyword in material.ShaderKeywords_C21_Utf8String.String.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                keywords.Add(keyword);
            }
        }
        if (material.Has_ShaderKeywords_C21_AssetList_Utf8String())
        {
            foreach (AssetRipper.Primitives.Utf8String keyword in material.ShaderKeywords_C21_AssetList_Utf8String)
            {
                if (!keyword.IsEmpty)
                {
                    keywords.Add(keyword.String);
                }
            }
        }
        if (material.Has_ValidKeywords_C21())
        {
            foreach (AssetRipper.Primitives.Utf8String keyword in material.ValidKeywords_C21)
            {
                if (!keyword.IsEmpty)
                {
                    keywords.Add(keyword.String);
                }
            }
        }

        List<string> disabledPasses = [];
        if (material.Has_DisabledShaderPasses_C21())
        {
            foreach (AssetRipper.Primitives.Utf8String pass in material.DisabledShaderPasses_C21)
            {
                if (!pass.IsEmpty)
                {
                    disabledPasses.Add(pass.String);
                }
            }
        }

        return new UnityMaterialProperties
        {
            Name = material.Name.String,
            ShaderName = ShaderNameOf(material.Shader_C21P),
            Textures = textures,
            TextureAssets = textureAssets,
            TextureScaleOffset = scaleOffset,
            FloatEntries = floats,
            ColorEntries = colors,
            KeywordList = keywords,
            DisabledPasses = disabledPasses,
            ShaderPasses = PassesOf(material.Shader_C21P),
        };
    }

    private static void Put(Dictionary<string, float> merged, List<string> order, string name, float value)
    {
        if (!merged.ContainsKey(name))
        {
            order.Add(name);
        }
        merged[name] = value;
    }
}
