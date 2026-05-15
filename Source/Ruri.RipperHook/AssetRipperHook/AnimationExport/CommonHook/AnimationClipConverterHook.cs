using AssetRipper.Checksum;
using AssetRipper.Processing.AnimationClips;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_108;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_120;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_198;
using AssetRipper.SourceGenerated.Classes.ClassID_20;
using AssetRipper.SourceGenerated.Classes.ClassID_2083052967;
using AssetRipper.SourceGenerated.Classes.ClassID_224;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_330;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_96;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.AnimationClip.GenericBinding;
using System.Reflection;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Minimal-diff parity hooks for AR's animation export pipeline so .anim YAML
/// output matches VibeStudio (AssetStudio) byte-for-byte modulo float-precision
/// noise. We don't replace <c>AnimationClipConverter.Process</c> — AR's
/// streamed/dense/constant algorithm is already correct once the game-side
/// hook has backfilled compressed ACL data into <c>DenseClip.m_SampleArray</c>
/// (for AKEF: <see cref="Ruri.RipperHook.Endfield.EndFieldCommon_Hook"/>).
/// The two surviving divergences are surgical:
///
///  1. <see cref="GetReversedPath"/> — AR formats unresolved CRC32 hashes as
///     <c>path_0x&lt;HEX&gt;_&lt;reverse-ascii&gt;</c>; VibeStudio uses
///     <c>path_&lt;decimal&gt;</c>. The same helper backs MissedPropertyPrefix
///     / ScriptPropertyPrefix / TypeTreePropertyPrefix so one replacement
///     normalizes all four prefixes.
///
///  2. <see cref="ToAttributeName"/> — AR's <c>RendererMaterial</c> /
///     <c>BlendShape</c> fallback embeds the path string when the path can't
///     be resolved, producing <c>material.&lt;path&gt;</c>. Two bindings on the
///     same unresolved path with different material-property hashes collide
///     into one CurveData (path, attribute, classID, script) tuple and one
///     of the FloatCurves silently drops. VibeStudio falls back to
///     <c>material.&lt;decimal-attribute&gt;</c>, so we re-implement those two
///     cases; the rest of <c>ToAttributeName</c>'s behaviour is preserved by
///     forwarding to AR's resolver helpers (FieldHashes / Roots / etc.) so
///     this stays a thin override, not a fork.
/// </summary>
public partial class AR_AnimationExport_Hook
{
    /// <summary>
    /// Replace <c>AnimationClipConverter.GetReversedPath</c> so all unresolved
    /// CRC32 hashes (path / missed property / script property / typetree
    /// property) serialize as <c>"&lt;prefix&gt;&lt;decimal-hash&gt;"</c> —
    /// AssetStudio / VibeStudio convention. Original used
    /// <c>Crc32Algorithm.ReverseAscii(hash, $"{prefix}0x{hash:X}_")</c> which
    /// appends 6 ASCII chars that reverse-hash to the same CRC32; useful for
    /// AR-only debugging but visible in the YAML output, so it diverges from
    /// VibeStudio byte-for-byte.
    /// </summary>
    [RetargetMethod(typeof(AnimationClipConverter), "GetReversedPath", isBefore: true, isReturn: true)]
    public static string GetReversedPath(string prefix, uint hash)
    {
        return prefix + hash;
    }

