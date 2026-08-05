using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.Game.SBUE;

// The export knobs every SBUE pass shares. ExportOptions' own defaults are
// tuned for FModel's GUI (UEFormat meshes, no Nanite, top-layer-only
// materials); the values pinned here are the ones this pipeline is built
// around, so they are stated rather than inherited.
public static class SbueExportOptions
{
    public static ExportOptions Create(EMeshFormat meshFormat, bool exportMaterials = true)
    {
        return new ExportOptions(
            meshFormat: meshFormat,
            naniteMeshFormat: ENaniteMeshFormat.NaniteOnly,
            meshQuality: EMeshQuality.Highest,
            materialDepth: EMaterialDepth.AllLayersNoRef,
            exportMaterials: exportMaterials);
    }
}
