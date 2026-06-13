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
            if (entry.Value is UTexture2D texture && context.Convert(texture) is ITexture2D converted)
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

    private static void AddTexEnv(IUnityPropertySheet sheet, string name, ITexture2D texture, AssetCollection collection)
    {
        if (sheet.Has_TexEnvs_AssetDictionary_Utf8String_UnityTexEnv_5())
        {
            var pair = sheet.TexEnvs_AssetDictionary_Utf8String_UnityTexEnv_5.AddNew();
            pair.Key = name;
            pair.Value.Texture.SetAsset(collection, texture);
            pair.Value.Scale.SetOne();
        }
        else if (sheet.Has_TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_5())
        {
            var pair = sheet.TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_5.AddNew();
            pair.Key.Name = name;
            pair.Value.Texture.SetAsset(collection, texture);
            pair.Value.Scale.SetOne();
        }
        else if (sheet.Has_TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_3_5())
        {
            var pair = sheet.TexEnvs_AssetDictionary_FastPropertyName_UnityTexEnv_3_5.AddNew();
            pair.Key.Name = name;
            pair.Value.Texture.SetAsset(collection, texture);
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