    /// <summary>
    /// Replace <c>CustomCurveResolver.ToAttributeName</c>. Only the
    /// <c>RendererMaterial</c> / <c>BlendShape</c> fallback strings differ
    /// from AR (we swap path-embedded fallbacks for attribute-hash form);
    /// every other <c>BindingCustomType</c> case is identical to AR's
    /// upstream method body, modulo using the decimal hash format (matching
    /// hook #1) for the typed-fallback strings that some cases emit.
    /// </summary>
    [RetargetMethod(typeof(CustomCurveResolver), nameof(CustomCurveResolver.ToAttributeName), isBefore: true, isReturn: true)]
    public static string ToAttributeName(CustomCurveResolver self, BindingCustomType type, uint attribute, string path)
    {
        switch (type)
        {
            case BindingCustomType.BlendShape:
                {
                    const string Prefix = "blendShape.";
                    // VibeStudio fallback: attribute hash, not path.
                    if (IsUnresolvedPath(path)) return Prefix + attribute;
                    foreach (IGameObject root in GetRoots(self))
                    {
                        ITransform rootTransform = root.GetTransform();
                        ITransform? child = rootTransform.FindChild(path);
                        if (child == null) continue;
                        ISkinnedMeshRenderer? skin = child.GameObject_C4P?.TryGetComponent<ISkinnedMeshRenderer>();
                        if (skin == null) continue;
                        IMesh? mesh = skin.MeshP;
                        if (mesh == null) continue;
                        string? shapeName = mesh.FindBlendShapeNameByCRC(attribute);
                        if (shapeName == null) continue;
                        return Prefix + shapeName;
                    }
                    // VibeStudio fallback: attribute hash (AR used path-form).
                    return Prefix + attribute;
                }

            case BindingCustomType.Renderer:
                return "m_Materials.Array.data[" + attribute + "]";

            case BindingCustomType.RendererMaterial:
                {
                    const string Prefix = "material.";
                    if (IsUnresolvedPath(path)) return Prefix + attribute;
                    foreach (IGameObject root in GetRoots(self))
                    {
                        ITransform rootTransform = root.GetTransform();
                        ITransform? child = rootTransform.FindChild(path);
                        if (child == null) continue;

                        uint crc28 = attribute & 0xFFFFFFF;
                        IRenderer? renderer = child.GameObject_C4P?.TryGetComponent<IRenderer>();
                        if (renderer == null) continue;
                        string? property = renderer.FindMaterialPropertyNameByCRC28(crc28);
                        if (property == null) continue;

                        if ((attribute & 0x80000000) != 0) return Prefix + property;

                        uint subPropIndex = (attribute >> 28) & 3;
                        bool isRgba = (attribute & 0x40000000) != 0;
                        char subProperty = subPropIndex switch
                        {
                            0 => isRgba ? 'r' : 'x',
                            1 => isRgba ? 'g' : 'y',
                            2 => isRgba ? 'b' : 'z',
                            _ => isRgba ? 'a' : 'w',
                        };
                        return Prefix + property + "." + subProperty;
                    }
                    return Prefix + attribute;
                }

            case BindingCustomType.SpriteRenderer:
                if (attribute == 0) return "m_Sprite";
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.MonoBehaviour:
                if (MonoBehaviour.TryGetPath(attribute, out string? mbPath)) return mbPath;
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.Light:
                if (Light.TryGetPath(attribute, out string? lightPath)) return lightPath;
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.RendererShadows:
                if (Renderer.TryGetPath(attribute, out string? rsPath)) return rsPath;
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.ParticleSystem:
                if (ParticleSystem.TryGetPath(attribute, out string? psPath)) return psPath;
                // VibeStudio: ParticleSystem_<decimal>. AR used ReverseAscii.
                return "ParticleSystem_" + attribute;

            case BindingCustomType.RectTransform:
                if (RectTransform.TryGetPath(attribute, out string? rtPath)) return rtPath;
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.LineRenderer:
                if (LineRenderer.TryGetPath(attribute, out string? lrPath)) return lrPath;
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.TrailRenderer:
                if (TrailRenderer.TryGetPath(attribute, out string? trPath)) return trPath;
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.PositionConstraint:
                {
                    uint property = attribute & 0xF;
                    return property switch
                    {
                        0 => "m_RestTranslation.x",
                        1 => "m_RestTranslation.y",
                        2 => "m_RestTranslation.z",
                        3 => "m_Weight",
                        4 => "m_TranslationOffset.x",
                        5 => "m_TranslationOffset.y",
                        6 => "m_TranslationOffset.z",
                        7 => "m_AffectTranslationX",
                        8 => "m_AffectTranslationY",
                        9 => "m_AffectTranslationZ",
                        10 => "m_Active",
                        11 => $"m_Sources.Array.data[{attribute >> 8}].sourceTransform",
                        12 => $"m_Sources.Array.data[{attribute >> 8}].weight",
                        _ => throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}"),
                    };
                }

            case BindingCustomType.RotationConstraint:
                {
                    uint property = attribute & 0xF;
                    return property switch
                    {
                        0 => "m_RestRotation.x",
                        1 => "m_RestRotation.y",
                        2 => "m_RestRotation.z",
                        3 => "m_Weight",
                        4 => "m_RotationOffset.x",
                        5 => "m_RotationOffset.y",
                        6 => "m_RotationOffset.z",
                        7 => "m_AffectRotationX",
                        8 => "m_AffectRotationY",
                        9 => "m_AffectRotationZ",
                        10 => "m_Active",
                        11 => $"m_Sources.Array.data[{attribute >> 8}].sourceTransform",
                        12 => $"m_Sources.Array.data[{attribute >> 8}].weight",
                        _ => throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}"),
                    };
                }

