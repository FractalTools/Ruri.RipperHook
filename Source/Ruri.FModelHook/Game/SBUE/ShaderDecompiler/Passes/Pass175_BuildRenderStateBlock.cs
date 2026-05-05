using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 175 — Translates the material's UE render state UProperties into
// equivalent Unity ShaderLab Tags + per-Pass commands.
//
// The translation is mechanical, not annotative: every UE state value maps
// to the closest ShaderLab construct that produces the same on-screen
// behaviour. We do NOT emit UE.* debug tags — downstream tools (Unity
// importers, FX-graph editors) ignore unknown tags but choke when expected
// Unity tags carry the wrong value, so the output stays in the standard
// Unity vocabulary.
//
// Source-of-truth references (UE 5.2 / 5.1):
//
//   EBlendMode
//     Engine/Source/Runtime/Engine/Public/MaterialShared.h
//     drives Blend / ZWrite / Queue / RenderType.
//
//   EMaterialShadingModel
//     Engine/Source/Runtime/Engine/Public/MaterialShared.h
//     MSM_TwoSidedFoliage forces double-sided rasterisation regardless of
//     the per-material TwoSided flag (see UMaterial::IsTwoSided() override
//     in Materials/Material.cpp), so the shading-model gate widens TwoSided.
//
//   EMaterialDomain
//     Engine/Source/Runtime/Engine/Classes/Engine/EngineTypes.h
//     PostProcess / UI / LightFunction / Decal each pin a different
//     pipeline-stage state set; we mirror the closest-equivalent Unity
//     fullscreen / overlay / decal setup.
//
// Output style mirrors typical Unity ShaderLab:
//   * Two-arg `Blend SrcRGB DstRGB, SrcA DstA` form for explicitness.
//   * No comments inside Tags / Pass blocks.
//   * Defaults (Cull Back, ZWrite On, ZTest LEqual, Blend Off,
//     ColorMask RGBA) are not emitted — letting the ShaderLab parser
//     supply them keeps the file compact and compositionally correct.
//
// Out of scope (closed-world ceiling, see UE_TEXTURE_BINDING_TRUTH.md):
//   * Per-pass stencil ref/mask values from FRHIDepthStencilStateInitializer.
//   * Custom RT format / blend factors set in C++ at PSO setup.
internal static class Pass175_BuildRenderStateBlock
{
    public static void DoPass(PipelineState state)
    {
        if (state.UnifiedMaterialReader == null)
        {
            state.Log("    RenderState: skipped (no UnifiedMaterialReader).");
            return;
        }

        int populated = 0;
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            if (!TryResolveRenderState(state, map, out JsonElement renderState))
            {
                continue;
            }

            ResolvedState resolved = Resolve(renderState);
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

    // First non-null TryGetRenderState across the map's assets wins. Sibling
    // instances of the same parent share render state (overrides folded in
    // by Pass020), so any of them yields the same translated output.
    private static bool TryResolveRenderState(PipelineState state, ShaderMapInfo map, out JsonElement renderState)
    {
        renderState = default;
        foreach (string asset in map.Assets)
        {
            JsonElement? candidate = state.UnifiedMaterialReader!.TryGetRenderState(asset);
            if (candidate.HasValue)
            {
                renderState = candidate.Value;
                return true;
            }
        }
        return false;
    }

    // Bag of the four UE state values that drive ShaderLab output, normalised
    // to the bare enum literal (no "EnumType::" prefix CUE4Parse adds when the
    // UProperty was explicitly serialised).
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

        // MSM_TwoSidedFoliage acts as a forced two-sided override at runtime
        // (UMaterial::IsTwoSided()), so widen the gate even when bTwoSided=false.
        public bool EffectiveTwoSided => TwoSided || string.Equals(ShadingModel, "MSM_TwoSidedFoliage", StringComparison.Ordinal);
    }

    private static ResolvedState Resolve(JsonElement rs)
    {
        return new ResolvedState(
            blendMode: NormaliseEnumLiteral(ReadString(rs, "BlendMode")) ?? "BLEND_Opaque",
            shadingModel: NormaliseEnumLiteral(ReadString(rs, "ShadingModel")) ?? "MSM_DefaultLit",
            materialDomain: NormaliseEnumLiteral(ReadString(rs, "MaterialDomain")) ?? "MD_Surface",
            twoSided: ReadBool(rs, "TwoSided"),
            disableDepthTest: ReadBool(rs, "DisableDepthTest"),
            ditheredLODTransition: ReadBool(rs, "DitheredLODTransition"));
    }

