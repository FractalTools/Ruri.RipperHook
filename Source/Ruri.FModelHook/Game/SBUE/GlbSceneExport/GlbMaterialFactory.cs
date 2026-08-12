using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class GlbMaterialFactory
{
    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    private readonly HashSet<string> _registeredMaterialPathNames = new(StringComparer.Ordinal);

    private readonly Dictionary<string, MaterialEmbedBundle> _bundlesByPathName = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _firstPathNameByMaterialName = new(StringComparer.Ordinal);

    private readonly Dictionary<string, DecodedTextureMip0> _decodedMip0ByTexturePathName = new(StringComparer.Ordinal);

    private readonly HashSet<string> _extraMipsAlreadyDecodedTexturePathNames = new(StringComparer.Ordinal);

    private readonly struct DecodedTextureMip0
    {
        public readonly byte[] Bytes;
        public readonly string Extension;

        public DecodedTextureMip0(byte[] bytes, string extension)
        {
            Bytes = bytes;
            Extension = extension;
        }
    }

    private readonly object _decodeLock = new();

    public GlbMaterialFactory(Action<string> log, Action<string> logError)
    {
        _log = log;
        _logError = logError;
    }

    public bool RegisterUnique(UMaterialInterface? material)
    {
        if (material is null) return false;
        string pathName = material.GetPathName();
        return _registeredMaterialPathNames.Add(pathName);
    }

    public int UniqueMaterialCount => _registeredMaterialPathNames.Count;

    public IReadOnlyCollection<string> RegisteredMaterialPathNames => _registeredMaterialPathNames;

    public MaterialEmbedBundle? RegisterMaterial(UMaterialInterface? material, ExportOptions options)
    {
        if (material is null) return null;

        string pathName = material.GetPathName();
        if (_bundlesByPathName.TryGetValue(pathName, out var existing))
        {
            _registeredMaterialPathNames.Add(pathName);
            return existing;
        }

        string ownerName = material.Owner?.Name ?? material.Name;
        string materialInternalPath = (material.Owner?.Provider?.FixPath(ownerName) ?? material.Name).SubstringBeforeLast('.');

        var parameters = new CMaterialParams2();
        try
        {
            material.GetParams(parameters, options.MaterialDepth);
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   GetParams failed for material '{pathName}': {ex.Message}");
        }

        var textureNamesByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters.Textures)
        {
            textureNamesByKey[pair.Key] = pair.Value.GetPathName();
        }

        var bundle = new MaterialEmbedBundle(
            materialPathName: pathName,
            materialName: material.Name,
            materialInternalPath: materialInternalPath,
            parameters: parameters,
            textureNamesByKey: textureNamesByKey);

        foreach (var pair in parameters.Textures)
        {
            string textureRoleKey = pair.Key;
            if (pair.Value is not UTexture2D texture) continue;
            DecodeAndCacheAllMips(texture, options, bundle, textureRoleKey);
        }

        _bundlesByPathName[pathName] = bundle;
        _registeredMaterialPathNames.Add(pathName);

        if (!_firstPathNameByMaterialName.ContainsKey(material.Name))
        {
            _firstPathNameByMaterialName[material.Name] = pathName;
        }
        else if (!string.Equals(_firstPathNameByMaterialName[material.Name], pathName, StringComparison.Ordinal))
        {
            _log($"[GlbScene]   material name collision: '{material.Name}' has both '{_firstPathNameByMaterialName[material.Name]}' and '{pathName}' — first wins on embed.");
        }

        return bundle;
    }

    public bool TryGetEmbedBundleByMaterialName(string materialName, out MaterialEmbedBundle bundle)
    {
        if (_firstPathNameByMaterialName.TryGetValue(materialName, out var pathName)
            && _bundlesByPathName.TryGetValue(pathName, out var b))
        {
            bundle = b;
            return true;
        }
        bundle = default!;
        return false;
    }

    public IReadOnlyCollection<MaterialEmbedBundle> Bundles => _bundlesByPathName.Values;

    public int EmbedIntoAllParts(string searchRootDirectory)
    {
        if (!Directory.Exists(searchRootDirectory))
        {
            _logError($"[GlbScene]   embed pass: search root '{searchRootDirectory}' does not exist.");
            return 0;
        }

        string[] partFiles = Directory.GetFiles(searchRootDirectory, "*.glb", SearchOption.AllDirectories);
        int embeddedFileCount = 0;
        foreach (string glbPath in partFiles)
        {
            try
            {
                if (EmbedIntoPart(glbPath)) embeddedFileCount++;
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene]   embed pass: '{glbPath}' failed: {ex.Message}");
            }
        }
        return embeddedFileCount;
    }

    private void DecodeAndCacheAllMips(UTexture2D texture, ExportOptions options, MaterialEmbedBundle bundle, string roleKey)
    {
        string texturePathName = texture.GetPathName();

        _log($"[GlbScene]   decode texture: {texturePathName} (role={roleKey})");

        DecodedTextureMip0 mip0;
        try
        {
            lock (_decodeLock)
            {
                if (!_decodedMip0ByTexturePathName.TryGetValue(texturePathName, out mip0))
                {
                    CTexture? bitmap = texture.Decode(options.TexturePlatform);
                    if (bitmap is null)
                    {
                        _logError($"[GlbScene]   texture '{texturePathName}' decode returned null at mip 0.");
                        return;
                    }
                    byte[] payload = bitmap.Encode(options.TextureFormat, options.ExportHdrTexturesAsHdr, out var ext);
                    mip0 = new DecodedTextureMip0(payload, ext);
                    _decodedMip0ByTexturePathName[texturePathName] = mip0;
                }
            }
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   texture '{texturePathName}' mip 0 decode failed: {ex.Message}");
            return;
        }

        string textureInternalPath = (texture.Owner?.Provider?.FixPath(texture.Owner.Name) ?? texture.Name).SubstringBeforeLast('.');
        bundle.RecordTextureFile(roleKey, textureInternalPath, mip0.Extension, mip0.Bytes);

        bool extraMipsAreOursToDecode;
        lock (_decodeLock)
        {
            extraMipsAreOursToDecode = _extraMipsAlreadyDecodedTexturePathNames.Add(texturePathName);
        }
        if (!extraMipsAreOursToDecode)
        {
            return;
        }

        var platformData = texture.PlatformData;
        if (platformData?.Mips is null) return;

        string textureMipBasePath = (texture.Owner?.Provider?.FixPath(texture.Owner.Name) ?? texture.Name).SubstringBeforeLast('.');
        int firstMipIndex = texture.GetFirstMipIndex();
        for (int mipIndex = firstMipIndex + 1; mipIndex < platformData.Mips.Length; mipIndex++)
        {
            try
            {
                lock (_decodeLock)
                {
                    var bitmap = texture.DecodeMip(mipIndex, options.TexturePlatform);
                    if (bitmap is null) continue;
                    byte[] mipPng = bitmap.Encode(options.TextureFormat, options.ExportHdrTexturesAsHdr, out var ext);
                    bundle.RecordExtraMipFile(textureMipBasePath, mipIndex, mipPng, ext);
                }
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene]   texture '{texturePathName}' mip {mipIndex} decode failed: {ex.Message}");
            }
        }
    }

    public bool EmbedIntoPart(string glbFilePath)
    {
        ModelRoot model;
        try
        {
            model = ModelRoot.Load(glbFilePath);
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   embed: load '{glbFilePath}' failed: {ex.Message}");
            return false;
        }

        int rebindCount = 0;
        foreach (var schemaMaterial in model.LogicalMaterials)
        {
            if (string.IsNullOrEmpty(schemaMaterial.Name)) continue;
            if (!TryGetEmbedBundleByMaterialName(schemaMaterial.Name, out var bundle)) continue;
            ApplyBundleToMaterial(model, schemaMaterial, bundle);
            rebindCount++;
        }

        if (rebindCount == 0)
        {
            return false;
        }

        try
        {
            using var output = File.Create(glbFilePath);
            model.WriteGLB(output);
            _log($"[GlbScene]   embed: rebound {rebindCount} materials in '{Path.GetFileName(glbFilePath)}'.");
            return true;
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   embed: write back '{glbFilePath}' failed: {ex.Message}");
            return false;
        }
    }

    private static void ApplyBundleToMaterial(ModelRoot model, Material schemaMaterial, MaterialEmbedBundle bundle)
    {
        if (bundle.TryGetPngForRoleSet(CMaterialParams2.Diffuse[0], out var baseColorPng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackDiffuse, out baseColorPng))
        {
            BindChannel(model, schemaMaterial, "BaseColor", baseColorPng);
        }
        else if (bundle.Parameters.TryGetLinearColor(out var baseColor, "BaseColor", "DiffuseColor", "Color"))
        {
            var channelNullable = schemaMaterial.FindChannel("BaseColor");
            if (channelNullable.HasValue)
            {
                var channel = channelNullable.Value;
                channel.Parameter = new Vector4(baseColor.R, baseColor.G, baseColor.B, baseColor.A);
            }
        }

        if (bundle.TryGetPngForRoleSet(CMaterialParams2.Normals[0], out var normalPng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackNormals, out normalPng))
        {
            BindChannel(model, schemaMaterial, "Normal", normalPng);
        }

        if (bundle.TryGetPngForRoleSet(CMaterialParams2.SpecularMasks[0], out var metallicRoughnessPng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackSpecularMasks, out metallicRoughnessPng))
        {
            BindChannel(model, schemaMaterial, "MetallicRoughness", metallicRoughnessPng);
        }

        if (bundle.TryGetPngForRoleSet(CMaterialParams2.Emissive[0], out var emissivePng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackEmissive, out emissivePng))
        {
            BindChannel(model, schemaMaterial, "Emissive", emissivePng);
        }
        else if (bundle.Parameters.TryGetLinearColor(out var emissiveColor, "Emissive", "EmissiveColor"))
        {
            var emissiveChannelNullable = schemaMaterial.FindChannel("Emissive");
            if (emissiveChannelNullable.HasValue)
            {
                var emissiveChannel = emissiveChannelNullable.Value;
                emissiveChannel.Parameter = new Vector4(emissiveColor.R, emissiveColor.G, emissiveColor.B, 1f);
            }
        }

        switch (bundle.Parameters.BlendMode)
        {
            case EBlendMode.BLEND_Translucent:
            case EBlendMode.BLEND_Additive:
            case EBlendMode.BLEND_Modulate:
            case EBlendMode.BLEND_AlphaComposite:
            case EBlendMode.BLEND_AlphaHoldout:
            case EBlendMode.BLEND_TranslucentColoredTransmittance:
                schemaMaterial.Alpha = AlphaMode.BLEND;
                break;
            case EBlendMode.BLEND_Masked:
                schemaMaterial.Alpha = AlphaMode.MASK;
                break;
        }
    }

    private static void BindChannel(ModelRoot model, Material schemaMaterial, string channelKey, byte[] pngBytes)
    {
        var channel = schemaMaterial.FindChannel(channelKey);
        if (!channel.HasValue) return;
        if (pngBytes.Length == 0) return;

        MemoryImage memoryImage;
        try
        {
            memoryImage = new MemoryImage(pngBytes);
        }
        catch (ArgumentException)
        {
            return;
        }
        if (!memoryImage.IsValid) return;

        Image image = model.UseImageWithContent(memoryImage);
        channel.Value.SetTexture(0, image);
    }

    public void WriteSidecars(string outputDirectory)
    {
        var baseDirectory = new DirectoryInfo(outputDirectory);
        foreach (var bundle in _bundlesByPathName.Values)
        {
            try
            {
                bundle.WriteSidecarTo(baseDirectory, _logError);
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene]   sidecar '{bundle.MaterialPathName}' write failed: {ex.Message}");
            }
        }
    }
}

