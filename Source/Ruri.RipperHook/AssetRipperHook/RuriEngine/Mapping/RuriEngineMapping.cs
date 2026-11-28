using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruri.RipperHook.RuriEngine;

internal enum ClassKind
{
    Scripted,
    Mapped,
    Node,
    Payload,
    Reserved,
}

internal enum ImporterKind
{
    Yaml,
    Copy,
    Script,
    Skip,
}

internal sealed class ClassRow
{
    public ClassKind Kind { get; init; } = ClassKind.Reserved;

    public string? EngineType { get; init; }

    public string Extension { get; init; } = "entity";
}

internal sealed class SplitGroup
{
    public string Component { get; init; } = string.Empty;

    public string[] Fields { get; init; } = [];
}

internal sealed class SplitRow
{
    public string? Identity { get; init; }

    public bool EmitIdentity { get; init; } = true;

    public SplitGroup[] Groups { get; init; } = [];
}

internal sealed class RuriEngineMapping
{
    public const string ResourceName = "RuriEngineMapping.json";

    private static readonly ClassRow ReservedRow = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private HashSet<string>? bookkeeping;
    private Dictionary<string, HashSet<string>>? ignored;
    private HashSet<string>? builtinGuids;
    private Dictionary<string, string>? redirects;
    private HashSet<string>? blanked;
    private Dictionary<string, string>? relocations;
    private HashSet<string>? metaTextKeys;
    private HashSet<string>? nonImporterBlocks;
    private readonly ConcurrentDictionary<string, Dictionary<string, string?>> normalized = new(StringComparer.Ordinal);

    public string FieldPrefix { get; init; } = "m_";

    public string ComponentListField { get; init; } = string.Empty;

    public string IdentityComponent { get; init; } = string.Empty;

    public string IdentityField { get; init; } = string.Empty;

    public Dictionary<string, SplitRow> Splits { get; init; } = new();

    public string[] BookkeepingFields { get; init; } = [];

    public string[] MetaTextKeys { get; init; } = [];

    public string[] NonImporterBlocks { get; init; } = [];

    public string[] IdentityKeys { get; init; } = [];

    public string[] IdentityKeyImporters { get; init; } = [];

    public string[] BuiltinGuids { get; init; } = [];

    public Dictionary<string, string> PackageGuidRedirects { get; init; } = new();

    public Dictionary<string, string> BlankedPackageGuids { get; init; } = new();

    public Dictionary<string, string> OutputRelocations { get; init; } = new();

    public Dictionary<string, ClassRow> Classes { get; init; } = new();

    public Dictionary<string, Dictionary<string, long>> CurrentVersions { get; init; } = new();

    public Dictionary<string, Dictionary<string, string>> FieldRenames { get; init; } = new();

    public Dictionary<string, string[]> IgnoredFields { get; init; } = new();

    public Dictionary<string, ImporterKind> Importers { get; init; } = new();

    public Dictionary<string, string> ImporterByExtension { get; init; } = new();

    public static RuriEngineMapping Load(string? overridePath)
    {
        if (overridePath is { Length: > 0 })
        {
            return Parse(File.ReadAllBytes(overridePath), overridePath);
        }
        Assembly assembly = typeof(RuriEngineMapping).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"[RuriEngine] {ResourceName} is not embedded in {assembly.GetName().Name}.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray(), $"embedded:{ResourceName}");
    }

    public ClassRow RowOf(string unityClassName)
    {
        return Classes.TryGetValue(unityClassName, out ClassRow? row) ? row : ReservedRow;
    }

    public SplitRow? SplitOf(string engineTypeName)
    {
        return Splits.TryGetValue(engineTypeName, out SplitRow? row) ? row : null;
    }

    public ImporterKind? ImporterOf(string importerName)
    {
        return Importers.TryGetValue(importerName, out ImporterKind kind) ? kind : null;
    }

    public string? ImporterOfExtension(string extension)
    {
        return ImporterByExtension.TryGetValue(extension.TrimStart('.').ToLowerInvariant(), out string? importerName) ? importerName : null;
    }

    public string StripPrefix(string name)
    {
        return name.Length > FieldPrefix.Length
            && name.StartsWith(FieldPrefix, StringComparison.Ordinal)
            && char.IsAsciiLetter(name[FieldPrefix.Length])
            ? name[FieldPrefix.Length..]
            : name;
    }

    public bool IsBookkeeping(string sourceName)
    {
        bookkeeping ??= new HashSet<string>(BookkeepingFields, StringComparer.Ordinal);
        string stripped = StripPrefix(sourceName);
        return stripped.Length != sourceName.Length && bookkeeping.Contains(stripped);
    }

