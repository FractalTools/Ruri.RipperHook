using System;
using System.Collections.Generic;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Materials;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Drives the full "zero compromise" material payload for the GLB scene export.
//
// Two passes, both reading from the SAME GlbMaterialFactory in-memory cache so
// the lossless layer and the embedded layer are bit-coherent:
//
//   (1) Lossless sidecar pass : for every UNIQUE material the geometry build
//       cited, write a `<material-package-path>.json` matching the public
//       MaterialData layout (MaterialExporter2.cs:13-17) plus every referenced
//       UTexture2D decoded for every mip level under
//       `<texture-package-path>.<ext>` / `<texture-package-path>.mipN.<ext>`.
//       This pass replaces the original `materialExporter.TryWriteToDir` call
//       so the native texture decoder runs serialized under a global lock and
//       a per-PathName de-dup table is honoured — the upstream's inner
//       `Parallel.ForEach` decode loop is too fragile at scene scale.
//
//   (2) Embedded PBR pass : open every `.glb` part written by GlbSceneContext
//       in `outputDirectory`, walk `ModelRoot.LogicalMaterials`, and for each
//       material whose `Name` matches a registered bundle wire the cached
//       PNG bytes onto the BaseColor / Normal / MetallicRoughness / Emissive
//       channels via `MaterialChannel.SetTexture`. Geometry stays untouched.
//
// Ground truth references:
//   * CUE4Parse-Conversion/Materials/MaterialExporter2.cs:13-72 — shape of the
//     JSON sidecar + decode/encode call pattern this writer mirrors.
//   * CUE4Parse-Conversion/IExporter.cs:23-53 — `ExporterOptions` is the
//     options struct each `MaterialExporter2.Options` member carries; we read
//     it back to drive the same MaterialFormat / TextureFormat / Platform /
//     ExportHdrTexturesAsHdr knobs the original construction used.
//   * CUE4Parse-Conversion/Meshes/glTF/Gltf.cs:200-210 — `tex.Name` ->
//     `MaterialBuilder.Name` -> `Schema2.Material.Name` propagation that the
//     embed pass keys on.
public sealed class MaterialTextureWriter
{
    // Reflection handle for `MaterialExporter2._materialData`. The field is
    // private but its type `MaterialData` is public (Textures dict +
    // CMaterialParams2) — read-only introspection lets us walk the actual
    // UTexture2D references the exporter resolved at construction time
    // without re-running `GetParams` on a fresh material instance.
    //
    // Initialised at startup and cached for the lifetime of the writer.
    private static readonly FieldInfo MaterialExporter2DataField =
        typeof(MaterialExporter2).GetField("_materialData", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "MaterialExporter2._materialData field not found; CUE4Parse vendor copy must have moved — see MaterialExporter2.cs:22.");

    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    public MaterialTextureWriter(Action<string> log, Action<string> logError)
    {
        _log = log;
        _logError = logError;
    }

    // Drive both passes. `materialExporters` is the GlbSceneContext.MaterialExporters
    // list (one `MaterialExporter2` per UNIQUE material — the context already
    // de-duped at insert time). `materialKeys` is the parallel PathName list.
    // `outputDirectory` is the run root; render-layer .glb parts live somewhere
    // under it (the orchestrator builds `<outputDir>/<map-package-path>...`),
    // so the embed pass recursively scans for `.glb` files.
    public void Write(
        IReadOnlyList<MaterialExporter2> materialExporters,
        IReadOnlyList<string> materialKeys,
        string outputDirectory)
    {
        if (materialExporters.Count == 0)
        {
            _log("[GlbScene]   material writer: no materials to write.");
            return;
        }

        var factory = new GlbMaterialFactory(_log, _logError);

        // (1) Decode + register every unique material into the in-memory cache.
        for (int materialIndex = 0; materialIndex < materialExporters.Count; materialIndex++)
        {
            string materialKey = materialIndex < materialKeys.Count
                ? materialKeys[materialIndex]
                : "(unknown)";
            _log($"[GlbScene]   material {materialIndex + 1}/{materialExporters.Count}: {materialKey}");

            try
            {
                RegisterExporterIntoFactory(materialExporters[materialIndex], materialKey, factory);
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene]   material register failed ({materialKey}): {ex.Message}");
            }
        }