public sealed class MaterialEmbedBundle
{
    public string MaterialPathName { get; }
    public string MaterialName { get; }
    public string MaterialInternalPath { get; }
    public CMaterialParams2 Parameters { get; }
    public IReadOnlyDictionary<string, string> TextureNamesByKey => _textureNamesByKey;

    private readonly Dictionary<string, string> _textureNamesByKey;
    private readonly Dictionary<string, byte[]> _pngByRoleKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextureFileSpec> _textureFilesByRoleKey = new(StringComparer.Ordinal);
    private readonly List<MipFileSpec> _extraMipFiles = new();

    public MaterialEmbedBundle(
        string materialPathName,
        string materialName,
        string materialInternalPath,
        CMaterialParams2 parameters,
        Dictionary<string, string> textureNamesByKey)
    {
        MaterialPathName = materialPathName;
        MaterialName = materialName;
        MaterialInternalPath = materialInternalPath;
        Parameters = parameters;
        _textureNamesByKey = textureNamesByKey;
    }

    internal void RecordTextureFile(string roleKey, string textureInternalPath, string extension, byte[] mip0Png)
    {
        _pngByRoleKey[roleKey] = mip0Png;
        _textureFilesByRoleKey[roleKey] = new TextureFileSpec(textureInternalPath, extension, mip0Png);
    }