    private static string BuildSubShaderTags(ResolvedState rs)
    {
        (string renderType, string queue) = MapDomainToTags(rs.MaterialDomain, rs.BlendMode);

        StringBuilder sb = new();
        sb.AppendLine("Tags {");
        sb.AppendLine($"    \"RenderType\"=\"{renderType}\"");
        sb.AppendLine($"    \"Queue\"=\"{queue}\"");

        // UI / Overlay materials want Unity's projector / batching opt-outs;
        // these are common boilerplate Unity authors expect to see for an
        // overlay-queue Pass.
        if (string.Equals(rs.MaterialDomain, "MD_UI", StringComparison.Ordinal))
        {
            sb.AppendLine("    \"IgnoreProjector\"=\"True\"");
            sb.AppendLine("    \"PreviewType\"=\"Plane\"");
        }
        else if (string.Equals(rs.MaterialDomain, "MD_DeferredDecal", StringComparison.Ordinal))
        {
            // Decals project onto opaque geometry — ForceNoShadowCasting
            // matches the Unity convention for projector-style passes.
            sb.AppendLine("    \"ForceNoShadowCasting\"=\"True\"");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildPassCommands(ResolvedState rs)
    {
        StringBuilder sb = new();

        // Cull — TwoSided OR TwoSidedFoliage shading model forces Off.
        // Otherwise the ShaderLab default (Cull Back) applies; emit nothing.
        if (rs.EffectiveTwoSided)
        {
            sb.AppendLine("Cull Off");
        }

        // Domain-specific overrides come first; these pin a fullscreen /
        // overlay / decal state set that doesn't depend on BlendMode.
        switch (rs.MaterialDomain)
        {
            case "MD_PostProcess":
                // Fullscreen blit-style pass: no Z, no cull, no blend.
                if (!rs.EffectiveTwoSided) sb.AppendLine("Cull Off");
                sb.AppendLine("ZTest Always");
                sb.AppendLine("ZWrite Off");
                // Blend defaults to Off — output overwrites destination.
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_UI":
                // Standard UI quad pass: alpha blend, no cull, no Z.
                if (!rs.EffectiveTwoSided) sb.AppendLine("Cull Off");
                sb.AppendLine("ZTest Always");
                sb.AppendLine("ZWrite Off");
                sb.AppendLine("Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha");
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_LightFunction":
                // Light functions modulate light contribution; UE renders them
                // as deferred-light volumes. Closest ShaderLab equivalent is
                // a back-faces fullscreen-style pass.
                sb.AppendLine("ZTest Always");
                sb.AppendLine("ZWrite Off");
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_DeferredDecal":
                // Decals project onto opaque geometry; cull front to render
                // the back faces of the projection volume, no Z write.
                sb.AppendLine("Cull Front");
                sb.AppendLine("ZTest GEqual");
                sb.AppendLine("ZWrite Off");
                EmitBlend(sb, rs);
                return sb.ToString().TrimEnd('\r', '\n');

            case "MD_Volume":
                // Volume shaders render back faces with no depth write so the
                // volume is sampled along the view ray.
                sb.AppendLine("Cull Front");
                sb.AppendLine("ZWrite Off");
                EmitBlend(sb, rs);
                return sb.ToString().TrimEnd('\r', '\n');
        }

        // Surface domain (and unknown domains): drive everything off BlendMode.
        EmitBlend(sb, rs);

        // ZWrite — Opaque/Masked write depth; translucent family doesn't.
        if (IsTranslucentFamily(rs.BlendMode))
        {
            sb.AppendLine("ZWrite Off");
        }

        // ZTest — bDisableDepthTest forces Always; otherwise the default
        // LEqual applies and we emit nothing.
        if (rs.DisableDepthTest)
        {
            sb.AppendLine("ZTest Always");
        }

        // Masked + DitheredLODTransition triggers Unity's AlphaToMask (UE
        // uses MSAA-aware alpha-to-coverage when available).
        if (string.Equals(rs.BlendMode, "BLEND_Masked", StringComparison.Ordinal) && rs.DitheredLODTransition)
        {
            sb.AppendLine("AlphaToMask On");
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    // Writes the Blend command for a Surface-domain BlendMode. Two-arg form
    // (RGB + alpha) is used uniformly so the alpha behaviour is always
    // explicit. Opaque / Masked don't emit a Blend line — ShaderLab's default
    // is `Blend Off` which matches both.
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
            // Opaque / Masked → no Blend (ShaderLab default Off is correct).
        }
    }

    private static (string RenderType, string Queue) MapDomainToTags(string materialDomain, string blendMode)
    {
        switch (materialDomain)
        {
            case "MD_DeferredDecal":
                // Decals are queued before transparent in URP/HDRP terms.
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

        // Surface domain — map by BlendMode.
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

    // Strips the "EnumTypeName::" qualifier CUE4Parse adds when a UProperty
    // enum value was explicitly serialised. Returns the bare literal
    // ("MD_PostProcess") regardless of input form ("EMaterialDomain::MD_PostProcess"
    // or just "MD_PostProcess"). Returns null/empty unchanged.
    private static string? NormaliseEnumLiteral(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        int sep = raw.IndexOf("::", StringComparison.Ordinal);
        return sep >= 0 ? raw[(sep + 2)..] : raw;
    }

    private static string? ReadString(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool ReadBool(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(property, out JsonElement value)) return false;
        return value.ValueKind == JsonValueKind.True;
    }
}
