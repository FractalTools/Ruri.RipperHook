using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruri.RipperHook.RuriEngine;

internal enum ClassKind
{
    Scripted,
    Mapped,
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

    public string Extension { get; init; } = "asset";
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
    private HashSet<string>? builtinGuids;
    private HashSet<string>? metaTextKeys;

    public string FieldPrefix { get; init; } = "m_";

    public string ComponentListField { get; init; } = string.Empty;

    public string[] BookkeepingFields { get; init; } = [];

    public string[] MetaTextKeys { get; init; } = [];

    public string[] BuiltinGuids { get; init; } = [];

    public Dictionary<string, ClassRow> Classes { get; init; } = new();

    public Dictionary<string, Dictionary<string, string>> FieldRenames { get; init; } = new();

    public Dictionary<string, ImporterKind> Importers { get; init; } = new();

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

    public ImporterKind? ImporterOf(string importerName)
    {
        return Importers.TryGetValue(importerName, out ImporterKind kind) ? kind : null;
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

    public bool IsMetaTextKey(string key)
    {
        metaTextKeys ??= new HashSet<string>(MetaTextKeys, StringComparer.Ordinal);
        return metaTextKeys.Contains(key);
    }

    public bool IsBuiltinGuid(string guid)
    {
        builtinGuids ??= new HashSet<string>(BuiltinGuids, StringComparer.Ordinal);
        return builtinGuids.Contains(guid);
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

    public string? ResolveField(string engineTypeName, string sourceName, Func<string, bool> hasField)
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
        return hasField(stripped) ? stripped : null;
    }

    public string? ResolveField(StructShape shape, string sourceName)
    {
        return ResolveField(shape.TypeName, sourceName, name => shape.TryGetField(name, out _));
    }

    private static RuriEngineMapping Parse(byte[] json, string origin)
    {
        return JsonSerializer.Deserialize<RuriEngineMapping>(json, Options)
            ?? throw new InvalidDataException($"[RuriEngine] {origin} holds no mapping table.");
    }
}
