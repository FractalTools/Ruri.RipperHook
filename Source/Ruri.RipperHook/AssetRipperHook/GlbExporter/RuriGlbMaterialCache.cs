using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Material;
using AssetRipper.SourceGenerated.Subclasses.UnityTexEnv;
using SharpGLTF.Materials;
using SharpGLTF.Memory;

namespace Ruri.RipperHook.GlbExporter;

/// <summary>
/// Material/texture -> SharpGLTF MaterialBuilder cache. Mirrors the material half of AR's
/// GlbLevelBuilder.BuildParameters (frozen tree, private) with a data-driven texture-property
/// name table extended beyond AR's four "_MainTex" aliases to the URP/game names EndField uses.
/// </summary>
public sealed class RuriGlbMaterialCache
{
    private static readonly HashSet<string> MainTextureNames = new(StringComparer.Ordinal)
    {
        "_MainTex", "texture", "Texture", "_Texture",
        "_BaseMap", "_BaseColorMap", "_BaseColorTex", "_BaseTex",
        "_AlbedoMap", "_Albedo", "_Diffuse", "_DiffuseTex", "_DiffuseMap",
    };

    private static readonly HashSet<string> NormalTextureNames = new(StringComparer.Ordinal)
    {
        "_Normal", "Normal", "normal",
        "_BumpMap", "_NormalMap", "_NormalTex",
    };

    private readonly MaterialBuilder _defaultMaterial = new("DefaultMaterial");
    private readonly Dictionary<ITexture2D, MemoryImage?> _imageCache = new();
    private readonly Dictionary<IMaterial, MaterialBuilder> _materialCache = new();

    public MaterialBuilder GetOrMakeMaterial(IMaterial? material)
    {
        if (material is null)
        {
            return _defaultMaterial;
        }
        if (!_materialCache.TryGetValue(material, out MaterialBuilder? materialBuilder))
        {
            materialBuilder = MakeMaterialBuilder(material);
            _materialCache.Add(material, materialBuilder);
        }
        return materialBuilder;
    }

    /// <summary>SkinnedMeshRenderer/MeshRenderer 的材质列表按 submesh 序号对齐。</summary>
    public MaterialBuilder GetOrMakeMaterial(IRenderer renderer, int subMeshIndex)
    {
        AccessListBase<IPPtr_Material> materials = renderer.Materials_C25;
        IMaterial? material = subMeshIndex < materials.Count
            ? materials[subMeshIndex].TryGetAsset(renderer.Collection)
            : null;
        return GetOrMakeMaterial(material);
    }

    private MaterialBuilder MakeMaterialBuilder(IMaterial material)
    {
        MaterialBuilder materialBuilder = new(material.Name);
        GetTextures(material, out ITexture2D? mainTexture, out ITexture2D? normalTexture);
        if (mainTexture is not null && TryGetOrMakeImage(mainTexture, out MemoryImage mainImage))
        {
            materialBuilder.WithBaseColor(mainImage);
        }
        if (normalTexture is not null && TryGetOrMakeImage(normalTexture, out MemoryImage normalImage))
        {
            materialBuilder.WithNormal(normalImage);
        }
        return materialBuilder;
    }

    private bool TryGetOrMakeImage(ITexture2D texture, out MemoryImage image)
    {
        if (_imageCache.TryGetValue(texture, out MemoryImage? cached))
        {
            image = cached.GetValueOrDefault();
            return cached.HasValue;
        }
        if (TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
        {
            using MemoryStream memoryStream = new();
            bitmap.SaveAsPng(memoryStream);
            image = new MemoryImage(memoryStream.ToArray());
            _imageCache.Add(texture, image);
            return true;
        }
        // 转换失败也缓存,免得同一张失败贴图反复重试解码。
        _imageCache.Add(texture, null);
        image = default;
        return false;
    }

    private static void GetTextures(IMaterial material, out ITexture2D? mainTexture, out ITexture2D? normalTexture)
    {
        mainTexture = null;
        normalTexture = null;
        ITexture2D? mainReplacement = null;
        AssetCollection collection = material.Collection;
        foreach ((Utf8String utf8Name, IUnityTexEnv textureParameter) in material.GetTextureProperties())
        {
            string name = utf8Name.String;
            if (MainTextureNames.Contains(name))
            {
                mainTexture ??= textureParameter.Texture.TryGetAsset(collection) as ITexture2D;
            }
            else if (NormalTextureNames.Contains(name))
            {
                normalTexture ??= textureParameter.Texture.TryGetAsset(collection) as ITexture2D;
            }
            else
            {
                mainReplacement ??= textureParameter.Texture.TryGetAsset(collection) as ITexture2D;
            }
        }
        mainTexture ??= mainReplacement;
    }
}
