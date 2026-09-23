using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_108;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_20;
using AssetRipper.SourceGenerated.Classes.ClassID_205;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_23;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.LOD;
using AssetRipper.SourceGenerated.Subclasses.LODRenderer;

namespace Ruri.RipperHook.Statements;

/// <summary>Which renderers a hierarchy actually draws, with what mesh and what materials.
///
/// A renderer listed under LOD1+ and never under the wanted level is a lower-detail
/// duplicate, keyed on the LODGroup's own lists and never on a name. A ShadowsOnly renderer
/// exists purely to cast a shadow. A disabled renderer or inactive object draws nothing right
/// now but is routinely a runtime-toggled variant, so it is kept and said so unless the
/// request excludes it. Static batching gives a renderer a window into a shared mesh, and the
/// window travels with it. Skinned renderers come first, then mesh renderers, in file order.</summary>
public sealed record UnityRendererInfo
{
    public required IRenderer Renderer { get; init; }

    public required bool Skinned { get; init; }

    public required UnityNode? Node { get; init; }

    public required string Name { get; init; }

    public IMesh? Mesh { get; init; }

    public required IReadOnlyList<IMaterial?> Materials { get; init; }

    public required IReadOnlyList<ITransform?> Bones { get; init; }

    public (int First, int Count)? SubMeshWindow { get; init; }

    public required bool Disabled { get; init; }
}

public sealed class UnityCameraInfo
{
    public required UnityNode Node { get; init; }

    public required string Name { get; init; }

    public required string Tag { get; init; }

    public required float FieldOfView { get; init; }

    public required float Near { get; init; }

    public required float Far { get; init; }

    public required bool Orthographic { get; init; }

    public required float OrthographicSize { get; init; }

    public required bool Disabled { get; init; }
}

/// <summary>One light as the engine emits it. The colour is LINEAR -- the component serializes it
/// gamma-encoded and the engine lights with its linear value -- and the intensity is the component's
/// own, so a host scales the colour by the intensity in its own units. Angles are full cone angles in
/// degrees.</summary>
public sealed class UnityLightInfo
{
    public required UnityNode Node { get; init; }

    public required string Name { get; init; }

    public required int Type { get; init; }

    public required float Red { get; init; }

    public required float Green { get; init; }

    public required float Blue { get; init; }

    public required float Intensity { get; init; }

    public required float Range { get; init; }

    public required float SpotAngle { get; init; }

    public required float InnerSpotAngle { get; init; }

    public required float AreaWidth { get; init; }

    public required float AreaHeight { get; init; }

    public required bool Shadows { get; init; }

    public required bool Disabled { get; init; }
}

public sealed class UnityRendererFilters
{
    public const int EveryLevel = -1;

    public int DetailLevel { get; init; }

    public bool ShadowProxies { get; init; }

    public bool Inactive { get; init; } = true;
}

public sealed class UnityRendererSkips
{
    public int Lod { get; set; }

    public int Shadow { get; set; }

    public int Inactive { get; set; }

    public int InactiveIncluded { get; set; }
}

public static class UnityRenderers
{
    /// <summary>Renderers that are NOT at the wanted detail level, read off the LODGroups in
    /// scope. A renderer listed at the wanted level stays even when also listed at others; a
    /// group not authoring the wanted level contributes its nearest one; a renderer under no
    /// group is never discarded.</summary>
    public static HashSet<IRenderer> LodDiscardSet(IEnumerable<ILODGroup> groups, int level)
    {
        HashSet<IRenderer> keep = new(ReferenceEqualityComparer.Instance);
        HashSet<IRenderer> discard = new(ReferenceEqualityComparer.Instance);
        if (level == UnityRendererFilters.EveryLevel)
        {
            return discard;
        }
        foreach (ILODGroup group in groups)
        {
            int count = group.LODs.Count;
            if (count == 0)
            {
                continue;
            }
            int wanted = 0;
            (int Distance, int Index) best = (int.MaxValue, int.MaxValue);
            for (int index = 0; index < count; index++)
            {
                (int Distance, int Index) candidate = (Math.Abs(index - level), index);
                if (candidate.CompareTo(best) < 0)
                {
                    best = candidate;
                    wanted = index;
                }
            }
            for (int index = 0; index < count; index++)
            {
                ILOD lod = group.LODs[index];
                foreach (ILODRenderer entry in lod.Renderers)
                {
                    if (entry.Renderer.TryGetAsset(group.Collection) is not IRenderer renderer)
                    {
                        continue;
                    }
                    (index == wanted ? keep : discard).Add(renderer);
                }
            }
        }
        discard.ExceptWith(keep);
        return discard;
    }