    internal void RecordExtraMipFile(string textureInternalPath, int mipIndex, byte[] pngBytes, string extension)
    {
        _extraMipFiles.Add(new MipFileSpec(textureInternalPath, mipIndex, pngBytes, extension));
    }

    public bool TryGetPngForRoleSet(string[] roleNameCandidates, out byte[] pngBytes)
    {
        foreach (string roleName in roleNameCandidates)
        {
            if (_pngByRoleKey.TryGetValue(roleName, out var bytes))
            {
                pngBytes = bytes;
                return true;
            }
        }
        pngBytes = Array.Empty<byte>();
        return false;
    }

    public bool TryGetPngForFallback(string fallbackKey, out byte[] pngBytes)
    {
        if (_pngByRoleKey.TryGetValue(fallbackKey, out var bytes))
        {
            pngBytes = bytes;
            return true;
        }
        pngBytes = Array.Empty<byte>();
        return false;
    }

    public void WriteSidecarTo(DirectoryInfo baseDirectory, Action<string>? perFileErrorSink = null)
    {
        var materialData = new MaterialJsonPayload
        {
            Textures = new Dictionary<string, string>(_textureNamesByKey),
            Parameters = Parameters,
        };
        string jsonPath = FixAndCreatePath(baseDirectory, MaterialInternalPath, "json");
        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(materialData, Formatting.Indented));

