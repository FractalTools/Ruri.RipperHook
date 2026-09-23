using AssetRipper.Import.Logging;
using CUE4Parse.UE4.Assets.Exports.Material;
using Ruri.FModelHook.ShaderDecompiler.Semantics;

namespace Ruri.FModelHook.BlenderBridge.Readers;

/// <summary>
/// A material interface resolved the way the engine resolves it: the base material of its
/// parent chain states every parameter and its default, each instance down the chain overrides
/// by name, and the compiled base pass says which slot feeds which part of the surface.
///
/// The names below are the vocabulary the resolved set is stated in -- one shared spelling of
/// "blend mode", "shading model", "two sided" and "opacity mask clip value", so a consumer never
/// has to know they came from Unreal.
/// </summary>
public static class UnrealMaterial
{
    public const string BlendModeName = "BlendMode";
    public const string ShadingModelName = "ShadingModel";
    public const string TwoSidedName = "TwoSided";
    public const string OpacityMaskClipValueName = "OpacityMaskClipValue";

    /// <summary>
    /// The parameter set a material interface resolves to. This is the ONE reading of a
    /// material, so nothing that consumes one can drift from anything else that does.
    /// </summary>
    internal static UnrealMaterialParameters Resolve(UnrealFileProvider provider, MaterialSemanticsResolver? resolver,
        UMaterialInterface source, List<UMaterialInterface>? chain = null)
    {
        UnrealMaterialParameters parameters = new(provider);
        bool rooted = false;
        foreach (UMaterialInterface layer in chain ?? Chain(source))
        {
            switch (layer)
            {
                case UMaterial baseMaterial:
                    parameters.ReadRoot(baseMaterial);
                    rooted = true;
                    break;
                case UMaterialInstance instance:
                    parameters.ReadInstance(instance);
                    break;
            }
        }
        if (!rooted)
        {
            Logger.Warning(LogCategory.Import,
                $"[Unreal] {source.GetPathName()}: its parent chain reaches no base material, so this build states no "
                + "surface for it -- the package its chain ends at is not in this install.");
        }
        if (resolver is not null && resolver.Resolve(source) is { } semantics)
        {
            if (semantics.IsResolved)
            {
                parameters.Apply(semantics);
            }
            else
            {
                Logger.Verbose(LogCategory.Import, $"[Unreal] {source.GetPathName()}: material semantics not read: {semantics.Status}");
            }
        }
        return parameters;
    }

    /// <summary>The parent chain from the base material down to <paramref name="source"/>.</summary>
    internal static List<UMaterialInterface> Chain(UMaterialInterface source)
    {
        List<UMaterialInterface> chain = new();
        HashSet<UUnrealMaterial> seen = new(ReferenceEqualityComparer.Instance);
        UUnrealMaterial? cursor = source;
        while (cursor is UMaterialInterface layer && seen.Add(layer))
        {
            chain.Add(layer);
            cursor = layer is UMaterialInstance instance ? instance.Parent : null;
        }
        chain.Reverse();
        return chain;
    }
}