    public static IEnumerable<UnityRendererInfo> Renderers(IEnumerable<ISkinnedMeshRenderer> skinned,
        IEnumerable<IMeshRenderer> meshRenderers, UnityHierarchy hierarchy, UnityRendererFilters filters,
        UnityRendererSkips skips, Func<IRenderer, string, bool> atLevel)
    {
        foreach (ISkinnedMeshRenderer renderer in skinned)
        {
            IGameObject? gameObject = renderer.GameObject_C25P;
            UnityNode? node = hierarchy.Of(gameObject);
            string name = gameObject?.Name ?? "Object";
            bool? disabled = Accept(renderer, node, name, filters, skips, atLevel);
            if (disabled is null)
            {
                continue;
            }
            yield return new UnityRendererInfo
            {
                Renderer = renderer,
                Skinned = true,
                Node = node,
                Name = name,
                Mesh = renderer.MeshP,
                Materials = renderer.Materials_C25P.ToArray(),
                Bones = renderer.BonesP.ToArray(),
                SubMeshWindow = StaticBatchWindow(renderer),
                Disabled = disabled.Value,
            };
        }
        foreach (IMeshRenderer renderer in meshRenderers)
        {
            IGameObject? gameObject = renderer.GameObject_C25P;
            UnityNode? node = hierarchy.Of(gameObject);
            string name = gameObject?.Name ?? "Object";
            bool? disabled = Accept(renderer, node, name, filters, skips, atLevel);
            if (disabled is null || node is null)
            {
                continue;
            }
            yield return new UnityRendererInfo
            {
                Renderer = renderer,
                Skinned = false,
                Node = node,
                Name = name,
                Mesh = gameObject?.TryGetComponent<IMeshFilter>()?.MeshP,
                Materials = renderer.Materials_C25P.ToArray(),
                Bones = [],
                SubMeshWindow = StaticBatchWindow(renderer),
                Disabled = disabled.Value,
            };
        }
    }

    private static bool? Accept(IRenderer renderer, UnityNode? node, string name, UnityRendererFilters filters,
        UnityRendererSkips skips, Func<IRenderer, string, bool> atLevel)
    {
        if (!atLevel(renderer, name))
        {
            skips.Lod++;
            return null;
        }
        if (!filters.ShadowProxies && renderer.GetShadowCastingMode() == ShadowCastingMode.ShadowsOnly)
        {
            skips.Shadow++;
            return null;
        }
        bool disabled = !renderer.Enabled_C25 || (node is not null && !node.ActiveInHierarchy);
        if (disabled)
        {
            if (filters.Inactive)
            {
                skips.InactiveIncluded++;
            }
            else
            {
                skips.Inactive++;
                return null;
            }
        }
        return disabled;
    }

    private static (int First, int Count)? StaticBatchWindow(IRenderer renderer)
    {
        if (!renderer.Has_StaticBatchInfo_C25())
        {
            return null;
        }
        int count = renderer.StaticBatchInfo_C25.SubMeshCount;
        return count <= 0 ? null : (renderer.StaticBatchInfo_C25.FirstSubMesh, count);
    }

