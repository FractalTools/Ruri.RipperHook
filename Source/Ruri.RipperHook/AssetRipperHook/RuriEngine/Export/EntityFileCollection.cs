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
    private const string MainObjectKey = "mainObjectFileID";

    private readonly IReadOnlyList<IUnityObjectBase> members;
    private readonly HashSet<IUnityObjectBase> memberSet;
    private readonly RuriEngineMapping mapping;
    private readonly EntityDocumentBuilder builder;
    private readonly RecordTable records;
    private readonly GapReport gaps;
    private readonly UnityYamlAssetCollection? yaml;

    public EntityFileCollection(IAssetExporter exporter, IReadOnlyList<IUnityObjectBase> members, RuriEngineMapping mapping, EntityDocumentBuilder builder, RecordTable records, GapReport gaps)
    {
        AssetExporter = exporter;
        this.members = members;
        memberSet = new HashSet<IUnityObjectBase>(members);
        this.mapping = mapping;
        this.builder = builder;
        this.records = records;
        this.gaps = gaps;
        yaml = members[0].Collection as UnityYamlAssetCollection;
        GUID = UnityGuid.Parse((yaml is null ? StableGuid(members[0]) : Guid.Parse(yaml.Entry.Guid)).ToString("N", CultureInfo.InvariantCulture));
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
        string path = yaml is null ? NewPath(projectDirectory, fileSystem) : fileSystem.Path.Join(projectDirectory, ConvertedPath(yaml.Entry));
        string description = yaml?.Entry.RelativePath ?? $"{members[0].ClassName} {Name}";

        ReferenceResolver references = new(container, mapping, records, gaps, Guid.Parse(GUID.ToString()), yaml?.Entry);
        ObjectValue entities = new();
        int order = 0;
        foreach (IUnityObjectBase asset in members)
        {
            ObjectValue? components;
            try
            {
                components = builder.BuildComponents(asset, references, description, order++);
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
        else
        {
            yaml.Entry.ConvertedRelativePath = ConvertedPath(yaml.Entry);
        }
        return true;
    }

    private static Guid StableGuid(IUnityObjectBase main)
    {
        return ObjectGuid.Derive(ObjectDomain.DerivedProduct, Guid.Empty, $"{main.Collection.Name}|{main.PathID.ToString(CultureInfo.InvariantCulture)}");
    }

    private string ConvertedPath(ProjectEntry entry)
    {
        string extension = mapping.RowOf(MainMember(entry).ClassName).Extension;
        return System.IO.Path.ChangeExtension(mapping.RelocatedPath(entry.RelativePath) ?? entry.RelativePath, extension);
    }

    private IUnityObjectBase MainMember(ProjectEntry entry)
    {
        if (entry.Meta.TryGet(entry.ImporterName, out UnityYamlNode? importer)
            && importer is UnityYamlMapping block
            && block.TryGetText(MainObjectKey) is { } text
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mainId))
        {
            foreach (IUnityObjectBase member in members)
            {
                if (member.PathID == mainId)
                {
                    return member;
                }
            }
        }
        foreach (IUnityObjectBase member in members)
        {
            if (mapping.RowOf(member.ClassName).Kind == ClassKind.Mapped)
            {
                return member;
            }
        }
        return members[0];
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
