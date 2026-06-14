using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Top-level "what landed in this scene package" report.
//
// One per --export-map-direct run, written as `scene-manifest.json` next to
// the .glb output, listing:
//
//   * Source map package path + EGame version.
//   * Render layer: number of placements, unique meshes, written .glb part
//     files, materials emitted.
//   * Lossless layer: actor / component / property counts written under
//     Actors/.
//   * Closure layer: asset counts written under Assets/.
//   * Dropped: ANY actor / component / asset the pipeline saw but failed to
//     export. The user requirement is `dropped == 0`; the manifest is the
//     audit trail that proves it.
//
// FOUNDATION (this revision): everything is filled today EXCEPT the
// per-actor / per-component counts that the deferred CompleteSceneDataExporter
// and DependencyClosureExporter will increment. They expose simple counter
// accessors so the cell only has to call `manifest.RecordActor(...)` /
// `manifest.RecordAsset(...)` from inside their existing loops.
public sealed class SceneManifest
{
    public string SourceMapPackagePath { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    public RenderLayerCounts Render { get; } = new();
    public LosslessLayerCounts Lossless { get; } = new();
    public ClosureLayerCounts Closure { get; } = new();
    public DroppedCounts Dropped { get; } = new();

    public List<string> Notes { get; } = new();

    public void RecordActor(string exportType)
    {
        Lossless.ActorCount++;
        Lossless.ActorsByExportType.TryGetValue(exportType, out int existing);
        Lossless.ActorsByExportType[exportType] = existing + 1;
    }

    public void RecordComponent(string exportType)
    {
        Lossless.ComponentCount++;
        Lossless.ComponentsByExportType.TryGetValue(exportType, out int existing);
        Lossless.ComponentsByExportType[exportType] = existing + 1;
    }

    public void RecordAsset(string assetPackagePath)
    {
        Closure.AssetCount++;
        Closure.AssetPackagePaths.Add(assetPackagePath);
    }

    public void RecordDroppedActor(string reason)
    {
        Dropped.Actors++;
        Dropped.Reasons.Add(reason);
    }

    public void RecordDroppedComponent(string reason)
    {
        Dropped.Components++;
        Dropped.Reasons.Add(reason);
    }

    public void RecordDroppedAsset(string reason)
    {
        Dropped.Assets++;
        Dropped.Reasons.Add(reason);
    }

    public void Write(string manifestFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestFilePath)!);
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(manifestFilePath, json);
    }

    public sealed class RenderLayerCounts
    {
        public int PlacementCount { get; set; }
        public int UniqueMeshCount { get; set; }
        public int MaterialCount { get; set; }
        public int PartFileCount { get; set; }
        public List<string> PartFiles { get; } = new();
    }

    public sealed class LosslessLayerCounts
    {
        public int ActorCount { get; set; }
        public int ComponentCount { get; set; }
        public Dictionary<string, int> ActorsByExportType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ComponentsByExportType { get; } = new(StringComparer.Ordinal);
    }

    public sealed class ClosureLayerCounts
    {
        public int AssetCount { get; set; }
        public List<string> AssetPackagePaths { get; } = new();
    }

    public sealed class DroppedCounts
    {
        public int Actors { get; set; }
        public int Components { get; set; }
        public int Assets { get; set; }
        public List<string> Reasons { get; } = new();
    }
}