    public static IEnumerable<UnityCameraInfo> Cameras(IEnumerable<ICamera> cameras,
        IEnumerable<IMonoBehaviour> behaviours, UnityHierarchy hierarchy, UnityRendererFilters filters)
    {
        foreach (ICamera camera in cameras)
        {
            (UnityNode? node, bool disabled, bool skip) = ContentNode(camera.GameObject_C20P, camera.Enabled_C20 != 0, hierarchy, filters);
            if (node is null || skip)
            {
                continue;
            }
            yield return new UnityCameraInfo
            {
                Node = node,
                Name = node.Name,
                Tag = node.GameObject?.TagString ?? string.Empty,
                FieldOfView = camera.Field_of_view_C20,
                Near = camera.Near_clip_plane_C20,
                Far = camera.Far_clip_plane_C20,
                Orthographic = camera.Orthographic_C20,
                OrthographicSize = camera.Orthographic_size_C20,
                Disabled = disabled,
            };
        }
        foreach (IMonoBehaviour behaviour in behaviours)
        {
            SerializableStructure? structure = behaviour.Structure is UnloadedStructure unloaded
                ? unloaded.LoadStructure()
                : behaviour.Structure as SerializableStructure;
            if (structure is null || !structure.TryGetField("m_Lens", out SerializableValue lens)
                || lens.CValue is not SerializableStructure block)
            {
                continue;
            }
            (UnityNode? node, bool disabled, bool skip) = ContentNode(behaviour.GameObjectP, behaviour.Enabled != 0, hierarchy, filters);
            if (node is null || skip)
            {
                continue;
            }
            yield return new UnityCameraInfo
            {
                Node = node,
                Name = node.Name,
                Tag = node.GameObject?.TagString ?? string.Empty,
                FieldOfView = Single(block, "FieldOfView", 60f),
                Near = Single(block, "NearClipPlane", 0.3f),
                Far = Single(block, "FarClipPlane", 1000f),
                Orthographic = Boolean(block, "Orthographic"),
                OrthographicSize = Single(block, "OrthographicSize", 5f),
                Disabled = disabled,
            };
        }
    }

    private static float Single(SerializableStructure block, string field, float fallback) =>
        block.TryGetField(field, out SerializableValue value) ? value.AsSingle : fallback;

    private static bool Boolean(SerializableStructure block, string field) =>
        block.TryGetField(field, out SerializableValue value) && value.AsBoolean;

    public static IEnumerable<UnityLightInfo> Lights(IEnumerable<ILight> lights, UnityHierarchy hierarchy,
        UnityRendererFilters filters)
    {
        foreach (ILight light in lights)
        {
            (UnityNode? node, bool disabled, bool skip) = ContentNode(light.GameObjectP, light.Enabled != 0, hierarchy, filters);
            if (node is null || skip)
            {
                continue;
            }
            yield return new UnityLightInfo
            {
                Node = node,
                Name = node.Name,
                Type = (int)light.Type,
                Red = SrgbColor.Decode(light.Color.R),
                Green = SrgbColor.Decode(light.Color.G),
                Blue = SrgbColor.Decode(light.Color.B),
                Intensity = light.Intensity,
                Range = light.Range,
                SpotAngle = light.SpotAngle,
                InnerSpotAngle = light.Has_InnerSpotAngle() ? light.InnerSpotAngle : 0f,
                AreaWidth = light.AreaSize.X,
                AreaHeight = light.AreaSize.Y,
                Shadows = light.Shadows.Type != 0,
                Disabled = disabled,
            };
        }
    }

    private static (UnityNode? Node, bool Disabled, bool Skip) ContentNode(IGameObject? gameObject, bool enabled,
        UnityHierarchy hierarchy, UnityRendererFilters filters)
    {
        UnityNode? node = hierarchy.Of(gameObject);
        if (node is null)
        {
            return (null, false, false);
        }
        bool disabled = !enabled || !node.ActiveInHierarchy;
        return (node, disabled, disabled && !filters.Inactive);
    }
}
