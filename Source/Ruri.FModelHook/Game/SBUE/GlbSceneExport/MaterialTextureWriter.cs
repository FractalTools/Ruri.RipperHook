using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse_Conversion.Options;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class MaterialTextureWriter
{
    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    public MaterialTextureWriter(Action<string> log, Action<string> logError)
    {
        _log = log;
        _logError = logError;
    }

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

        try
        {
            factory.WriteSidecars(outputDirectory);
            _log($"[GlbScene]   sidecar pass : {factory.Bundles.Count} materials, {factory.UniqueMaterialCount} unique pathnames.");
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene]   sidecar pass failed: {ex.Message}");
        }

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
