using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using Ruri.RipperHook.Bridge;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Data;

namespace Ruri.RipperHook.Statements;

/// <summary>
/// The serialized text of the scripted data assets a set of archives carries, in memory. A
/// title's designer-authored tables (which model a character declares, which mesh a part
/// slot wears) are scripted assets whose readers this build already has in the text form; the
/// text is produced here for those readers alone and nothing of geometry ever takes this road.
/// </summary>
public static class UnityAssetText
{
    /// <summary>The same texts with the path each was written under, for a reader that wants
    /// ONE of them: which asset a text is cannot be read out of the text itself without knowing
    /// the game's own spelling, and the path already says it.</summary>
    public static List<(string Path, string Text)> Entries(CabTable map, IEnumerable<string> cabs)
    {
        List<(string, string)> entries = [];
        foreach ((string path, string text) in Read(map, cabs))
        {
            entries.Add((path, text));
        }
        return entries;
    }

    public static List<string> MonoBehaviours(CabTable map, IEnumerable<string> cabs)
    {
        List<string> only = [];
        foreach ((string _path, string text) in Read(map, cabs))
        {
            only.Add(text);
        }
        return only;
    }

    private static List<(string Path, string Text)> Read(CabTable map, IEnumerable<string> cabs)
    {
        CabClosure closure = ClosureReader.Resolve(map, cabs, reachThroughDependents: true);
        if (closure.Files.Length == 0)
        {
            return [];
        }
        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ExportSettings.ShaderExportMode = ShaderExportMode.Dummy;
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
        TextOnlyExportHandler handler = new(settings);
        GameData gameData = ClosureReader.Load(closure, handler);
        if (!gameData.GameBundle.HasAnyAssetCollections())
        {
            return [];
        }
        handler.Process(gameData);
        InMemoryFileSystem memory = new();
        handler.Export(gameData, "mem:/text", memory);
        List<(string, string)> texts = [];
        foreach ((string path, byte[] bytes) in memory.Files)
        {
            if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                texts.Add((path, System.Text.Encoding.UTF8.GetString(bytes)));
            }
        }
        return texts;
    }

    /// <summary>Everything that is not a scripted asset or a game object tree is swallowed
    /// before the default exporters see it: what this reads is designer-authored text, and
    /// a prefab's own components ride inside the prefab's file.</summary>
    private sealed class TextOnlyExportHandler(FullConfiguration settings) : ExportHandler(settings)
    {
        protected override void BeforeExport(ProjectExporter projectExporter)
        {
            projectExporter.OverrideExporter<IUnityObjectBase>(new DropExporter(), allowInheritance: true);
        }
    }

    private sealed class DropExporter : IAssetExporter
    {
        public bool TryCreateCollection(IUnityObjectBase asset, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IExportCollection? exportCollection)
        {
            if (asset is AssetRipper.SourceGenerated.Classes.ClassID_1.IGameObject
                or AssetRipper.SourceGenerated.Classes.ClassID_2.IComponent
                or AssetRipper.SourceGenerated.Classes.ClassID_1001.IPrefabInstance)
            {
                exportCollection = null;
                return false;
            }
            exportCollection = new SkipExportCollection(this, asset);
            return true;
        }

        public bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem) => false;

        public void Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem, Action<IExportContainer, IUnityObjectBase, string, FileSystem>? callback)
        {
        }

        public bool Export(IExportContainer container, IEnumerable<IUnityObjectBase> assets, string path, FileSystem fileSystem) => false;

        public void Export(IExportContainer container, IEnumerable<IUnityObjectBase> assets, string path, FileSystem fileSystem, Action<IExportContainer, IUnityObjectBase, string, FileSystem>? callback)
        {
        }

        public AssetType ToExportType(IUnityObjectBase asset) => AssetType.Serialized;

        public bool ToUnknownExportType(Type type, out AssetType assetType)
        {
            assetType = AssetType.Serialized;
            return false;
        }
    }
}
