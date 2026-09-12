using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.Utils;

public static class UnrealExportOptions
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
