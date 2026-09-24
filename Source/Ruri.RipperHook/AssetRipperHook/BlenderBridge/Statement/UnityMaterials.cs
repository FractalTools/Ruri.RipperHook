using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader.SerializedShader;
using AssetRipper.SourceGenerated.Subclasses.ColorRGBAf;
using AssetRipper.SourceGenerated.Subclasses.FastPropertyName;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedProperty;
using AssetRipper.SourceGenerated.Subclasses.UnityPropertySheet;
using AssetRipper.SourceGenerated.Subclasses.UnityTexEnv;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// A Unity Material's property tables, normalised. Unity has spelled these several ways:
/// integer-typed shader properties moved out of m_Floats into m_Ints in 2021 (a toggle that
/// moved reads as absent otherwise), and shader keywords have had three serialisations, of
/// which only the valid list is enabled. The number and colour tables are what the engine
/// reads: a property the material's sheet does not hold reads its shader's Properties default.
/// Which property is which surface input is not decided here -- that is a mapping, and a
/// mapping is configuration (<see cref="TextureRoles"/>).
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
        PutShaderDefaults(material.Shader_C21P, merged, order, colors);
        List<KeyValuePair<string, float>> floats = new(order.Count);
        foreach (string name in order)
        {
            floats.Add(new KeyValuePair<string, float>(name, merged[name]));
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

    /// <summary>A sheet only holds what was set while the material used a shader that declared it, so a
    /// property its current shader declares and its sheet lacks reads that shader's own default (the engine's
    /// Properties block): a number its first default component, a colour or vector all four. Textures are not
    /// filled here -- an empty slot samples the shader's default texture, which a host states per slot.</summary>
    private static void PutShaderDefaults(IShader? shader, Dictionary<string, float> merged, List<string> order,
        List<KeyValuePair<string, float[]>> colors)
    {
        if (shader is null || !shader.Has_ParsedForm())
        {
            return;
        }
        HashSet<string> coloured = new(StringComparer.Ordinal);
        foreach ((string name, float[] _) in colors)
        {
            coloured.Add(name);
        }
        foreach (ISerializedProperty property in shader.ParsedForm.PropInfo.Props)
        {
            string name = property.Name.String;
            switch (property.GetType_())
            {
                case SerializedPropertyType.Float:
                case SerializedPropertyType.Range:
                case SerializedPropertyType.Int:
                    if (!merged.ContainsKey(name))
                    {
                        Put(merged, order, name, property.DefValue_0_);
                    }
                    break;
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Vector:
                    if (coloured.Add(name))
                    {
                        colors.Add(new KeyValuePair<string, float[]>(name,
                            [property.DefValue_0_, property.DefValue_1_, property.DefValue_2_, property.DefValue_3_]));
                    }
                    break;
            }
        }
    }

    private static void Put(Dictionary<string, float> merged, List<string> order, string name, float value)
    {
        if (!merged.ContainsKey(name))
        {
            order.Add(name);
        }
        merged[name] = value;
    }

    /// <summary>A material as a title leaves it after writing onto it at run time, under the name its
    /// instance is stated by. Each write, in order, does what the engine's own setter does to the
    /// material's tables: a colour or a float replaces the stored value or joins the table, a texture
    /// fills its slot or, being null, empties it, and a keyword is enabled or disabled.</summary>
    public static UnityMaterialProperties Written(UnityMaterialProperties material, string name,
        IReadOnlyList<MaterialWrite> writes, Func<IUnityObjectBase, string> keyOf)
    {
        List<KeyValuePair<string, float>> floats = [.. material.FloatEntries];
        List<KeyValuePair<string, float[]>> colors = [.. material.ColorEntries];
        List<KeyValuePair<string, string>> textures = [.. material.Textures];
        List<KeyValuePair<string, ITexture2D>> textureAssets = [.. material.TextureAssets];
        List<string> keywords = [.. material.KeywordList];
        foreach (MaterialWrite write in writes)
        {
            switch (write.Kind)
            {
                case MaterialWriteKind.Color:
                    Set(colors, write.Property, write.Value);
                    break;
                case MaterialWriteKind.Float:
                    Set(floats, write.Property, write.Value[0]);
                    break;
                case MaterialWriteKind.Texture:
                    if (write.Texture is { } texture)
                    {
                        Set(textures, write.Property, keyOf(texture));
                        Set(textureAssets, write.Property, texture);
                    }
                    else
                    {
                        textures.RemoveAll(entry => entry.Key == write.Property);
                        textureAssets.RemoveAll(entry => entry.Key == write.Property);
                    }
                    break;
                case MaterialWriteKind.Keyword:
                    keywords.RemoveAll(keyword => keyword == write.Property);
                    if (write.Value[0] != 0.0f)
                    {
                        keywords.Add(write.Property);
                    }
                    break;
            }
        }
        return new UnityMaterialProperties
        {
            Name = name,
            ShaderName = material.ShaderName,
            Textures = textures,
            TextureAssets = textureAssets,
            TextureScaleOffset = material.TextureScaleOffset,
            FloatEntries = floats,
            ColorEntries = colors,
            KeywordList = keywords,
            DisabledPasses = material.DisabledPasses,
            ShaderPasses = material.ShaderPasses,
        };
    }

    /// <summary>What tells one list of writes from another: every write's property, kind, value and
    /// texture, hashed -- two renderers written alike draw the same material.</summary>
    public static string WriteSignature(IReadOnlyList<MaterialWrite> writes, Func<IUnityObjectBase, string> keyOf)
    {
        System.Text.StringBuilder text = new();
        foreach (MaterialWrite write in writes)
        {
            text.Append(write.Property).Append('\u001f').Append((int)write.Kind).Append('\u001f');
            foreach (float component in write.Value)
            {
                text.Append(BitConverter.SingleToInt32Bits(component).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            }
            text.Append('\u001f').Append(write.Texture is null ? string.Empty : keyOf(write.Texture)).Append('\u001e');
        }
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static void Set<T>(List<KeyValuePair<string, T>> entries, string name, T value)
    {
        int index = entries.FindIndex(entry => entry.Key == name);
        if (index < 0)
        {
            entries.Add(new KeyValuePair<string, T>(name, value));
        }
        else
        {
            entries[index] = new KeyValuePair<string, T>(name, value);
        }
    }
}
