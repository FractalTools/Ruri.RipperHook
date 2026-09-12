using System.Text;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// How the map's material is drawn -- blend, cull, depth -- stated as the shaderlab tags and pass
/// commands that mean the same. Read off the material the caller named, because an instance is
/// free to override what its template blends as.
/// </summary>
internal static class ShaderLabRenderState
{
    public static void Build(ShaderSourceState state)
    {
        int populated = 0;
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            if (map.Target.Material is not { } material)
            {
                continue;
            }

            ResolvedState resolved = Resolve(material);
            string tagsBlock = BuildSubShaderTags(resolved);
            string passCommands = BuildPassCommands(resolved);

            if (!string.IsNullOrEmpty(tagsBlock) || !string.IsNullOrEmpty(passCommands))
            {
                map.SubShaderTags = tagsBlock;
                map.PassCommands = passCommands;
                populated++;
            }
        }

        state.Log($"    RenderState: populated {populated}/{state.ShaderMaps.Count} shader-maps.");
    }

    private readonly struct ResolvedState
    {
        public readonly string BlendMode;
        public readonly string ShadingModel;
        public readonly string MaterialDomain;
        public readonly bool TwoSided;
        public readonly bool DisableDepthTest;
        public readonly bool DitheredLODTransition;

        public ResolvedState(string blendMode, string shadingModel, string materialDomain,
            bool twoSided, bool disableDepthTest, bool ditheredLODTransition)
        {
            BlendMode = blendMode;
            ShadingModel = shadingModel;
            MaterialDomain = materialDomain;
            TwoSided = twoSided;
            DisableDepthTest = disableDepthTest;
            DitheredLODTransition = ditheredLODTransition;
        }

        public bool EffectiveTwoSided => TwoSided || string.Equals(ShadingModel, "MSM_TwoSidedFoliage", StringComparison.Ordinal);
    }

    /// <summary>
    /// The material's drawing state: what it declares itself, and -- when it is an instance --
    /// what it overrides of its template's, falling back to the template for the properties an
    /// instance cannot override.
    /// </summary>
    private static ResolvedState Resolve(UMaterialInterface material)
    {
        string blendMode = "BLEND_Opaque";
        string shadingModel = "MSM_DefaultLit";
        bool twoSided = false;
        bool disableDepthTest = false;
        bool ditheredLODTransition = false;

        if (material is UMaterial declared)
        {
            blendMode = declared.BlendMode.ToString();
            shadingModel = declared.ShadingModel.ToString();
            twoSided = declared.TwoSided;
            disableDepthTest = declared.bDisableDepthTest;
        }

        if (material is UMaterialInstance instance)
        {
            if (instance.BasePropertyOverrides is { } overrides)
            {
                blendMode = overrides.BlendMode.ToString();
                shadingModel = overrides.ShadingModel.ToString();
                ditheredLODTransition = overrides.DitheredLODTransition;
            }
            if (instance.Parent is UMaterial template)
            {
                twoSided |= template.TwoSided;
                disableDepthTest |= template.bDisableDepthTest;
            }
        }

        string materialDomain = material.TryGetValue(out FName domain, "MaterialDomain") && !domain.IsNone
            ? domain.Text
            : "MD_Surface";
        if (!ditheredLODTransition && material.TryGetValue(out bool dithered, "DitheredLODTransition"))
        {
            ditheredLODTransition = dithered;
        }

        return new ResolvedState(
            blendMode: NormaliseEnumLiteral(blendMode) ?? "BLEND_Opaque",
            shadingModel: NormaliseEnumLiteral(shadingModel) ?? "MSM_DefaultLit",
            materialDomain: NormaliseEnumLiteral(materialDomain) ?? "MD_Surface",
            twoSided: twoSided,
            disableDepthTest: disableDepthTest,
            ditheredLODTransition: ditheredLODTransition);
    }

    private static string BuildSubShaderTags(ResolvedState rs)
    {
        (string renderType, string queue) = MapDomainToTags(rs.MaterialDomain, rs.BlendMode);

        StringBuilder sb = new();
        sb.AppendLine("Tags {");
        sb.AppendLine($"    \"RenderType\"=\"{renderType}\"");
        sb.AppendLine($"    \"Queue\"=\"{queue}\"");

        if (string.Equals(rs.MaterialDomain, "MD_UI", StringComparison.Ordinal))
        {
            sb.AppendLine("    \"IgnoreProjector\"=\"True\"");
            sb.AppendLine("    \"PreviewType\"=\"Plane\"");
        }
        else if (string.Equals(rs.MaterialDomain, "MD_DeferredDecal", StringComparison.Ordinal))
        {
            sb.AppendLine("    \"ForceNoShadowCasting\"=\"True\"");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildPassCommands(ResolvedState rs)
    {
        StringBuilder sb = new();

        if (rs.EffectiveTwoSided)
        {
            sb.AppendLine("Cull Off");
        }

        switch (rs.MaterialDomain)
        {
            case "MD_PostProcess":
                if (!rs.EffectiveTwoSided) sb.AppendLine("Cull Off");
                sb.AppendLine("ZTest Always");
                sb.AppendLine("ZWrite Off");
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_UI":
                if (!rs.EffectiveTwoSided) sb.AppendLine("Cull Off");
                sb.AppendLine("ZTest Always");
                sb.AppendLine("ZWrite Off");
                sb.AppendLine("Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha");
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_LightFunction":
                sb.AppendLine("ZTest Always");
                sb.AppendLine("ZWrite Off");
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_DeferredDecal":
                sb.AppendLine("Cull Front");
                sb.AppendLine("ZTest GEqual");
                sb.AppendLine("ZWrite Off");
                EmitBlend(sb, rs);
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_Volume":
                sb.AppendLine("Cull Front");
                sb.AppendLine("ZWrite Off");
                EmitBlend(sb, rs);
                return sb.ToString().TrimEnd('\r', '\n');
        }

        EmitBlend(sb, rs);

        if (IsTranslucentFamily(rs.BlendMode))
        {
            sb.AppendLine("ZWrite Off");
        }

        if (rs.DisableDepthTest)
        {
            sb.AppendLine("ZTest Always");
        }

        if (string.Equals(rs.BlendMode, "BLEND_Masked", StringComparison.Ordinal) && rs.DitheredLODTransition)
        {
            sb.AppendLine("AlphaToMask On");
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static void EmitBlend(StringBuilder sb, ResolvedState rs)
    {
        switch (rs.BlendMode)
        {
            case "BLEND_Translucent":
            case "BLEND_TranslucentColoredTransmittance":
                sb.AppendLine("Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha");
                break;
            case "BLEND_Additive":
                sb.AppendLine("Blend One One, One One");
                break;
            case "BLEND_Modulate":
                sb.AppendLine("Blend DstColor Zero, Zero One");
                break;
            case "BLEND_AlphaComposite":
                sb.AppendLine("Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha");
                break;
            case "BLEND_AlphaHoldout":
                sb.AppendLine("Blend Zero OneMinusSrcAlpha, Zero OneMinusSrcAlpha");
                sb.AppendLine("ColorMask A");
                break;
        }
    }

    private static (string RenderType, string Queue) MapDomainToTags(string materialDomain, string blendMode)
    {
        switch (materialDomain)
        {
            case "MD_DeferredDecal":
                return ("Decal", "Geometry+225");
            case "MD_LightFunction":
                return ("LightFunction", "Overlay");
            case "MD_Volume":
                return ("Volume", "Transparent");
            case "MD_PostProcess":
                return ("Overlay", "Overlay");
            case "MD_UI":
                return ("Transparent", "Overlay");
            case "MD_RuntimeVirtualTexture":
                return ("Opaque", "Geometry");
        }

        return blendMode switch
        {
            "BLEND_Masked" => ("TransparentCutout", "AlphaTest"),
            "BLEND_Translucent" or
            "BLEND_TranslucentColoredTransmittance" or
            "BLEND_Additive" or
            "BLEND_Modulate" or
            "BLEND_AlphaComposite" or
            "BLEND_AlphaHoldout" => ("Transparent", "Transparent"),
            _ => ("Opaque", "Geometry"),
        };
    }

    private static bool IsTranslucentFamily(string blendMode) => blendMode switch
    {
        "BLEND_Translucent" or
        "BLEND_TranslucentColoredTransmittance" or
        "BLEND_Additive" or
        "BLEND_Modulate" or
        "BLEND_AlphaComposite" or
        "BLEND_AlphaHoldout" => true,
        _ => false,
    };

    private static string? NormaliseEnumLiteral(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        int sep = raw.IndexOf("::", StringComparison.Ordinal);
        return sep >= 0 ? raw[(sep + 2)..] : raw;
    }
}
