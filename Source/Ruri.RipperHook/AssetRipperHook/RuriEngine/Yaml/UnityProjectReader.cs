using System.Security.Cryptography;
using System.Text;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Logging;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;

namespace Ruri.RipperHook.RuriEngine;

internal static class UnityProjectReader
{
    private const string AssetsDirectoryName = "Assets";
    private const string PackageCacheDirectory = "Library/PackageCache";
    private const string SettingsAssetPattern = "*.asset";
    private const string ProjectSettingsDirectoryName = "ProjectSettings";
    private const string ProjectVersionFile = "ProjectVersion.txt";
    private const string ProjectSettingsFile = "ProjectSettings.asset";
    private const string EditorVersionKey = "m_EditorVersion";
    private const string ProductNameKey = "productName";
    private const string MetaExtension = ".meta";
    private const string ScriptExtension = ".cs";
    private const string GuidKey = "guid";
    private const string FolderKey = "folderAsset";
    private const int MonoBehaviourClassId = 114;
    private const int MonoScriptClassId = 115;
    private const long MonoScriptFileId = 11500000;

    private static ReadOnlySpan<byte> YamlSignature => "%YAML"u8;

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    public static GameData? TryLoad(IReadOnlyList<string> paths, FileSystem fileSystem)
    {
        if (paths.Count != 1
            || !fileSystem.Directory.Exists(fileSystem.Path.Join(paths[0], AssetsDirectoryName))
            || !fileSystem.Directory.Exists(fileSystem.Path.Join(paths[0], ProjectSettingsDirectoryName)))
        {
            return null;
        }
        return Load(paths[0], fileSystem);
    }

    private static GameData Load(string root, FileSystem fileSystem)
    {
        RuriEngineMapping mapping = RuriEngineSession.Mapping;
        EngineTypes types = RuriEngineSession.Types;
        GapReport gaps = RuriEngineSession.Gaps;

        UnityVersion version = ReadVersion(root, fileSystem);
        gaps.ProjectVersion = version.ToString();
        gaps.ProjectName = ReadProductName(root, fileSystem);
        Logger.Info(LogCategory.Import, $"[UnityYaml] project '{gaps.ProjectName}' at {root}, Unity {version}");

        GameBundle bundle = new();
        ProjectIndex index = new(root);
        PackageIndex packages = PackagesOf(root, fileSystem);
        Logger.Info(LogCategory.Import, $"[UnityYaml] {packages.Count} package scripts and YAML assets indexed");
        index.Materialize = guid => MaterializePackage(guid, packages, bundle, index, mapping, version, fileSystem);
        List<UnityYamlAssetCollection> yamlAssets = new();
        foreach (ProjectEntry entry in Scan(root, fileSystem, mapping, gaps))
        {
            UnityYamlAssetCollection collection = new(bundle, entry, version) { Project = index };
            index.Add(collection);
            if (entry.Kind == ProjectFileKind.Script)
            {
                AddScript(collection, version);
            }
            else if (entry.Kind == ProjectFileKind.YamlAsset)
            {
                yamlAssets.Add(collection);
            }
        }
        Logger.Info(LogCategory.Import, $"[UnityYaml] {index.Collections.Count()} project files, {yamlAssets.Count} YAML assets to read");

        ITypeResolver resolver = new EngineTypeResolver(types);
        Parallel.ForEach(yamlAssets, collection => LoadYamlAsset(collection, index, mapping, types, resolver, gaps, version, fileSystem));
        for (List<UnityYamlAssetCollection> referenced = index.TakePending(); referenced.Count > 0; referenced = index.TakePending())
        {
            Logger.Info(LogCategory.Import, $"[UnityYaml] {referenced.Count} package YAML assets the project refers to");
            Parallel.ForEach(referenced, collection => LoadYamlAsset(collection, index, mapping, types, resolver, gaps, version, fileSystem));
        }
        return new GameData(bundle, version, new BaseManager(static _ => { }), null);
    }

    private static UnityVersion ReadVersion(string root, FileSystem fileSystem)
    {
        string path = fileSystem.Path.Join(root, ProjectSettingsDirectoryName, ProjectVersionFile);
        if (UnityYamlParser.ParseSingle(fileSystem.File.ReadAllText(path)) is UnityYamlMapping mapping
            && mapping.TryGetText(EditorVersionKey) is { Length: > 0 } text)
        {
            return UnityVersion.Parse(text);
        }
        throw new InvalidDataException($"{path} states no {EditorVersionKey}.");
    }

