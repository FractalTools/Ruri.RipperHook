using System.Globalization;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_1034;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class EntityFileCollection : ExportCollection
{
    private const string EntitiesKey = "entities";
    private const string ComponentsKey = "components";

    private readonly IReadOnlyList<IUnityObjectBase> members;
    private readonly HashSet<IUnityObjectBase> memberSet;
    private readonly RuriEngineMapping mapping;
    private readonly EntityDocumentBuilder builder;
    private readonly GapReport gaps;
    private readonly UnityYamlAssetCollection? yaml;

    public EntityFileCollection(IAssetExporter exporter, IReadOnlyList<IUnityObjectBase> members, RuriEngineMapping mapping, EntityDocumentBuilder builder, GapReport gaps)
    {
        AssetExporter = exporter;
        this.members = members;
        memberSet = new HashSet<IUnityObjectBase>(members);
        this.mapping = mapping;
        this.builder = builder;
        this.gaps = gaps;
        yaml = members[0].Collection as UnityYamlAssetCollection;
        GUID = yaml is null ? UnityGuid.NewGuid() : UnityGuid.Parse(yaml.Entry.Guid);
    }

    public override UnityGuid GUID { get; }

    public override IAssetExporter AssetExporter { get; }

    public override AssetCollection File => members[0].Collection;

    public override IEnumerable<IUnityObjectBase> Assets => members;

    public override string Name => members[0].GetBestName();

    public override bool Contains(IUnityObjectBase asset) => memberSet.Contains(asset);

    public override long GetExportID(IExportContainer container, IUnityObjectBase asset)
    {
        return yaml is null ? ExportIdHandler.GetMainExportID(asset) : asset.PathID;
    }

    public override MetaPtr CreateExportPointer(IExportContainer container, IUnityObjectBase asset, bool isLocal)
    {
        long exportId = GetExportID(container, asset);
        return isLocal ? new MetaPtr(exportId) : new MetaPtr(exportId, GUID, AssetExporter.ToExportType(asset));
    }

    protected override string GetExportExtension(IUnityObjectBase asset) => mapping.RowOf(asset.ClassName).Extension;

    public override bool Export(IExportContainer container, string projectDirectory, FileSystem fileSystem)
    {
        string path = yaml is null ? NewPath(projectDirectory, fileSystem) : fileSystem.Path.Join(projectDirectory, yaml.Entry.RelativePath);
        string description = yaml?.Entry.RelativePath ?? $"{members[0].ClassName} {Name}";

        ReferenceResolver references = new(container, mapping, gaps);
        ObjectValue entities = new();
        foreach (IUnityObjectBase asset in members)
        {
            ObjectValue? components;
            try
            {
                components = builder.BuildComponents(asset, references, description);
            }
            catch (Exception exception)
            {
                gaps.Add(GapCategories.UnreadableFile, $"{asset.ClassName} could not be converted", $"{description}: {exception.GetType().Name}: {exception.Message}");
                continue;
            }
            if (components is null)
            {
                continue;
            }
            ObjectValue entity = new();
            entity.Add(ComponentsKey, components);
            entities.Add(GetExportID(container, asset).ToString(CultureInfo.InvariantCulture), entity);
        }
        if (entities.Count == 0)
        {
            return true;
        }

        ObjectValue document = new();
        document.Add(EntitiesKey, entities);
        fileSystem.Directory.Create(fileSystem.Path.GetDirectoryName(path));
        fileSystem.File.WriteAllBytes(path, JsonValueWriter.Write(document));
        if (yaml is null)
        {
            WriteMeta(container, path, fileSystem);
        }
        return true;
    }

    private string NewPath(string projectDirectory, FileSystem fileSystem)
    {
        IUnityObjectBase main = members[0];
        string directory = fileSystem.Path.Join(projectDirectory, FileSystem.FixInvalidPathCharacters(main.GetBestDirectory()));
        fileSystem.Directory.Create(directory);
        return fileSystem.Path.Join(directory, GetUniqueFileName(main, directory, fileSystem));
    }

    private void WriteMeta(IExportContainer container, string path, FileSystem fileSystem)
    {
        INativeFormatImporter importer = NativeFormatImporter.Create(container.File, container.ExportVersion);
        importer.MainObjectFileID = GetExportID(container, members[0]);
        if (importer.Has_AssetBundleName_R() && members[0].AssetBundleName is { } bundleName)
        {
            importer.AssetBundleName_R = bundleName;
        }
        ExportMeta(container, new Meta(GUID, importer), path, fileSystem);
    }
}