        foreach (var pair in _textureFilesByRoleKey)
        {
            var spec = pair.Value;
            string texturePath = FixAndCreatePath(baseDirectory, spec.TextureInternalPath, spec.Extension);
            try
            {
                File.WriteAllBytes(texturePath, spec.PngBytes);
            }
            catch (Exception ex)
            {
                perFileErrorSink?.Invoke($"[GlbScene]   sidecar texture write '{texturePath}' failed: {ex.Message}");
            }
        }

        foreach (var mip in _extraMipFiles)
        {
            string mipBasePath = mip.TextureInternalPath + ".mip" + mip.MipIndex.ToString();
            string mipPath = FixAndCreatePath(baseDirectory, mipBasePath, mip.Extension);
            try
            {
                File.WriteAllBytes(mipPath, mip.PngBytes);
            }
            catch (Exception ex)
            {
                perFileErrorSink?.Invoke($"[GlbScene]   sidecar mip write '{mipPath}' failed: {ex.Message}");
            }
        }
    }

    private static string FixAndCreatePath(DirectoryInfo baseDirectory, string fullPath, string extension)
    {
        if (fullPath.StartsWith('/')) fullPath = fullPath[1..];
        string path = Path.Combine(baseDirectory.FullName, fullPath) + "." + extension.ToLowerInvariant();
        string parentDirectory = path.Replace('\\', '/').SubstringBeforeLast('/');
        Directory.CreateDirectory(parentDirectory);
        return path;
    }

    private readonly struct TextureFileSpec
    {
        public readonly string TextureInternalPath;
        public readonly string Extension;
        public readonly byte[] PngBytes;

        public TextureFileSpec(string textureInternalPath, string extension, byte[] pngBytes)
        {
            TextureInternalPath = textureInternalPath;
            Extension = extension;
            PngBytes = pngBytes;
        }
    }

    private readonly struct MipFileSpec
    {
        public readonly string TextureInternalPath;
        public readonly int MipIndex;
        public readonly byte[] PngBytes;
        public readonly string Extension;

        public MipFileSpec(string textureInternalPath, int mipIndex, byte[] pngBytes, string extension)
        {
            TextureInternalPath = textureInternalPath;
            MipIndex = mipIndex;
            PngBytes = pngBytes;
            Extension = extension;
        }
    }

    private sealed class MaterialJsonPayload
    {
        public Dictionary<string, string>? Textures { get; init; }
        public CMaterialParams2? Parameters { get; init; }
    }
}
