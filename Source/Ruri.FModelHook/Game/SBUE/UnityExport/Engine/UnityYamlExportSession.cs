using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

// Owns the lifetime of one synthetic Unity export: a GameBundle + a single
// ProcessedAssetCollection (wrapped in a ConversionContext) that every converted
// object lands in. Convert() turns one UE export into a Unity object via the
// registry (deduplicated). ExportAll() hands the populated GameBundle off to
// AssetRipper's full ExportHandler pipeline so textures route through
// TextureAssetExporter -> PNG, materials through .mat, meshes through .asset, etc.
// — instead of the half-baked YAML-only DefaultYamlExporter path.
public sealed class UnityYamlExportSession
{
    private readonly GameBundle _bundle;
    private readonly ConversionContext _context;
    private readonly UnityVersion _version;
    private readonly Action<string> _logError;

    public int ConvertedCount => _context.Converted.Count;

    public UnityYamlExportSession(UnityVersion version, Action<string> logError)
    {
        _version = version;
        _logError = logError;
        _bundle = new GameBundle();
        // AddNewProcessedCollection MUST carry the version, else version dispatch
        // falls to the Texture2D_3_5 antique layout with different field names
        // (FModelHook design note).
        ProcessedAssetCollection collection = _bundle.AddNewProcessedCollection("RuriUnityExport", version);
        _context = new ConversionContext(collection);
    }

    // Convert one UE export into a Unity object (or null if unmapped). Per-asset
    // failures are logged with full context and swallowed — one bad asset must
    // never sink a multi-thousand-asset run.
    public IUnityObjectBase? Convert(UObject source)
    {
        try
        {
            return _context.Convert(source);
        }
        catch (Exception ex)
        {
            _logError($"[UnityExport] convert failed: {ex.Message}");
            return null;
        }
    }

    // Run AssetRipper's real export pipeline on the populated GameBundle.
    // ExportHandler.Export -> ProjectExporter builds the full exporter stack
    // (TextureAssetExporter -> PNG, SceneYamlExporter -> .prefab/.unity,
    // ScriptableObjectExporter -> .asset, mesh streamed exporter -> .asset,
    // SceneAssetExporter -> .asset, etc.) and walks every collection in the
    // bundle, asking each per-type exporter to TryCreateCollection on its
    // assets and Export them. Output lands in
    // {outputDirectory}/ExportedProject/Assets/<typed-subdir>/.
    //
    // We invoke Process() before Export() so processors like SpriteProcessor
    // attach a SpriteInformationObject MainAsset to every standalone ITexture2D
    // — that is what TextureAssetExporter.TryCreateCollection keys on; without
    // it, textures fall through to YamlStreamedAssetExporter and emit .texture2D
    // YAML (the very bug we're fixing).
    //
    // The synthetic collection has no assemblies, no scenes, no engine resources
    // -> assembly processors no-op (mscorlib == null), scene processors find
    // nothing, ProjectExporter.DoFinalOverrides supplies the engine-asset and
    // deleted-asset exporters. Cross-asset PPtrs we wired during the Convert()
    // phase are resolved by ProjectAssetContainer (constructed inside
    // ProjectExporter.Export).
    public int ExportAll(string outputDirectory)
    {
        FullConfiguration settings = new FullConfiguration();
        // The exporter exposes ImageExportFormat.Png by default (see ExportSettings.cs),
        // so textures flow through TextureAssetExporter -> DirectBitmap.Save(..., Png).

        BaseManager assemblyManager = new BaseManager(static _ => { });
        GameData gameData = new GameData(_bundle, _version, assemblyManager, null);

        ExportHandler exportHandler = new ExportHandler(settings);

        // Bridge AR's Logger into our error sink for the duration of this export
        // — Logger is a process-wide static (registered loggers list), so we
        // attach a forwarder, run, then detach. Without this the per-exporter
        // failure diagnostics ("Can't export 'Tex' because resources file 'X'
        // hasn't been found", "Unable to convert 'Tex' to bitmap", etc.) vanish
        // into the void and we cannot diagnose missing-PNG cases.
        ForwardingLogger forwarder = new ForwardingLogger(_logError);
        Logger.Add(forwarder);
        try
        {
            // Processors need a bundle that has any assets at all; bail cleanly when
            // there is nothing to process (mirrors ExportHandler.LoadProcessAndExport
            // guard `HasAnyAssetCollections`).
            if (_bundle.HasAnyAssetCollections())
            {
                try
                {
                    exportHandler.Process(gameData);
                }
                catch (Exception ex)
                {
                    _logError($"[UnityExport] Process() failed (continuing to Export): {ex.Message}");
                }
            }

            try
            {
                exportHandler.Export(gameData, outputDirectory, LocalFileSystem.Instance);
            }
            catch (Exception ex)
            {
                _logError($"[UnityExport] Export() failed: {ex.Message}");
                return 0;
            }
        }
        finally
        {
            Logger.Remove(forwarder);
        }

        // ProjectExporter writes one file per IExportCollection; we don't get a
        // file-count back from AR's API. Approximate it with the converted-asset
        // count — close enough for the self-test summary, and the on-disk
        // listing under outputDirectory/ExportedProject is the source of truth.
        return _context.Converted.Count;
    }

    // Forwards AR's per-step diagnostics (LogType.Warning / Error and the per-
    // collection Export progress prints) into the session's error sink. Info
    // lines stay routed through AR's normal path so we don't drown the runner
    // log in scene-load chatter.
    private sealed class ForwardingLogger : ILogger
    {
        private readonly Action<string> _sink;
        public ForwardingLogger(Action<string> sink) => _sink = sink;
        public void BlankLine(int numLines) { }
        public void Log(LogType type, LogCategory category, string message)
        {
            if (type == LogType.Warning || type == LogType.Error)
            {
                _sink($"[AR.{category}.{type}] {message}");
            }
        }
    }

    // Exposed so callers (FModel GUI hook, future inspection tooling) can walk
    // the conversion result without going through ExportAll.
    internal IReadOnlyList<IUnityObjectBase> Converted => _context.Converted;
}