    public bool IsIgnoredField(string unityClassName, string fieldName)
    {
        ignored ??= IgnoredFields.ToDictionary(static pair => pair.Key, static pair => new HashSet<string>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        return ignored.TryGetValue(unityClassName, out HashSet<string>? names) && names.Contains(fieldName);
    }

    public bool IsMetaTextKey(string key)
    {
        metaTextKeys ??= new HashSet<string>(MetaTextKeys, StringComparer.Ordinal);
        return metaTextKeys.Contains(key);
    }

    public bool DropsIdentityKey(string importer, string key)
    {
        return Array.IndexOf(IdentityKeys, key) >= 0 && Array.IndexOf(IdentityKeyImporters, importer) < 0;
    }

    public bool IsNonImporterBlock(string key)
    {
        nonImporterBlocks ??= new HashSet<string>(NonImporterBlocks, StringComparer.Ordinal);
        return nonImporterBlocks.Contains(key);
    }

    public bool IsBuiltinGuid(string guid)
    {
        builtinGuids ??= new HashSet<string>(BuiltinGuids, StringComparer.Ordinal);
        return builtinGuids.Contains(guid);
    }

    /// <summary>包资产的引用改写:空表里的 guid 写成空引用,重定向表里的 guid 换成引擎资产 guid(主对象 fileID 与类型沿用源引用);都不在表里返回 null。</summary>
    public ReferenceValue? RetargetPackageReference(string? guid, long fileId, GapReport gaps, string where)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return null;
        }
        blanked ??= new HashSet<string>(BlankedPackageGuids.Keys, StringComparer.OrdinalIgnoreCase);
        if (blanked.Contains(guid))
        {
            gaps.Add(GapCategories.PackageReferenceBlanked, guid, where);
            return ReferenceValue.Null;
        }
        redirects ??= new Dictionary<string, string>(PackageGuidRedirects, StringComparer.OrdinalIgnoreCase);
        if (redirects.TryGetValue(guid, out string? engineGuid))
        {
            gaps.Add(GapCategories.PackageReferenceRedirected, guid, where);
            return ReferenceValue.To(engineGuid, fileId, ObjectGuid.IsMainByStride(fileId));
        }
        return null;
    }

    /// <summary>源工程相对路径(含扩展名)的输出改址;不在表里返回 null,表键值按正斜杠比较。</summary>
    public string? RelocatedPath(string relativePath)
    {
        relocations ??= new Dictionary<string, string>(OutputRelocations, StringComparer.OrdinalIgnoreCase);
        return relocations.TryGetValue(relativePath.Replace('\\', '/'), out string? target) ? target : null;
    }

    public bool TryRename(string engineTypeName, string sourceName, [NotNullWhen(true)] out string? engineName)
    {
        if (FieldRenames.TryGetValue(engineTypeName, out Dictionary<string, string>? renames)
            && renames.TryGetValue(sourceName, out engineName))
        {
            return true;
        }
        engineName = null;
        return false;
    }

    public string? ResolveField(string engineTypeName, string sourceName, Func<string, bool> hasField, Func<IEnumerable<string>> fieldNames)
    {
        if (TryRename(engineTypeName, sourceName, out string? renamed) && hasField(renamed))
        {
            return renamed;
        }
        if (hasField(sourceName))
        {
            return sourceName;
        }
        string stripped = StripPrefix(sourceName);
        if (hasField(stripped))
        {
            return stripped;
        }
        Dictionary<string, string?> byKey = normalized.GetOrAdd(engineTypeName, _ => NormalizedFields(fieldNames()));
        return byKey.TryGetValue(NormalizedKey(sourceName), out string? found) ? found : null;
    }

    public string? ResolveField(StructShape shape, string sourceName)
    {
        return ResolveField(shape.TypeName, sourceName, name => shape.TryGetField(name, out _), () => shape.Fields.Select(static field => field.Name));
    }

    public string NormalizedKey(string name)
    {
        return StripPrefix(name).TrimStart('_').ToLowerInvariant();
    }

    private Dictionary<string, string?> NormalizedFields(IEnumerable<string> fieldNames)
    {
        Dictionary<string, string?> byKey = new(StringComparer.Ordinal);
        foreach (string name in fieldNames)
        {
            string key = NormalizedKey(name);
            byKey[key] = byKey.ContainsKey(key) ? null : name;
        }
        return byKey;
    }

    private static RuriEngineMapping Parse(byte[] json, string origin)
    {
        return JsonSerializer.Deserialize<RuriEngineMapping>(json, Options)
            ?? throw new InvalidDataException($"[RuriEngine] {origin} holds no mapping table.");
    }
}
