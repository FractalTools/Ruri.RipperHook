using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Material-to-glTF translation surface.
//
// "Zero compromise" contract:
//   (1) Lossless sidecar : every unique material gets a `<material-package-
//       path>.json` matching MaterialExporter2's MaterialData layout (Textures
//       dict + CMaterialParams2 parameter tree), plus every referenced
//       UTexture2D decoded for EVERY mip level (not just mip 0 as FModel does)
//       and written under the texture's package-path-mirrored location.
//   (2) Embedded PBR : every `.glb` part written by GlbSceneContext gets a
//       post-write rebind that walks `ModelRoot.LogicalMaterials` and feeds
//       each material's `BaseColor` / `Normal` / `MetallicRoughness` /
//       `Emissive` / `Occlusion` channels from the in-memory PNG byte cache
//       built during (1). Geometry bytes stay untouched — only the materials
//       table at the end of the GLB binary header is mutated then re-saved.
//
// Ground truth references:
//   * CUE4Parse-Conversion/Materials/MaterialExporter2.cs:35-46   GetParams +
//     PathName extraction flow that this class mirrors at register time.
//   * CUE4Parse-Conversion/Materials/MaterialExporter2.cs:49-72   sidecar JSON
//     + per-texture decode/encode loop; we reuse the same MaterialData shape
//     and same package-path-mirroring helper.
//   * CUE4Parse/UE4/Assets/Exports/Material/CMaterialParams2.cs:46-134   role-
//     name tables Diffuse[0]/Normals[0]/SpecularMasks[0]/Emissive[0] used to
//     pick the right texture for each PBR channel.
//   * CUE4Parse-Conversion/Meshes/glTF/Gltf.cs:200-210   the `material.Name`
//     binding (Gltf writes `UMaterialInterface.Name`); we use the same key on
//     the embed pass so the rebind hits the right material.
//
// Threading contract:
//   The native texture decoder (BC/ASTC/...) is process-global, with hot paths
//   that allocate native buffers; on this scene scale (~thousands of textures)
//   the safest thing is a single global lock around every `texture.Decode` +
//   `bitmap.Encode` call. Registration runs single-threaded (driven by the
//   serial MaterialTextureWriter outer loop), but the lock is kept as a defence
//   in depth so any future caller can register concurrently without crashing
//   into the native side.
public sealed class GlbMaterialFactory
{
    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    // Set of material PathNames seen so far. The legacy `RegisterUnique` API
    // surface — kept verbatim because GlbSceneContext.BuildMesh's de-dup uses
    // the same identity (material.GetPathName()).
    private readonly HashSet<string> _registeredMaterialPathNames = new(StringComparer.Ordinal);

    // Bundles keyed by material PathName. One bundle per unique material.
    private readonly Dictionary<string, MaterialEmbedBundle> _bundlesByPathName = new(StringComparer.Ordinal);

    // Bundles keyed by `UMaterialInterface.Name` for the embed-time rebind.
    // The Gltf exporter writes `material.Name` (NOT PathName) onto the
    // SharpGLTF MaterialBuilder, so the rebind has to look up by Name. Name
    // collisions across packages are possible — first registration wins and
    // a warning is logged so the audit trail captures the lossy edge.
    private readonly Dictionary<string, string> _firstPathNameByMaterialName = new(StringComparer.Ordinal);

    // Decoded mip-0 bytes per UTexture2D PathName, so a texture shared across
    // many materials is decoded ONCE no matter how many bundles cite it. The
    // value carries both the encoded byte payload and the chosen extension
    // (PNG/JPEG/TGA/HDR depending on ExporterOptions.TextureFormat +
    // ExportHdrTexturesAsHdr).
    private readonly Dictionary<string, DecodedTextureMip0> _decodedMip0ByTexturePathName = new(StringComparer.Ordinal);

    // Tracks which textures have already had their extra mips (1..N) decoded
    // so a texture cited by N materials decodes its mip chain exactly once.
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