            case BindingCustomType.ScaleConstraint:
                {
                    uint property = attribute & 0xF;
                    return property switch
                    {
                        0 => "m_ScaleAtRest.x",
                        1 => "m_ScaleAtRest.y",
                        2 => "m_ScaleAtRest.z",
                        3 => "m_Weight",
                        4 => "m_ScalingOffset.x",
                        5 => "m_ScalingOffset.y",
                        6 => "m_ScalingOffset.z",
                        7 => "m_AffectScalingX",
                        8 => "m_AffectScalingY",
                        9 => "m_AffectScalingZ",
                        10 => "m_Active",
                        11 => $"m_Sources.Array.data[{attribute >> 8}].sourceTransform",
                        12 => $"m_Sources.Array.data[{attribute >> 8}].weight",
                        _ => throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}"),
                    };
                }

            case BindingCustomType.AimConstraint:
                {
                    uint property = attribute & 0xF;
                    return property switch
                    {
                        0 => "m_Weight",
                        1 => "m_AffectRotationX",
                        2 => "m_AffectRotationY",
                        3 => "m_AffectRotationZ",
                        4 => "m_Active",
                        5 => "m_WorldUpObject",
                        6 => $"m_Sources.Array.data[{attribute >> 8}].sourceTransform",
                        7 => $"m_Sources.Array.data[{attribute >> 8}].weight",
                        _ => throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}"),
                    };
                }

            case BindingCustomType.ParentConstraint:
                {
                    uint property = attribute & 0xF;
                    return property switch
                    {
                        0 => "m_Weight",
                        1 => "m_AffectTranslationX",
                        2 => "m_AffectTranslationY",
                        3 => "m_AffectTranslationZ",
                        4 => "m_AffectRotationX",
                        5 => "m_AffectRotationY",
                        6 => "m_AffectRotationZ",
                        7 => "m_Active",
                        8 => $"m_TranslationOffsets.Array.data[{attribute >> 8}].x",
                        9 => $"m_TranslationOffsets.Array.data[{attribute >> 8}].y",
                        10 => $"m_TranslationOffsets.Array.data[{attribute >> 8}].z",
                        11 => $"m_RotationOffsets.Array.data[{attribute >> 8}].x",
                        12 => $"m_RotationOffsets.Array.data[{attribute >> 8}].y",
                        13 => $"m_RotationOffsets.Array.data[{attribute >> 8}].z",
                        14 => $"m_Sources.Array.data[{attribute >> 8}].sourceTransform",
                        15 => $"m_Sources.Array.data[{attribute >> 8}].weight",
                        _ => throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}"),
                    };
                }

            case BindingCustomType.LookAtConstraint:
                {
                    uint property = attribute & 0xF;
                    return property switch
                    {
                        0 => "m_Weight",
                        1 => "m_Active",
                        2 => "m_WorldUpObject",
                        3 => $"m_Sources.Array.data[{attribute >> 8}].sourceTransform",
                        4 => $"m_Sources.Array.data[{attribute >> 8}].weight",
                        5 => "m_Roll",
                        _ => throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}"),
                    };
                }

            case BindingCustomType.Camera:
                if (Camera.TryGetPath(attribute, out string? cameraPath)) return cameraPath;
                throw new ArgumentException($"Unknown attribute 0x{attribute:X} ({attribute}) for {type}");

            case BindingCustomType.VisualEffect:
                if (VisualEffect.TryGetPath(attribute, out string? vfxPath)) return vfxPath;
                return "VisualEffect_" + attribute;

            case BindingCustomType.ParticleForceField:
                if (ParticleSystemForceField.TryGetPath(attribute, out string? pffPath)) return pffPath;
                return "ParticleForceField_" + attribute;

            case BindingCustomType.UserDefined:
                return "UserDefined_" + attribute;

            case BindingCustomType.MeshFilter:
                if (MeshFilter.TryGetPath(attribute, out string? mfPath)) return mfPath;
                return "MeshFilter_" + attribute;

            default:
                throw new ArgumentException($"Binding type {type} not implemented", nameof(type));
        }
    }

    private static bool IsUnresolvedPath(string path)
    {
        // Matches VibeStudio "path_<decimal>" *and* AR's legacy
        // "path_0x<HEX>_<6chars>" — checking the prefix is enough since real
        // bone paths never start with "path_".
        return path.StartsWith("path_");
    }

    /// <summary>
    /// Pull the lazy-initialized <c>Roots</c> array off the resolver via
    /// reflection. AR exposes it as a private property with a backing field;
    /// we don't want to duplicate the discovery (it calls
    /// <c>IAnimationClip.FindRoots()</c> which walks the asset graph), so we
    /// just read it. The field name is the auto-generated backing for
    /// <c>Roots { field-keyword }</c>; if AR renames it we fall back to
    /// walking properties.
    /// </summary>
    private static IGameObject[] GetRoots(CustomCurveResolver resolver)
    {
        if (s_rootsField == null)
        {
            // C# 13 `field` keyword auto-generates `<Roots>k__BackingField`.
            s_rootsField = typeof(CustomCurveResolver).GetField("<Roots>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (s_rootsField == null)
            {
                // Fall back to invoking the property getter — this triggers the
                // lazy init the same way AR's original code did.
                PropertyInfo? prop = typeof(CustomCurveResolver).GetProperty("Roots",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                {
                    return (IGameObject[])prop.GetValue(resolver)!;
                }
                return Array.Empty<IGameObject>();
            }
        }

        var current = (IGameObject[]?)s_rootsField.GetValue(resolver);
        if (current != null) return current;

        // Backing field not initialized yet — trigger the lazy init via the
        // property accessor, then re-read.
        PropertyInfo? rootsProp = typeof(CustomCurveResolver).GetProperty("Roots",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (rootsProp != null)
        {
            return (IGameObject[])rootsProp.GetValue(resolver)!;
        }
        return Array.Empty<IGameObject>();
    }

    private static FieldInfo? s_rootsField;
}
