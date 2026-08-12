using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

internal static class SceneCensus
{
    internal enum Family
    {
        InstanceComponents,        ComponentTemplate,        StaticMeshComponent,        SkeletalMesh,        Landscape,        Light,        Decal,        Unclassified,    }

    public static void Log(IReadOnlyList<WorldActor> actors, Action<string> log)
    {
        var exportTypeHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var familyHistogram = new Dictionary<Family, int>();
        var unclassifiedTypes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var placement in actors)
        {
            IPropertyHolder actor = placement.Actor;
            string exportType = (actor as UObject)?.ExportType ?? actor.GetType().Name;
            Increment(exportTypeHistogram, exportType);

            Family family = Classify(actor, exportType);
            familyHistogram.TryGetValue(family, out int familyCount);
            familyHistogram[family] = familyCount + 1;
            if (family == Family.Unclassified) Increment(unclassifiedTypes, exportType);
        }

        log($"[GlbScene][Census] {actors.Count} placements across {exportTypeHistogram.Count} distinct actor ExportType(s).");
        log("[GlbScene][Census] Renderable family tally (handled today = InstanceComponents/ComponentTemplate/StaticMeshComponent):");
        foreach (var (family, count) in familyHistogram.OrderByDescending(pair => pair.Value))
        {
            log($"[GlbScene][Census]   {family,-20} {count}");
        }

        log("[GlbScene][Census] Top actor ExportTypes:");
        foreach (var (exportType, count) in exportTypeHistogram.OrderByDescending(pair => pair.Value).Take(40))
        {
            log($"[GlbScene][Census]   {exportType,-44} {count}");
        }

        if (unclassifiedTypes.Count > 0)
        {
            log("[GlbScene][Census] Unclassified actor ExportTypes (no probed renderable marker — inspect these):");
            foreach (var (exportType, count) in unclassifiedTypes.OrderByDescending(pair => pair.Value).Take(40))
            {
                log($"[GlbScene][Census]   {exportType,-44} {count}");
            }
        }
    }

    public static void DumpSamples(IReadOnlyList<WorldActor> actors, Action<string> log, params string[] targetExportTypes)
    {
        var remaining = new HashSet<string>(targetExportTypes, StringComparer.Ordinal);
        foreach (var placement in actors)
        {
            if (placement.Actor is not UObject actor) continue;
            if (!remaining.Remove(actor.ExportType)) continue;

            log($"[GlbScene][Sample] === {actor.ExportType} '{actor.Name}' ({actor.Properties.Count} properties) ===");
            foreach (var property in actor.Properties)
            {
                string name = property.Name.Text;
                if (actor.TryGetValue(out FPackageIndex single, name) && single is { IsNull: false })
                {
                    log($"[GlbScene][Sample]    .{name} -> {DescribeComponent(single)}");
                }
                else if (actor.TryGetValue(out FPackageIndex[] array, name) && array is { Length: > 0 })
                {
                    log($"[GlbScene][Sample]    .{name}[{array.Length}] -> [{string.Join(", ", array.Take(8).Select(DescribeComponent))}]");
                }
                else
                {
                    log($"[GlbScene][Sample]    .{name} : {property.PropertyType}");
                }
            }
            if (remaining.Count == 0) break;
        }
    }

    private static string DescribeComponent(FPackageIndex index)
    {
        if (index is not { IsNull: false }) return "null";
        try
        {
            if (index.Load() is not { } loaded) return $"{index.Name}(unloaded)";
            string detail = loaded.ExportType;
            if (loaded.TryGetValue(out FPackageIndex staticMesh, "StaticMesh") && staticMesh is { IsNull: false })
                detail += $" StaticMesh={staticMesh.Name}";
            if (loaded.TryGetValue(out FPackageIndex skeletalMesh, "SkeletalMesh", "SkinnedAsset") && skeletalMesh is { IsNull: false })
                detail += $" SkeletalMesh={skeletalMesh.Name}";
            return detail;
        }
        catch (Exception ex)
        {
            return $"{index.Name}(load-failed: {ex.GetType().Name})";
        }
    }

    private static Family Classify(IPropertyHolder actor, string exportType)
    {
        if (actor.TryGetValue(out FPackageIndex[] _, "InstanceComponents"))
            return Family.InstanceComponents;
        if (actor.TryGetValue(out FPackageIndex _, "ComponentTemplate"))
            return Family.ComponentTemplate;
        if (actor.TryGetValue(out FPackageIndex _, "StaticMeshComponent", "StaticMesh", "Mesh", "LightMesh", "SplineMesh"))
            return Family.StaticMeshComponent;
        if (actor.TryGetValue(out FPackageIndex _, "SkeletalMeshComponent", "SkeletalMesh"))
            return Family.SkeletalMesh;
        if (exportType.Contains("Landscape", StringComparison.OrdinalIgnoreCase) ||
            actor.TryGetValue(out FPackageIndex[] _, "LandscapeComponents"))
            return Family.Landscape;
        if (exportType.EndsWith("Light", StringComparison.Ordinal) ||
            actor.TryGetValue(out FPackageIndex _, "LightComponent"))
            return Family.Light;
        if (exportType.Contains("Decal", StringComparison.OrdinalIgnoreCase) ||
            actor.TryGetValue(out FPackageIndex _, "DecalComponent"))
            return Family.Decal;
        return Family.Unclassified;
    }

    private static void Increment(Dictionary<string, int> histogram, string key)
    {
        histogram.TryGetValue(key, out int count);
        histogram[key] = count + 1;
    }
}