        // (2) Lossless sidecar pass : write JSON + all texture mips to disk.
        try
        {
            factory.WriteSidecars(outputDirectory);
            _log($"[GlbScene]   sidecar pass : {factory.Bundles.Count} materials, {factory.UniqueMaterialCount} unique pathnames.");
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   sidecar pass failed: {ex.Message}");
        }

        // (3) Embedded PBR pass : rebind every `.glb` part's materials so PBR
        // textures land inside the GLB binary. The render-layer .glb parts are
        // already on disk by the time this writer runs (WorldGlbExporter calls
        // `context.FlushBatch()` before invoking this method); a recursive
        // scan is safe because the orchestrator clears `outputDirectory` per
        // run (per project CLAUDE.md).
        try
        {
            int rebound = factory.EmbedIntoAllParts(outputDirectory);
            _log($"[GlbScene]   embed pass  : rebound PBR in {rebound} .glb file(s).");
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   embed pass failed: {ex.Message}");
        }
    }

    // Pull the actual `UTexture2D` references out of a `MaterialExporter2` and
    // hand them to the factory for decode/cache. Skips materials that came in
    // without an exporter or without any textures resolved (typical of a
    // material whose `GetParams` returned an empty parameter set).
    //
    // The `materialKey` parameter is the material's `GetPathName()` (the same
    // identity GlbSceneContext.BuildMesh used to de-dupe). It is passed
    // through to the factory so the bundle is keyed correctly even when the
    // exporter's reflected internal path uses a FixPath-mangled form.
    //
    // The bundle's short `Name` is recovered from the exporter's reflected
    // `_internalFilePath` last segment, matching what Gltf.cs:203 writes on
    // the SharpGLTF primitive.
    private void RegisterExporterIntoFactory(
        MaterialExporter2 exporter,
        string materialKey,
        GlbMaterialFactory factory)
    {
        MaterialData materialData = (MaterialData)MaterialExporter2DataField.GetValue(exporter)!;
        CMaterialParams2? parameters = materialData.Parameters;
        if (parameters == null)
        {
            _log($"[GlbScene]     '{materialKey}' has no parameters; skipped.");
            return;
        }

        // Pull the per-exporter ExporterOptions back (matches the call-site
        // construction in GlbSceneContext.BuildMesh: `new MaterialExporter2(tex, options)`).
        ExporterOptions options = exporter.Options;

        // Recover the internal mountpoint-relative path the exporter resolved
        // at construction (MaterialExporter2.cs:38-39). The field is private
        // but the type is public string — reuse the same reflection slot path.
        string internalFilePath = GetPrivateString(exporter, "_internalFilePath") ?? string.Empty;
        string materialShortName = SubstringAfterLastSeparator(internalFilePath);
        if (string.IsNullOrEmpty(materialShortName))
        {
            // Fallback : pull the last segment from the PathName.
            materialShortName = materialKey.SubstringAfterLast('.');
            if (materialShortName.Contains('/'))
            {
                materialShortName = materialShortName.SubstringAfterLast('/');
            }
        }

        factory.RegisterExporterMaterial(
            materialPathName: materialKey,
            materialName: materialShortName,
            materialInternalPath: internalFilePath,
            parameters: parameters,
            options: options);
    }

    private static string? GetPrivateString(MaterialExporter2 exporter, string fieldName)
    {
        FieldInfo? field = typeof(MaterialExporter2).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(exporter) as string;
    }

    private static string SubstringAfterLastSeparator(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int slashIndex = path.LastIndexOf('/');
        int backslashIndex = path.LastIndexOf('\\');
        int sep = Math.Max(slashIndex, backslashIndex);
        return sep < 0 ? path : path[(sep + 1)..];
    }
}
