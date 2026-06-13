using AssetRipper.Assets.Collections;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.UnityPropertySheet;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Mappings;

// UMaterialInterface (UMaterial / UMaterialInstanceConstant / ...) -> Material.
// CUE4Parse's GetParams flattens the whole scalar/vector/texture parameter graph
// (across all parent layers) into one CMaterialParams2; we pour that straight
// into the Unity UnityPropertySheet (m_TexEnvs / m_Floats / m_Colors). Texture
// params become PPtrs to converted Texture2D assets through the shared context.
//
// The property sheet is versioned — TexEnvs/Floats/Colors each have several
// serialization shapes — so every write picks the live variant via Has_*(),
// exactly as AssetRipper's own readers do.
//
// The shader pointer is left null for now (Unity reads {fileID: 0} as "no
// shader"); pointing it at a Hidden/InternalErrorShader placeholder is a later
// refinement that needs a synthetic shader asset.
public static class MaterialMappings
{
    public static void Register()
    {
        MapperRegistry.Map<UMaterialInterface, IMaterial>(collection => collection.CreateMaterial())
            .Set(t => t.Name_C21, s => new Utf8String(s.Name))
            .After(PopulateSavedProperties);
    }

    private static void PopulateSavedProperties(UMaterialInterface material, IMaterial destination, ConversionContext context)
    {
        CMaterialParams2 parameters = new();
        try
        {
            material.GetParams(parameters, EMaterialFormat.AllLayers);
        }
        catch
        {
            // GetParams can throw on exotic custom material-expression graphs;
            // whatever it flattened before the throw is still usable.
        }

        IUnityPropertySheet sheet = destination.SavedProperties_C21;
        AssetCollection collection = destination.Collection;

        foreach (KeyValuePair<string, UUnrealMaterial> entry in parameters.Textures)
        {
            // Resolve the UE texture reference to an AR Texture2D PPtr when possible.
            // Cubemaps / texture arrays / volume / render-target textures don't
            // currently have an AR mapping registered, and an unmapped or non-2D
            // texture must not silently erase the named property — Unity shaders
            // bind by property name, and a missing slot can leave a uniform
            // unbound at runtime. Emit the TexEnv with a null Texture PPtr in
            // that case so the slot survives the round-trip with the right name,
            // Scale=(1,1), Offset=(0,0).
            ITexture2D? converted = entry.Value is UTexture2D texture2D
                ? context.Convert(texture2D) as ITexture2D
                : null;
            AddTexEnv(sheet, entry.Key, converted, collection);
        }
        foreach (KeyValuePair<string, FLinearColor> entry in parameters.Colors)
            AddColor(sheet, entry.Key, entry.Value);
        foreach (KeyValuePair<string, float> entry in parameters.Scalars)
            AddFloat(sheet, entry.Key, entry.Value);
        // Static-switch parameters carry no native Unity slot; fold them into
        // floats as 0/1 so the value survives the round-trip.
        foreach (KeyValuePair<string, bool> entry in parameters.Switches)
            AddFloat(sheet, entry.Key, entry.Value ? 1f : 0f);
    }

    // `texture` may be null when the source UE texture is not a UTexture2D (e.g.
    // cubemap / 2D-array / volume) or has no AR mapping registered yet — the
    // Texture PPtr is then left at {fileID:0, pathID:0} but the property slot
    // still ends up with the correct name and default sampler transform.
    private static void AddTexEnv(IUnityPropertySheet sheet, string name, ITexture2D? texture, AssetCollection collection)
    {
        if (sheet.Has_TexEnvs_AssetDictionary_Utf8String_UnityTexEnv_5())
        {
            var pair = sheet.TexEnvs_AssetDictionary_Utf8String_UnityTexEnv_5.AddNew();
            pair.Key = name;
            if (texture is not null) pair.Value.Texture.SetAsset(collection, texture);
            pair.Value.Scale.SetOne();
        }
        else if (sheet.Has_TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_5())
        {
            var pair = sheet.TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_5.AddNew();
            pair.Key.Name = name;
            if (texture is not null) pair.Value.Texture.SetAsset(collection, texture);
            pair.Value.Scale.SetOne();
        }
        else if (sheet.Has_TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_3_5())
        {
            var pair = sheet.TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_3_5.AddNew();
            pair.Key.Name = name;
            if (texture is not null) pair.Value.Texture.SetAsset(collection, texture);
            // UnityTexEnv_3_5 also has Scale — default-construction leaves it at
            // (0,0), but Unity's m_Scale is (1,1) for an untiled UV. Mirror the
            // newer layouts so the YAML round-trips identically across versions.
            pair.Value.Scale.SetOne();
        }
    }

    private static void AddColor(IUnityPropertySheet sheet, string name, FLinearColor color)
    {
        if (sheet.Has_Colors_AssetDictionary_Utf8String_ColorRGBAf())
        {
            var pair = sheet.Colors_AssetDictionary_Utf8String_ColorRGBAf.AddNew();
            pair.Key = name;
            pair.Value.SetValues(color.R, color.G, color.B, color.A);
        }
        else if (sheet.Has_Colors_AssetDictionary_FastPropertyName_ColorRGBAf())
        {
            var pair = sheet.Colors_AssetDictionary_FastPropertyName_ColorRGBAf.AddNew();
            pair.Key.Name = name;
            pair.Value.SetValues(color.R, color.G, color.B, color.A);
        }
    }

    private static void AddFloat(IUnityPropertySheet sheet, string name, float value)
    {
        if (sheet.Has_Floats_AssetDictionary_Utf8String_Single())
        {
            sheet.Floats_AssetDictionary_Utf8String_Single.Add(name, value);
        }
        else if (sheet.Has_Floats_AssetDictionary_FastPropertyName_Single())
        {
            var pair = sheet.Floats_AssetDictionary_FastPropertyName_Single.AddNew();
            pair.Key.Name = name;
            pair.Value = value;
        }
    }
}