    // Single global lock around the native decode/encode pipeline. Kept on
    // the factory rather than on individual bundles so concurrent registrants
    // see the same lock.
    private readonly object _decodeLock = new();

    public GlbMaterialFactory(Action<string> log, Action<string> logError)
    {
        _log = log;
        _logError = logError;
    }

    // First-time-seen check for a material PathName. GlbSceneContext.BuildMesh
    // already calls this kind of de-dup internally (it tracks
    // `_writtenMaterialKeys`), but the public API stays for orchestrator code
    // that wants to know whether a registration is fresh.
    //
    // Source: matches MaterialExporter2.cs:38-46 — material identity is the
    // package PathName, the same key the geometry-build de-dup uses.
    public bool RegisterUnique(UMaterialInterface? material)
    {
        if (material is null) return false;
        string pathName = material.GetPathName();
        return _registeredMaterialPathNames.Add(pathName);
    }

    public int UniqueMaterialCount => _registeredMaterialPathNames.Count;

    public IReadOnlyCollection<string> RegisteredMaterialPathNames => _registeredMaterialPathNames;

    // Full registration: decode the material's parameters, decode each
    // referenced texture for every mip, cache the PNG bytes, build the embed
    // bundle. Returns the freshly built bundle (or the previously cached one
    // if this material was already registered).
    //
    // Source path mirrored: MaterialExporter2 constructor at MaterialExporter2.cs:35-46
    //   * `unrealMaterial.GetParams(_materialData.Parameters, Options.MaterialFormat)`
    //   * `_materialData.Textures[key] = value.GetPathName()`
    // We do the same on a private CMaterialParams2 so the JSON sidecar shape
    // matches FModel byte-for-byte while still letting us reach the real
    // UTexture2D references for decode.
    public MaterialEmbedBundle? RegisterMaterial(UMaterialInterface? material, ExporterOptions options)
    {
        if (material is null) return null;

        string pathName = material.GetPathName();
        if (_bundlesByPathName.TryGetValue(pathName, out var existing))
        {
            _registeredMaterialPathNames.Add(pathName);
            return existing;
        }

        // Compute the same mountpoint-relative path MaterialExporter2.cs:38-39
        // produces, so the JSON sidecar lands at the FModel-compatible location.
        string ownerName = material.Owner?.Name ?? material.Name;
        string materialInternalPath = (material.Owner?.Provider?.FixPath(ownerName) ?? material.Name).SubstringBeforeLast('.');

        var parameters = new CMaterialParams2();
        try
        {
            material.GetParams(parameters, options.MaterialFormat);
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   GetParams failed for material '{pathName}': {ex.Message}");
        }

        // Snapshot the textures dict role-key -> PathName (matches
        // MaterialExporter2.cs:42-45 layout exactly so the JSON sidecar
        // bytes are byte-for-byte equivalent on the Textures field).
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

        // Decode every referenced texture for every mip level. The native
        // decode is serialized under the global lock and each texture is
        // PathName-deduped globally so a shared texture is decoded once.
        foreach (var pair in parameters.Textures)
        {
            string textureRoleKey = pair.Key;
            if (pair.Value is not UTexture2D texture) continue;
            DecodeAndCacheAllMips(texture, options, bundle, textureRoleKey);
        }

        _bundlesByPathName[pathName] = bundle;
        _registeredMaterialPathNames.Add(pathName);

        // Track first PathName per short Name so embed-time lookups by name
        // are deterministic. A second registration that collides on Name
        // (different package, same simple Name) keeps the first registration
        // and logs a warning — neither is "wrong" for embed and dropping the
        // second silently would hide a possible upstream issue.
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

    // MaterialTextureWriter entry point: register a material whose
    // CMaterialParams2 has already been resolved upstream (the
    // GlbSceneContext.BuildMesh -> MaterialExporter2 chain). This is the
    // common case in production — `GlbSceneContext` constructs a
    // MaterialExporter2 per section, then the writer reflects on its
    // `_materialData` to recover the textures + parameters.
    //
    // The caller provides everything the factory needs without forcing it to
    // resolve the material a second time. The texture decode + mip walk run
    // here under the global lock with per-PathName de-dup, identical to the
    // RegisterMaterial path that takes a fresh UMaterialInterface.
    public MaterialEmbedBundle? RegisterExporterMaterial(
        string materialPathName,
        string materialName,
        string materialInternalPath,
        CMaterialParams2 parameters,
        ExporterOptions options)
    {
        if (_bundlesByPathName.TryGetValue(materialPathName, out var existing))
        {
            _registeredMaterialPathNames.Add(materialPathName);
            return existing;
        }

        // Mirror the same Textures-PathName snapshot MaterialExporter2.cs:42-45
        // builds so the JSON sidecar matches FModel byte-for-byte.
        var textureNamesByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters.Textures)
        {
            textureNamesByKey[pair.Key] = pair.Value.GetPathName();
        }

        var bundle = new MaterialEmbedBundle(
            materialPathName: materialPathName,
            materialName: materialName,
            materialInternalPath: materialInternalPath,
            parameters: parameters,
            textureNamesByKey: textureNamesByKey);

        foreach (var pair in parameters.Textures)
        {
            string roleKey = pair.Key;
            if (pair.Value is not UTexture2D texture) continue;
            DecodeAndCacheAllMips(texture, options, bundle, roleKey);
        }

        _bundlesByPathName[materialPathName] = bundle;
        _registeredMaterialPathNames.Add(materialPathName);

        if (!_firstPathNameByMaterialName.ContainsKey(materialName))
        {
            _firstPathNameByMaterialName[materialName] = materialPathName;
        }
        else if (!string.Equals(_firstPathNameByMaterialName[materialName], materialPathName, StringComparison.Ordinal))
        {
            _log($"[GlbScene]   material name collision: '{materialName}' has both '{_firstPathNameByMaterialName[materialName]}' and '{materialPathName}' — first wins on embed.");
        }

        return bundle;
    }

