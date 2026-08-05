using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse_Conversion.Options;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Drives the full "zero compromise" material payload for the GLB scene export.
//
// Two passes, both reading from the SAME GlbMaterialFactory in-memory cache so
// the lossless layer and the embedded layer are bit-coherent:
//
//   (1) Lossless sidecar pass : for every UNIQUE material the geometry build
//       cited, write a `<material-package-path>.json` carrying the material's
//       textures + CMaterialParams2, plus every referenced UTexture2D decoded
//       for every mip level under `<texture-package-path>.<ext>` /
//       `<texture-package-path>.mipN.<ext>`. The native texture decoder runs
//       serialized under a global lock with a per-PathName de-dup table —
//       upstream's parallel decode loop is too fragile at scene scale.
//
//   (2) Embedded PBR pass : open every `.glb` part written by GlbSceneContext
//       in `outputDirectory`, walk `ModelRoot.LogicalMaterials`, and for each
//       material whose `Name` matches a registered bundle wire the cached
//       PNG bytes onto the BaseColor / Normal / MetallicRoughness / Emissive
//       channels via `MaterialChannel.SetTexture`. Geometry stays untouched.
//
// Interop with CUE4Parse-Conversion:
//   * `ExportOptions` drives the MaterialDepth / TextureFormat /
//     TexturePlatform / ExportHdrTexturesAsHdr knobs.
//   * GlbMeshSectionBuilder propagates the material's short Name ->
//     `MaterialBuilder.Name` -> `Schema2.Material.Name`, which the embed pass
//     keys on.
public sealed class MaterialTextureWriter
{
    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    public MaterialTextureWriter(Action<string> log, Action<string> logError)
    {
        _log = log;
        _logError = logError;
    }

    // Drive both passes. `materials` is the GlbSceneContext.Materials list (one
    // entry per UNIQUE material — the context already de-duped at insert time).
    // `materialKeys` is the parallel PathName list. `outputDirectory` is the run
    // root; render-layer .glb parts live somewhere under it (the orchestrator
    // builds `<outputDir>/<map-package-path>...`), so the embed pass recursively
    // scans for `.glb` files.
    public void Write(
        IReadOnlyList<UMaterialInterface> materials,
        IReadOnlyList<string> materialKeys,
        ExportOptions options,
        string outputDirectory)
    {
        if (materials.Count == 0)
        {
            _log("[GlbScene]   material writer: no materials to write.");
            return;
        }

        var factory = new GlbMaterialFactory(_log, _logError);

        // (1) Decode + register every unique material into the in-memory cache.
        for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
        {
            string materialKey = materialIndex < materialKeys.Count
                ? materialKeys[materialIndex]
                : "(unknown)";
            _log($"[GlbScene]   material {materialIndex + 1}/{materials.Count}: {materialKey}");

            try
            {
                factory.RegisterMaterial(materials[materialIndex], options);
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
}