    private static string ReadProductName(string root, FileSystem fileSystem)
    {
        string path = fileSystem.Path.Join(root, ProjectSettingsDirectoryName, ProjectSettingsFile);
        if (!fileSystem.File.Exists(path))
        {
            return string.Empty;
        }
        try
        {
            List<UnityYamlDocument> documents = UnityYamlParser.ParseDocuments(fileSystem.File.ReadAllText(path));
            foreach (UnityYamlDocument document in documents)
            {
                if (document.Root is UnityYamlMapping { Entries.Count: 1 } wrapper
                    && wrapper.Entries[0].Value is UnityYamlMapping body
                    && body.TryGetText(ProductNameKey) is { } name)
                {
                    return name;
                }
            }
        }
        catch (FormatException)
        {
        }
        return string.Empty;
    }

    private static List<ProjectEntry> Scan(string root, FileSystem fileSystem, RuriEngineMapping mapping, GapReport gaps)
    {
        List<ProjectEntry> entries = new();
        string assets = fileSystem.Path.Join(root, AssetsDirectoryName);
        foreach (string metaPath in fileSystem.Directory.EnumerateFiles(assets, "*" + MetaExtension, SearchOption.AllDirectories))
        {
            string assetPath = metaPath[..^MetaExtension.Length];
            string relative = fileSystem.Path.GetRelativePath(root, assetPath).Replace('\\', '/');
            bool isFolder = fileSystem.Directory.Exists(assetPath);
            bool exists = isFolder || fileSystem.File.Exists(assetPath);
            UnityYamlMapping meta;
            try
            {
                meta = UnityYamlParser.ParseSingle(fileSystem.File.ReadAllText(metaPath)) as UnityYamlMapping
                    ?? throw new FormatException("meta is not a mapping");
            }
            catch (FormatException exception)
            {
                gaps.Add(GapCategories.UnreadableFile, "unreadable meta", $"{relative}: {exception.Message}");
                continue;
            }
            if (meta.TryGetText(GuidKey) is not { Length: > 0 } guid)
            {
                gaps.Add(GapCategories.UnreadableFile, "meta without guid", relative);
                continue;
            }

            string importer = ImporterOf(meta, mapping);
            if (importer.Length == 0 && exists && !isFolder)
            {
                importer = mapping.ImporterOfExtension(fileSystem.Path.GetExtension(assetPath)) ?? string.Empty;
            }
            ProjectFileKind kind = ProjectFileKind.Skipped;
            if (!exists)
            {
                gaps.Add(GapCategories.OrphanMeta, "meta without asset", relative);
            }
            else if (isFolder)
            {
                kind = ProjectFileKind.Folder;
            }
            else
            {
                kind = KindOf(assetPath, relative, importer, mapping, gaps, fileSystem);
            }
            entries.Add(new ProjectEntry
            {
                Guid = guid,
                RelativePath = relative,
                AbsolutePath = assetPath,
                Meta = meta,
                ImporterName = importer,
                Kind = kind,
                WritesMeta = !isFolder,
            });
        }
        AddSettings(entries, root, fileSystem);
        return entries;
    }