    // Try to grab the cached embed bundle whose source `UMaterialInterface.Name`
    // matches the SharpGLTF primitive's `material.Name`. This is the look-up
    // the post-write embed pass uses.
    //
    // Source: Gltf.cs:203-210 writes `tex.Name` (the UMaterialInterface short
    // name) onto MaterialBuilder.Name, which round-trips through ToGltf2()
    // into Schema2.Material.Name.
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

    // Walk every .glb file in a directory tree and, for each one, rebind its
    // Schema2 materials' channel textures from the cached PNG bytes. Returns
    // the number of files visited.
    //
    // The walk respects the user's "scan output dir" contract: WorldGlbExporter
    // writes parts as `<outputDir>/<map-package-path>.partNNN.glb` (or just
    // `<map-package-path>.glb` for single-part maps); the run is clean (the
    // user clears the output dir per run), so a recursive scan is safe.
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

    // Decode every mip of a single UTexture2D under the global lock, encode
    // each into the configured format (PNG by default — matches FModel), and
    // cache mip-0 bytes for the embed pass plus all mips for the sidecar.
    //
    // De-dup: `_decodedPngByTexturePathName` keys on texture PathName so a
    // texture shared by ten materials is decoded once.
    //
    // Ground truth: MaterialExporter2.cs:58-69 single-mip decode is the
    // upstream's mip-0 path. We extend to "all mips" per the user's "zero
    // compromise / every byte" requirement; mip-0 byte-equivalence with FModel
    // is preserved because we feed `texture.GetFirstMipIndex()` as the mip-0
    // index (same call used internally by `t.Decode(platform)`).
    private void DecodeAndCacheAllMips(UTexture2D texture, ExporterOptions options, MaterialEmbedBundle bundle, string roleKey)
    {
        string texturePathName = texture.GetPathName();

        // Log BEFORE the native decode so a hard crash inside the codec
        // pinpoints the offending texture in the run log.
        _log($"[GlbScene]   decode texture: {texturePathName} (role={roleKey})");

        // mip-0 byte cache is the source of truth for both embed AND sidecar
        // mip-0 file. Build it once under the lock and reuse for both.
        // Check + insert happen under the SAME lock so concurrent registrants
        // cannot both miss the cache and both decode the same texture twice.
        DecodedTextureMip0 mip0;
        try
        {
            lock (_decodeLock)
            {
                if (!_decodedMip0ByTexturePathName.TryGetValue(texturePathName, out mip0))
                {
                    CTexture? bitmap = texture.Decode(options.Platform);
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

        // Always record the (roleKey -> mip-0 file) entry on this bundle so
        // the sidecar pass writes the file under THIS material's neighbourhood
        // AND the embed pass can pick the right PBR channel by role.
        string textureInternalPath = (texture.Owner?.Provider?.FixPath(texture.Owner.Name) ?? texture.Name).SubstringBeforeLast('.');
        bundle.RecordTextureFile(roleKey, textureInternalPath, mip0.Extension, mip0.Bytes);

        // Mips 1..N — for the lossless sidecar only. Embed always uses mip-0.
        // The texture's internal path is the parent of both mip-0 file and the
        // extra mips: a downstream tool walking `<textureInternalPath>.mipN.<ext>`
        // can reconstruct the full mip chain.
        //
        // Per-texture de-dup: the FIRST material to register a given texture
        // owns its extra mips (so the bundle's sidecar pass writes them); a
        // later material that shares the texture skips the decode AND the
        // record because the disk file is already there from the first owner.
        // The de-dup decision happens under the same global lock so concurrent
        // registrants do not both reserve the texture for their own bundle.
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
                    var bitmap = texture.DecodeMip(mipIndex, options.Platform);
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

    // Embed cached PBR images into a single written .glb file. The geometry
    // bytes are untouched; only the materials table is rebound and the file is
    // re-serialized.
    //
    // Implementation choice: we mutate Schema2 materials directly via
    // `MaterialChannel.SetTexture(int, Image)`, where `Image` is built by
    // `Toolkit.UseImageWithContent(root, new MemoryImage(pngBytes))`. This
    // path was verified against the SharpGLTF 1.0.0-alpha0023 surface area
    // shipped with this repo. The MaterialBuilder.WithChannelImage path
    // documented in the foundation header is the build-time analogue — it is
    // unreachable here because the MaterialBuilder is constructed inside
    // `Gltf.ExportStaticMeshSections` (Gltf.cs:209-210) which we cannot
    // intercept without modifying GlbSceneContext / the ground truth Gltf.cs.
    //
    // Falls back gracefully: a material with no bundle keeps its base-color-
    // only MaterialBuilder output unchanged.
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
            // Stream overload streams the GLB chunks directly into the file
            // without the intermediate ArraySegment buffer the no-arg overload
            // returns. Same call shape GlbSceneContext.WriteSceneTo uses.
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

    // Map UE PBR roles -> glTF KnownChannel, then call `MaterialChannel.SetTexture`
    // with a fresh Image built from the cached PNG bytes. Roles use the
    // CMaterialParams2 well-known role-key tables so a UE material whose
    // texture parameter is literally named "BaseColor" / "Diffuse" / "Albedo" /
    // ... all bind to the BaseColor glTF channel.
    //
    // Source: CMaterialParams2.cs:46-134 — Diffuse[0]/Normals[0]/SpecularMasks[0]/
    // Emissive[0] arrays drive the role detection.
    private static void ApplyBundleToMaterial(ModelRoot model, Material schemaMaterial, MaterialEmbedBundle bundle)
    {
        // BaseColor : Diffuse[0] role tables, falling back to PM_Diffuse — the
        // sentinel CMaterialParams2.VerifyTexture (CMaterialParams2.cs:326-343)
        // writes when a texture's param name regex-matches but is not in the
        // Diffuse[0] explicit candidate list. Without the fallback try, a
        // cooked material whose only diffuse-role entry lives under "PM_Diffuse"
        // would silently lose its embed binding even though the bytes are
        // already decoded into the bundle's cache.
        if (bundle.TryGetPngForRoleSet(CMaterialParams2.Diffuse[0], out var baseColorPng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackDiffuse, out baseColorPng))
        {
            BindChannel(model, schemaMaterial, "BaseColor", baseColorPng);
        }
        else if (bundle.Parameters.TryGetLinearColor(out var baseColor, "BaseColor", "DiffuseColor", "Color"))
        {
            // MaterialChannel is a `readonly struct` whose Parameter setter writes
            // through to the parented Material; assignment to `nullable.Value.Parameter`
            // (CS1612) requires a named local — the struct's payload still refers to
            // the shared Material so the write propagates correctly.
            var channelNullable = schemaMaterial.FindChannel("BaseColor");
            if (channelNullable.HasValue)
            {
                var channel = channelNullable.Value;
                channel.Parameter = new Vector4(baseColor.R, baseColor.G, baseColor.B, baseColor.A);
            }
        }

        // Normal : Normals[0] + PM_Normals fallback (same rationale).
        if (bundle.TryGetPngForRoleSet(CMaterialParams2.Normals[0], out var normalPng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackNormals, out normalPng))
        {
            BindChannel(model, schemaMaterial, "Normal", normalPng);
        }

        // MetallicRoughness : SpecularMasks[0] + PM_SpecularMasks fallback.
        if (bundle.TryGetPngForRoleSet(CMaterialParams2.SpecularMasks[0], out var metallicRoughnessPng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackSpecularMasks, out metallicRoughnessPng))
        {
            BindChannel(model, schemaMaterial, "MetallicRoughness", metallicRoughnessPng);
        }

        // Emissive : Emissive[0] + PM_Emissive fallback.
        if (bundle.TryGetPngForRoleSet(CMaterialParams2.Emissive[0], out var emissivePng)
            || bundle.TryGetPngForFallback(CMaterialParams2.FallbackEmissive, out emissivePng))
        {
            BindChannel(model, schemaMaterial, "Emissive", emissivePng);
        }
        else if (bundle.Parameters.TryGetLinearColor(out var emissiveColor, "Emissive", "EmissiveColor"))
        {
            // Same CS1612 pattern as the BaseColor branch above.
            var emissiveChannelNullable = schemaMaterial.FindChannel("Emissive");
            if (emissiveChannelNullable.HasValue)
            {
                var emissiveChannel = emissiveChannelNullable.Value;
                emissiveChannel.Parameter = new Vector4(emissiveColor.R, emissiveColor.G, emissiveColor.B, 1f);
            }
        }

        // Alpha mode rebind: CMaterialParams2.cs:35 `BlendMode` defaults to
        // BLEND_Opaque but cooked translucent / masked materials carry the
        // BLEND_Translucent / BLEND_Masked value. Map to glTF AlphaMode so
        // the embedded material faithfully reflects the engine's intent.
        // EBlendMode enum values from EBlendMode.cs:8-21.
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

        // glTF supports PNG / JPG / WebP / DDS / KTX2. The MemoryImage ctor
        // throws ArgumentException for anything else — that includes
        // HDR (RGBE radiance, written when the upstream chose
        // ExportHdrTexturesAsHdr) and TGA. For unsupported formats we
        // SKIP the embed silently and let the channel default Parameter
        // (white tint) drive the visual; the HDR/TGA bytes still land
        // in the lossless sidecar so a consumer can pick them up out of
        // band.
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

        // SharpGLTF dedups identical MemoryImage content (verified against
        // 1.0.0-alpha0023 — `UseImageWithContent` returns the same Image
        // when called repeatedly with the same byte sequence), so this is
        // O(1) extra GLB Image entries per unique texture across all
        // materials in the part — exactly what we want for a scene with
        // many materials reusing the same base/normal/etc texture.
        Image image = model.UseImageWithContent(memoryImage);
        // TEXCOORD index 0 = the primary UV set; matches Gltf.cs:262-267
        // which puts the section's primary UV in slot 0.
        channel.Value.SetTexture(0, image);
    }

    // Write every material's sidecar JSON + every texture mip to disk under
    // `outputDirectory`, mirroring the package-path layout
    // MaterialExporter2.TryWriteToDir produces (MaterialExporter2.cs:54-69).
    //
    // The JSON content matches the public `MaterialData` struct shape exactly —
    // the Textures dict (role-key -> texture PathName) plus the full
    // CMaterialParams2 instance — so consumers expecting the FModel-shape JSON
    // round-trip without change.
    //
    // Per-file write errors inside `WriteSidecarTo` are surfaced through the
    // bundle's error sink so "every byte" completeness can be audited from the
    // run log — a swallowed write means a texture mip never landed on disk and
    // the user must see the audit signal, not silently lose bytes.
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

// Per-material cached payload: parameter tree, texture role -> PathName map,
// and decoded PNG bytes per role plus extra-mip byte payloads.
//
// Layout choices:
//   * `_pngByRoleKey` maps UE role-key ("BaseColor"/"Diffuse"/"Normal"/...) to
//     the mip-0 PNG bytes. The embed pass picks the right entry by matching
//     against `CMaterialParams2.Diffuse[0]` / `Normals[0]` / ... arrays so a
//     material whose author named the param "Diffuse" still binds to glTF's
//     BaseColor channel.
//   * `_textureFilesByRoleKey` records the disk-target path + extension for
//     each role's mip-0 file so `WriteSidecarTo` can lay it out at the same
//     package-path-mirrored location MaterialExporter2 uses.
//   * `_extraMipFiles` collects mips 1..N — sidecar-only, never embedded.
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

    // Walk role-key candidates (e.g. CMaterialParams2.Diffuse[0]) and return
    // the first matching PNG. Mirrors the look-up shape `CMaterialParams2.
    // TryGetTexture2d(out texture, params string[] names)` uses (cited above).
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

    // Try a single fallback sentinel key (e.g. CMaterialParams2.FallbackDiffuse
    // = "PM_Diffuse"). VerifyTexture (CMaterialParams2.cs:326-343) writes the
    // texture under BOTH its original param name AND the matching PM_* fallback
    // when the param-name regex matches; if the original name was not in the
    // explicit Diffuse[0]/Normals[0]/... role list, the PM_* key is the only
    // way to reach the role binding for the embed pass.
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

    // Write the JSON sidecar + every cached texture mip to disk under
    // `baseDirectory`. JSON path & texture paths follow MaterialExporter2.cs:
    //  * JSON   : `<baseDirectory>/<materialInternalPath>.json`        line 54-56
    //  * mip 0  : `<baseDirectory>/<textureInternalPath>.<ext>`        line 65
    //  * mip N  : `<baseDirectory>/<textureInternalPath>.mip<N>.<ext>` (new)
    public void WriteSidecarTo(DirectoryInfo baseDirectory, Action<string>? perFileErrorSink = null)
    {
        // Serialise via the public MaterialData shape (Textures dict +
        // CMaterialParams2) so the bytes match what MaterialExporter2 would
        // have written for this material.
        var materialData = new MaterialData
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
                // Surface per-file write errors so the audit trail captures
                // any "byte that did not land on disk" — required to honour
                // the "every byte, zero compromise" delivery contract.
                perFileErrorSink?.Invoke($"[GlbScene]   sidecar texture write '{texturePath}' failed: {ex.Message}");
            }
        }

        foreach (var mip in _extraMipFiles)
        {
            // Extra mips land beside the texture's mip-0 file: stem is the
            // texture's internal path with a `.mipN` suffix so a downstream
            // tool can re-stitch the chain by walking the mip-suffix series.
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

    // Re-implementation of ExporterBase.FixAndCreatePath (IExporter.cs:103-109)
    // so this bundle is independent of CUE4Parse's protected member surface.
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
}
