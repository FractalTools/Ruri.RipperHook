using System.Text.Json;
using AssetRipper.Assets;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class RecordField
{
    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public int Capacity { get; init; }

    public string? Struct { get; init; }

    public string? Member { get; init; }

    public string? Owner { get; init; }
}

internal sealed class RecordStruct
{
    public string Assembly { get; init; } = string.Empty;

    public bool Enableable { get; init; }

    public RecordField[] Fields { get; init; } = [];
}

internal sealed class FieldSource
{
    public string? From { get; init; }

    public JsonElement? Default { get; init; }

    public JsonElement? Const { get; init; }

    public bool Record { get; init; }

    public bool HostName { get; init; }

    public bool Index { get; init; }

    public string? Child { get; init; }
}

internal sealed class RecordBuffer
{
    public string Member { get; init; } = string.Empty;

    public string Element { get; init; } = string.Empty;
}

internal sealed class ElementGroup
{
    public string Element { get; init; } = string.Empty;

    public Dictionary<string, FieldSource>[] Items { get; init; } = [];
}

internal sealed class ChildBuffer
{
    public string Element { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;
}

internal sealed class FlattenLevel
{
    public string List { get; init; } = string.Empty;

    public Dictionary<string, FieldSource> Fields { get; init; } = new();
}

internal sealed class FlattenBuffer
{
    public string Member { get; init; } = string.Empty;

    public string Element { get; init; } = string.Empty;

    public FlattenLevel[] Levels { get; init; } = [];
}

internal sealed class ExtraComponent
{
    public string Component { get; init; } = string.Empty;

    public Dictionary<string, FieldSource> Fields { get; init; } = new();
}

internal sealed class RecordClass
{
    public string Class { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string Assembly { get; init; } = string.Empty;

    public string? Component { get; init; }

    public bool TargetOnly { get; init; }

    public bool AlwaysEnabled { get; init; }

    public string[] Ignore { get; init; } = [];

    public Dictionary<string, FieldSource> Fields { get; init; } = new();

    public ExtraComponent[] Extra { get; init; } = [];

    public RecordBuffer[] Buffers { get; init; } = [];

    public ElementGroup[] Elements { get; init; } = [];

    public FlattenBuffer[] Flatten { get; init; } = [];

    public ChildBuffer[] Children { get; init; } = [];

    public Dictionary<string, Dictionary<string, FieldSource>> Roles { get; init; } = new();

    public List<string> OutputTypes()
    {
        List<string> types = new();
        if (Component is { } component && !TargetOnly)
        {
            types.Add(component);
        }
        types.AddRange(Extra.Select(static extra => extra.Component));
        types.AddRange(Buffers.Select(static buffer => buffer.Element));
        types.AddRange(Elements.Select(static group => group.Element));
        types.AddRange(Flatten.Select(static buffer => buffer.Element));
        types.AddRange(Children.Select(static buffer => buffer.Element));
        return types;
    }
}

internal sealed class RecordTable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private Dictionary<string, List<RecordClass>>? byClass;

    public static RecordTable Empty { get; } = new();

    public int Format { get; init; }

    public string PersistentEventBuffer { get; init; } = string.Empty;

    public Dictionary<string, string> Tags { get; init; } = new();

    public string[] IgnoredTags { get; init; } = [];

    public RecordClass[] Classes { get; init; } = [];

    public Dictionary<string, RecordStruct> Structs { get; init; } = new();

    public static RecordTable Load(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return Empty;
        }
        return JsonSerializer.Deserialize<RecordTable>(File.ReadAllBytes(path), Options)
            ?? throw new InvalidDataException($"[RuriEngine] {path} holds no record table.");
    }

    public bool TryFind(string className, string? @namespace, string? assembly, [NotNullWhen(true)] out RecordClass? row)
    {
        byClass ??= Classes.GroupBy(static item => item.Class, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        row = null;
        if (!byClass.TryGetValue(className, out List<RecordClass>? candidates))
        {
            return false;
        }
        foreach (RecordClass candidate in candidates)
        {
            if ((@namespace is null || candidate.Namespace.Length == 0 || candidate.Namespace == @namespace)
                && (assembly is null || candidate.Assembly.Length == 0 || candidate.Assembly == assembly))
            {
                if (row is not null)
                {
                    throw new InvalidDataException($"[RuriEngine] record class {className} matches two table rows.");
                }
                row = candidate;
            }
        }
        return row is not null;
    }

    public bool TryFind(IMonoScript script, [NotNullWhen(true)] out RecordClass? row)
    {
        return TryFind(script.ClassName_R.String, script.Namespace.String, AssemblyOf(script), out row);
    }

    public string ComponentTypeOf(IUnityObjectBase target)
    {
        bool found = target is IMonoBehaviour { ScriptP: { } script }
            ? TryFind(script, out RecordClass? scripted)
            : TryFind(target.ClassName, string.Empty, string.Empty, out scripted);
        return found && scripted!.Component is { } component ? component : string.Empty;
    }

    public string? ComponentTypeOfName(string assemblyQualifiedName)
    {
        int comma = assemblyQualifiedName.IndexOf(',');
        string name = (comma < 0 ? assemblyQualifiedName : assemblyQualifiedName[..comma]).Trim();
        int dot = name.LastIndexOf('.');
        string @namespace = dot < 0 ? string.Empty : name[..dot];
        string className = dot < 0 ? name : name[(dot + 1)..];
        return TryFind(className, @namespace, null, out RecordClass? row) && row.Component is { } component ? component : null;
    }

    private static string AssemblyOf(IMonoScript script)
    {
        const string LibrarySuffix = ".dll";
        string name = script.AssemblyName.String;
        return name.EndsWith(LibrarySuffix, StringComparison.OrdinalIgnoreCase) ? name[..^LibrarySuffix.Length] : name;
    }
}