    private static void AddSettings(List<ProjectEntry> entries, string root, FileSystem fileSystem)
    {
        string settings = fileSystem.Path.Join(root, ProjectSettingsDirectoryName);
        foreach (string path in fileSystem.Directory.EnumerateFiles(settings, SettingsAssetPattern))
        {
            if (!IsYaml(path, fileSystem))
            {
                continue;
            }
            string relative = $"{ProjectSettingsDirectoryName}/{fileSystem.Path.GetFileName(path)}";
            entries.Add(new ProjectEntry
            {
                Guid = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(relative))),
                RelativePath = relative,
                AbsolutePath = path,
                Meta = new UnityYamlMapping(),
                ImporterName = string.Empty,
                Kind = ProjectFileKind.YamlAsset,
                WritesMeta = false,
            });
        }
    }

    private static PackageIndex PackagesOf(string root, FileSystem fileSystem)
    {
        string cache = RuriEngineSession.PackageCachePath is { Length: > 0 } given
            ? given
            : fileSystem.Path.Join(root, PackageCacheDirectory);
        return fileSystem.Directory.Exists(cache) ? PackageIndex.Scan(cache, fileSystem) : PackageIndex.Empty;
    }

    private static UnityYamlAssetCollection? MaterializePackage(
        string guid,
        PackageIndex packages,
        GameBundle bundle,
        ProjectIndex index,
        RuriEngineMapping mapping,
        UnityVersion version,
        FileSystem fileSystem)
    {
        if (!packages.TryGet(guid, out PackageFile file))
        {
            return null;
        }
        ProjectFileKind kind = file.IsScript ? ProjectFileKind.Script : IsYaml(file.AssetPath, fileSystem) ? ProjectFileKind.YamlAsset : ProjectFileKind.Skipped;
        if (kind == ProjectFileKind.Skipped)
        {
            return null;
        }
        UnityYamlMapping meta;
        try
        {
            meta = UnityYamlParser.ParseSingle(fileSystem.File.ReadAllText(file.AssetPath + MetaExtension)) as UnityYamlMapping ?? throw new FormatException("meta is not a mapping");
        }
        catch (FormatException)
        {
            return null;
        }
        ProjectEntry entry = new()
        {
            Guid = guid,
            RelativePath = file.RelativePath,
            AbsolutePath = file.AssetPath,
            Meta = meta,
            ImporterName = ImporterOf(meta, mapping),
            Kind = kind,
            IsPackage = true,
            WritesMeta = kind == ProjectFileKind.YamlAsset,
        };
        UnityYamlAssetCollection collection = new(bundle, entry, version) { Project = index };
        if (kind == ProjectFileKind.Script)
        {
            AddScript(collection, version);
        }
        return collection;
    }

    private static string ImporterOf(UnityYamlMapping meta, RuriEngineMapping mapping)
    {
        foreach (KeyValuePair<string, UnityYamlNode> entry in meta.Entries)
        {
            if (entry.Key != FolderKey && !mapping.IsNonImporterBlock(entry.Key) && entry.Value is UnityYamlMapping)
            {
                return entry.Key;
            }
        }
        return string.Empty;
    }

    private static ProjectFileKind KindOf(string assetPath, string relative, string importer, RuriEngineMapping mapping, GapReport gaps, FileSystem fileSystem)
    {
        switch (mapping.ImporterOf(importer))
        {
            case ImporterKind.Yaml:
                if (IsYaml(assetPath, fileSystem))
                {
                    return ProjectFileKind.YamlAsset;
                }
                gaps.Add(GapCategories.UnsupportedLayout, "serialized asset is not YAML", relative);
                return ProjectFileKind.Skipped;
            case ImporterKind.Copy:
                return ProjectFileKind.Payload;
            case ImporterKind.Script:
                return assetPath.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase) ? ProjectFileKind.Script : ProjectFileKind.Skipped;
            case ImporterKind.Skip:
                return ProjectFileKind.Skipped;
            default:
                gaps.Add(GapCategories.UnmappedImporter, importer.Length == 0 ? "(none)" : importer, relative);
                return ProjectFileKind.Skipped;
        }
    }

    private static bool IsYaml(string path, FileSystem fileSystem)
    {
        using Stream stream = fileSystem.File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[Utf8ByteOrderMark.Length + YamlSignature.Length];
        ReadOnlySpan<byte> head = buffer[..stream.Read(buffer)];
        if (head.StartsWith(Utf8ByteOrderMark))
        {
            head = head[Utf8ByteOrderMark.Length..];
        }
        return head.StartsWith(YamlSignature);
    }

    private static void AddScript(UnityYamlAssetCollection collection, UnityVersion version)
    {
        string className = System.IO.Path.GetFileNameWithoutExtension(collection.Entry.RelativePath);
        IMonoScript script = MonoScript.Create(new AssetInfo(collection, MonoScriptFileId, MonoScriptClassId), version);
        script.ClassName_R = className;
        script.Name_R = className;
        script.Namespace = string.Empty;
        script.AssemblyName = string.Empty;
        collection.Add(script);
    }

    private static void LoadYamlAsset(
        UnityYamlAssetCollection collection,
        ProjectIndex index,
        RuriEngineMapping mapping,
        EngineTypes types,
        ITypeResolver resolver,
        GapReport gaps,
        UnityVersion version,
        FileSystem fileSystem)
    {
        ProjectEntry entry = collection.Entry;
        List<UnityYamlDocument> documents;
        try
        {
            documents = UnityYamlParser.ParseDocuments(fileSystem.File.ReadAllText(entry.AbsolutePath));
        }
        catch (FormatException exception)
        {
            gaps.Add(GapCategories.UnreadableFile, "unparsable YAML", $"{entry.RelativePath}: {exception.Message}");
            return;
        }

        if (NodeFileReader.IsNodeFile(entry))
        {
            NodeFileReader.Read(collection, documents, index, version, gaps);
            return;
        }

        YamlStructureFiller filler = new(mapping, index, resolver, gaps, version, collection);
        foreach (UnityYamlDocument document in documents)
        {
            if (document.Stripped)
            {
                continue;
            }
            string className = ClassNameOf(document.ClassId);
            ClassRow row = mapping.RowOf(className);
            if (document.Root is not UnityYamlMapping { Entries.Count: 1 } wrapper || wrapper.Entries[0].Value is not UnityYamlMapping body)
            {
                gaps.Add(GapCategories.UnreadableFile, "document without a body", entry.RelativePath);
                continue;
            }
            try
            {
                if (row.Kind == ClassKind.Scripted && document.ClassId == MonoBehaviourClassId)
                {
                    LoadBehaviour(collection, document, body, index, types, gaps, filler, version);
                }
                else if (row.Kind == ClassKind.Mapped)
                {
                    LoadMapped(collection, document, body, version);
                }
                else
                {
                    gaps.Add(GapCategories.UnmappedClass, className, entry.RelativePath);
                }
            }
            catch (Exception exception)
            {
                gaps.Add(GapCategories.UnreadableFile, $"{className} could not be read", $"{entry.RelativePath}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void LoadBehaviour(
        UnityYamlAssetCollection collection,
        UnityYamlDocument document,
        UnityYamlMapping body,
        ProjectIndex index,
        EngineTypes types,
        GapReport gaps,
        YamlStructureFiller filler,
        UnityVersion version)
    {
        string relative = collection.Entry.RelativePath;
        if (body.TryGet("m_GameObject", out UnityYamlNode? owner) && owner is UnityYamlMapping ownerReference
            && ownerReference.TryGetText("fileID") is { } ownerId && ownerId != "0")
        {
            gaps.Add(GapCategories.UnmappedClass, "MonoBehaviour (component of a game object)", relative);
            return;
        }

        IMonoBehaviour behaviour = MonoBehaviour.Create(new AssetInfo(collection, document.FileId, MonoBehaviourClassId), version);
        if (body.TryGetText("m_Enabled") is { } enabled && YamlScalars.TryParseInteger(enabled, out long enabledValue))
        {
            behaviour.Enabled = (byte)enabledValue;
        }
        if (body.TryGetText("m_Name") is { } name)
        {
            behaviour.Name_R = name;
        }
        collection.Add(behaviour);

        string? guid = body.TryGet("m_Script", out UnityYamlNode? scriptNode) && scriptNode is UnityYamlMapping scriptReference
            ? scriptReference.TryGetText(GuidKey)
            : null;
        if (guid is null
            || !index.TryGetCollection(guid, out UnityYamlAssetCollection? scriptCollection)
            || scriptCollection.Entry.Kind != ProjectFileKind.Script
            || !scriptCollection.TryGetAsset(MonoScriptFileId, out IMonoScript? script))
        {
            gaps.Add(GapCategories.UnresolvedScript, guid ?? "(no script reference)", relative);
            return;
        }
        behaviour.ScriptP = script;

        if (RuriEngineSession.Records.TryFind(script.ClassName_R.String, null, null, out _))
        {
            collection.Bodies.Add(document.FileId, body);
            return;
        }

        if (!ScriptIdentity.TryResolve(types, string.Empty, script.ClassName_R.String, out EngineClass? engineClass, out string reason))
        {
            gaps.Add(GapCategories.UnresolvedClass, reason, relative);
            return;
        }
        if (SplitLayout.RootOf(types, RuriEngineSession.Mapping, engineClass).CreateInstance(0, version) is SerializableStructure structure)
        {
            filler.FillStructure(structure, body, engineClass.FullName);
            behaviour.Structure = structure;
        }
    }

    private static void LoadMapped(
        UnityYamlAssetCollection collection,
        UnityYamlDocument document,
        UnityYamlMapping body,
        UnityVersion version)
    {
        if (AddTypeTreeAsset(collection, document.FileId, document.ClassId, version))
        {
            collection.Bodies.Add(document.FileId, body);
        }
    }

    internal static string ClassNameOf(int classId)
    {
        return Enum.IsDefined(typeof(ClassIDType), classId) ? ((ClassIDType)classId).ToString() : $"Class{classId}";
    }

    internal static bool AddTypeTreeAsset(UnityYamlAssetCollection collection, long fileId, int classId, UnityVersion version)
    {
        if (!TypeTreeNodeStruct.TryMakeFromTpk((ClassIDType)classId, version, out _, out TypeTreeNodeStruct editorTree))
        {
            RuriEngineSession.Gaps.Add(GapCategories.UnreadableFile, $"no Unity type tree for {ClassNameOf(classId)}", collection.Entry.RelativePath);
            return false;
        }
        collection.Add(TypeTreeObject.Create(new AssetInfo(collection, fileId, classId), editorTree, ITypeResolver.Null));
        return true;
    }
}
