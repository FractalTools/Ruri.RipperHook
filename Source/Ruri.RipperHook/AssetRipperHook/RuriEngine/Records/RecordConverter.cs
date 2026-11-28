using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class RecordConverter(RecordTable table, GapReport gaps, string file)
{
    private const string PersistentCallsMember = "PersistentCalls";
    private const string CallsMember = "Calls";
    private const string EnabledKey = "$enabled";
    private const string ValueMember = "Value";
    private const string AssetMember = "assetId";
    private const string EntityMember = "entity";
    private const string ComponentTypeMember = "componentType";
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static readonly Func<IReadOnlyList<IReadOnlyList<ChildRecord>>> NoChildren = static () => [];

    private sealed class State(RecordEntry entry)
    {
        public RecordEntry Entry { get; } = entry;

        public ulong MemberKey { get; init; }

        public bool IsListElement { get; init; }

        public bool AllowPersistentEvents { get; init; } = true;

        public List<KeyValuePair<string, ObjectValue>> PersistentEvents { get; init; } = new();

        public HashSet<string> Exact { get; init; } = new(StringComparer.Ordinal);

        public HashSet<string> Containers { get; init; } = new(StringComparer.Ordinal);

        public State ForListElements(ulong memberKey) => new(Entry) { MemberKey = memberKey, IsListElement = true, AllowPersistentEvents = AllowPersistentEvents, PersistentEvents = PersistentEvents };

        public State ForElements(ulong memberKey) => new(Entry) { MemberKey = memberKey, AllowPersistentEvents = false };
    }

    private sealed record SourceContext(ObjectValue Fields, ReferenceValue? Record, string HostName, Func<IReadOnlyList<IReadOnlyList<ChildRecord>>> Children, long Index);

    public ObjectValue Convert(RecordEntry entry, RecordClass row)
    {
        State state = new(entry);
        foreach (string path in ClaimedPaths(row))
        {
            state.Exact.Add(path);
            for (int separator = path.LastIndexOf('.'); separator > 0; separator = path.LastIndexOf('.', separator - 1))
            {
                state.Containers.Add(path[..separator]);
            }
        }

        SourceContext root = new(entry.Fields, null, entry.HostName, entry.Children, 0);
        ObjectValue components = new();
        if (row.Component is { } componentType && !row.TargetOnly)
        {
            ObjectValue component = MapPlan(table.Structs[componentType], row.Fields, root, state);
            if (!entry.Enabled && !row.AlwaysEnabled)
            {
                if (table.Structs[componentType].Enableable)
                {
                    component.Add(EnabledKey, ScalarValue.FromBoolean(false));
                }
                else
                {
                    gaps.Add(GapCategories.ShapeMismatch, $"{entry.ClassName}: disabled record, {componentType} is not enableable", file);
                }
            }
            components.Add(componentType, component);
        }
        foreach (ExtraComponent extra in row.Extra)
        {
            components.Add(extra.Component, MapPlan(table.Structs[extra.Component], extra.Fields, root, state));
        }
        ReportUnclaimed(entry.Fields, string.Empty, null, state);

        foreach (RecordBuffer buffer in row.Buffers)
        {
            List<Value> elements = new();
            if (Navigate(entry.Fields, buffer.Member) is { } member)
            {
                MapElements(table.Structs[buffer.Element], member, buffer.Member, state.ForListElements(Fnv1a64(buffer.Member)), elements);
            }
            Merge(components, buffer.Element, elements);
        }
        foreach (ElementGroup group in row.Elements)
        {
            RecordStruct plan = table.Structs[group.Element];
            List<Value> elements = new();
            foreach (Dictionary<string, FieldSource> item in group.Items)
            {
                elements.Add(MapPlan(plan, item, root, new State(entry)));
            }
            Merge(components, group.Element, elements);
        }
        foreach (FlattenBuffer buffer in row.Flatten)
        {
            List<Value> elements = new();
            if (Navigate(entry.Fields, buffer.Member) is ObjectValue start)
            {
                Flatten(buffer, 0, start, new Dictionary<string, Value>(), elements, state);
            }
            Merge(components, buffer.Element, elements);
        }
        foreach (ChildBuffer buffer in row.Children)
        {
            RecordStruct plan = table.Structs[buffer.Element];
            List<Value> elements = new();
            foreach (IReadOnlyList<ChildRecord> node in entry.Children())
            {
                if (node.FirstOrDefault(record => record.Row.Roles.ContainsKey(buffer.Role)) is { } chosen)
                {
                    elements.Add(MapPlan(plan, chosen.Row.Roles[buffer.Role], new SourceContext(chosen.Fields, chosen.Reference, string.Empty, NoChildren, 0), new State(entry)));
                }
            }
            Merge(components, buffer.Element, elements);
        }
        if (state.PersistentEvents.Count > 0)
        {
            Merge(components, table.PersistentEventBuffer, MapPersistentEvents(state));
        }
        return components;
    }

    private static IEnumerable<string> ClaimedPaths(RecordClass row)
    {
        IEnumerable<FieldSource> sources = row.Fields.Values
            .Concat(row.Extra.SelectMany(static extra => extra.Fields.Values))
            .Concat(row.Elements.SelectMany(static group => group.Items.SelectMany(static item => item.Values)));
        return row.Ignore
            .Concat(row.Buffers.Select(static buffer => buffer.Member))
            .Concat(row.Flatten.Select(static buffer => buffer.Member))
            .Concat(sources.Where(static source => source.From is not null).Select(static source => source.From!));
    }

    private static void Merge(ObjectValue components, string elementType, List<Value> elements)
    {
        if (elements.Count == 0)
        {
            return;
        }
        if (components.TryGet(elementType, out Value? known) && known is ArrayValue existing)
        {
            existing.Items.AddRange(elements);
            return;
        }
        ArrayValue array = new();
        array.Items.AddRange(elements);
        components.Add(elementType, array);
    }

    private static Value? Navigate(ObjectValue root, string path)
    {
        Value current = root;
        foreach (string part in path.Split('.'))
        {
            if (current is not ObjectValue holder || !holder.TryGet(part, out Value? next))
            {
                return null;
            }
            current = next;
        }
        return current;
    }

    private ObjectValue MapPlan(RecordStruct plan, Dictionary<string, FieldSource> sources, SourceContext context, State state)
    {
        ObjectValue result = new();
        foreach (RecordField field in plan.Fields)
        {
            if (IsReserved(field.Kind))
            {
                AssignReserved(result, field, state, state.MemberKey);
                continue;
            }
            if (!sources.TryGetValue(field.Name, out FieldSource? source) || SourceValue(source, context) is not { } value)
            {
                continue;
            }
            if (ConvertField(field, value, field.Name, state) is { } converted)
            {
                result.Add(field.Name, converted);
            }
        }
        return result;
    }

    private static Value? SourceValue(FieldSource source, SourceContext context)
    {
        if (source.Const is { } constant)
        {
            return Literal(constant);
        }
        if (source.Record)
        {
            return context.Record;
        }
        if (source.HostName)
        {
            return ScalarValue.FromText(context.HostName);
        }
        if (source.Index)
        {
            return ScalarValue.FromInteger(context.Index);
        }
        if (source.Child is { } role)
        {
            return context.Children().SelectMany(static node => node).FirstOrDefault(record => record.Row.Roles.ContainsKey(role))?.Reference;
        }
        if (source.From is { } path && Navigate(context.Fields, path) is { } found)
        {
            return found;
        }
        return source.Default is { } fallback ? Literal(fallback) : null;
    }

    private static Value Literal(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => ScalarValue.FromBoolean(true),
            JsonValueKind.False => ScalarValue.FromBoolean(false),
            JsonValueKind.String => ScalarValue.FromText(element.GetString() ?? string.Empty),
            JsonValueKind.Number when element.TryGetInt64(out long integer) => ScalarValue.FromInteger(integer),
            JsonValueKind.Number when element.TryGetUInt64(out ulong unsigned) => ScalarValue.FromUnsigned(unsigned),
            JsonValueKind.Number => ScalarValue.FromDouble(element.GetDouble()),
            _ => NullValue.Instance,
        };
    }

    private void Flatten(FlattenBuffer buffer, int depth, ObjectValue holder, Dictionary<string, Value> carried, List<Value> elements, State state)
    {
        FlattenLevel level = buffer.Levels[depth];
        if (!holder.TryGet(level.List, out Value? list) || list is not ArrayValue items)
        {
            return;
        }
        RecordStruct plan = table.Structs[buffer.Element];
        for (int index = 0; index < items.Items.Count; index++)
        {
            if (items.Items[index] is not ObjectValue item)
            {
                continue;
            }
            Dictionary<string, Value> row = new(carried);
            SourceContext context = new(item, null, string.Empty, NoChildren, index);
            foreach (KeyValuePair<string, FieldSource> field in level.Fields)
            {
                if (SourceValue(field.Value, context) is { } value)
                {
                    row[field.Key] = value;
                }
            }
            bool leaf = depth + 1 == buffer.Levels.Length || !(item.TryGet(buffer.Levels[depth + 1].List, out Value? next) && next is ArrayValue { Items.Count: > 0 });
            if (!leaf)
            {
                Flatten(buffer, depth + 1, item, row, elements, state);
                continue;
            }
            ObjectValue element = new();
            foreach (RecordField field in plan.Fields)
            {
                if (row.TryGetValue(field.Name, out Value? value) && ConvertField(field, value, field.Name, state) is { } converted)
                {
                    element.Add(field.Name, converted);
                }
            }
            elements.Add(element);
        }
    }

    private ObjectValue MapStruct(RecordStruct plan, ObjectValue source, string path, State state, bool strict, out int claimed)
    {
        ObjectValue result = new();
        HashSet<string> used = new(StringComparer.Ordinal);
        ulong memberKey = MemberKeyOf(source, path, state);
        foreach (RecordField field in plan.Fields)
        {
            if (IsReserved(field.Kind))
            {
                AssignReserved(result, field, state, memberKey);
                continue;
            }
            if (field.Member is not { } member || !source.TryGet(member, out Value? value))
            {
                continue;
            }
            if (!used.Add(member))
            {
                gaps.Add(GapCategories.ShapeMismatch, $"{Combine(path, member)}: claimed by two fields", file);
                continue;
            }
            Value? converted = field.Kind == "ActionOwner"
                ? ResolveOwner(field, value, result, Combine(path, member))
                : ConvertField(field, value, Combine(path, member), state);
            if (converted is not null)
            {
                result.Add(field.Name, converted);
            }
        }
        claimed = used.Count;
        if (strict)
        {
            ReportUnclaimed(source, path, used, state);
        }
        return result;
    }

    private static bool IsReserved(string kind) => kind is "MemberKey" or "Self" or "Name";

    private void AssignReserved(ObjectValue result, RecordField field, State state, ulong memberKey)
    {
        switch (field.Kind)
        {
            case "MemberKey":
                result.Add(field.Name, ScalarValue.FromUnsigned(memberKey));
                break;
            case "Self":
                result.Add(field.Name, ScalarValue.FromText(state.Entry.Self.ToString("N", CultureInfo.InvariantCulture)));
                break;
            case "Name":
                if (Encoding.UTF8.GetByteCount(state.Entry.Name) > field.Capacity)
                {
                    gaps.Add(GapCategories.ShapeMismatch, $"{state.Entry.ClassName}.{field.Name}: name longer than {field.Capacity} bytes", file);
                    break;
                }
                result.Add(field.Name, ScalarValue.FromText(state.Entry.Name));
                break;
        }
    }

    private Value? ResolveOwner(RecordField field, Value value, ObjectValue result, string where)
    {
        if (value is not ScalarValue { Kind: ScalarKind.Text } declared)
        {
            return Mismatch(where, value, "text");
        }
        string owner = declared.Text;
        if (field.Owner is { } sibling
            && result.TryGet(sibling, out Value? pointer)
            && pointer is ObjectValue holder
            && holder.TryGet(ComponentTypeMember, out Value? actual)
            && actual is ScalarValue { Kind: ScalarKind.Text } resolved
            && resolved.Text.Length > 0)
        {
            owner = resolved.Text;
        }
        else if (table.ComponentTypeOfName(owner) is { } engine)
        {
            owner = engine;
        }
        return Encoding.UTF8.GetByteCount(owner) <= field.Capacity ? ScalarValue.FromText(owner) : Mismatch(where, value, $"text up to {field.Capacity} bytes");
    }

    private static string Combine(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static ulong MemberKeyOf(ObjectValue source, string path, State state)
    {
        if (!state.IsListElement || !state.AllowPersistentEvents)
        {
            return state.MemberKey;
        }
        string? found = null;
        foreach (KeyValuePair<string, Value> entry in source.Fields)
        {
            if (entry.Value is not ObjectValue record || !IsPersistentEvent(record))
            {
                continue;
            }
            if (found is not null)
            {
                return state.MemberKey;
            }
            found = entry.Key;
        }
        return found is null ? state.MemberKey : Fnv1a64(Combine(path, found));
    }

    private static bool IsPersistentEvent(ObjectValue record)
    {
        return record.Count == 1
            && record.TryGet(PersistentCallsMember, out Value? calls)
            && calls is ObjectValue holder
            && holder.Fields.All(static entry => entry.Key == CallsMember);
    }

    private void ReportUnclaimed(ObjectValue source, string path, HashSet<string>? used, State state)
    {
        foreach (KeyValuePair<string, Value> entry in source.Fields)
        {
            if (used is not null && used.Contains(entry.Key))
            {
                continue;
            }
            string child = Combine(path, entry.Key);
            if (state.Exact.Contains(child))
            {
                continue;
            }
            if (entry.Value is ObjectValue record)
            {
                if (state.AllowPersistentEvents && table.PersistentEventBuffer.Length > 0 && IsPersistentEvent(record))
                {
                    state.PersistentEvents.Add(new KeyValuePair<string, ObjectValue>(child, record));
                    continue;
                }
                if (state.Containers.Contains(child))
                {
                    ReportUnclaimed(record, child, null, state);
                    continue;
                }
            }
            gaps.Add(GapCategories.IgnoredField, $"{state.Entry.ClassName}.{child}", file);
        }
    }

    private Value? ConvertField(RecordField field, Value value, string where, State state)
    {
        switch (field.Kind)
        {
            case "Boolean":
                return value is ScalarValue { Kind: ScalarKind.Boolean or ScalarKind.Integer or ScalarKind.Unsigned } flag
                    ? ScalarValue.FromBoolean(flag.AsBoolean)
                    : Mismatch(where, value, "boolean");
            case "Integer":
            case "Enumeration":
                return value is ScalarValue { IsIntegral: true } integer && FitsInteger(field.Type, integer)
                    ? integer
                    : Mismatch(where, value, field.Type);
            case "Number":
                return value is ScalarValue { IsNumber: true } number
                    ? field.Type == "Single" ? ScalarValue.FromSingle((float)number.AsDouble) : ScalarValue.FromDouble(number.AsDouble)
                    : Mismatch(where, value, "number");
            case "Text":
                return value is ScalarValue { Kind: ScalarKind.Text } text && Encoding.UTF8.GetByteCount(text.Text) <= field.Capacity
                    ? text
                    : Mismatch(where, value, $"text up to {field.Capacity} bytes");
            case "TextHash":
                return value is ScalarValue { Kind: ScalarKind.Text } hashed ? ScalarValue.FromUnsigned(Fnv1a64(hashed.Text)) : Mismatch(where, value, "text");
            case "Structure":
                RecordStruct nested = table.Structs[field.Struct!];
                ObjectValue? mapped = value switch
                {
                    ObjectValue record => MapStruct(nested, record, where, state, true, out _),
                    ReferenceValue pointer => MapPointer(nested, pointer, where, state),
                    _ => null,
                };
                if (mapped is null)
                {
                    return Mismatch(where, value, "object");
                }
                return mapped.Count == 0 ? null : mapped;
            case "NodeReference":
            case "AssetReference":
                return value switch
                {
                    ReferenceValue reference => TargetOf(reference),
                    ScalarValue { Kind: ScalarKind.Text } or NullValue => value,
                    _ => Mismatch(where, value, "reference"),
                };
            default:
                return Mismatch(where, value, field.Kind);
        }
    }

    private static Value TargetOf(ReferenceValue reference)
    {
        return reference.IsNull ? NullValue.Instance : ScalarValue.FromText(reference.Target.ToString("N", CultureInfo.InvariantCulture));
    }

    private ObjectValue MapPointer(RecordStruct plan, ReferenceValue pointer, string where, State state)
    {
        ObjectValue members = new();
        if (!pointer.IsNull)
        {
            members.Add(AssetMember, TargetOf(pointer));
            members.Add(EntityMember, TargetOf(pointer));
            members.Add(ComponentTypeMember, ScalarValue.FromText(pointer.ComponentType));
        }
        ObjectValue mapped = MapStruct(plan, members, where, state, false, out int claimed);
        if (!pointer.IsNull && claimed == 0)
        {
            gaps.Add(GapCategories.ShapeMismatch, $"{where}: pointer struct has no field reading {AssetMember}, {EntityMember} or {ComponentTypeMember}", file);
        }
        return mapped;
    }

    private void MapElements(RecordStruct plan, Value value, string path, State state, List<Value> elements)
    {
        switch (value)
        {
            case ArrayValue list:
                for (int index = 0; index < list.Items.Count; index++)
                {
                    elements.Add(MapElement(plan, list.Items[index], $"{path}[{index}]", state));
                }
                break;
            case ObjectValue or ReferenceValue:
                elements.Add(MapElement(plan, value, path, state));
                break;
            case ScalarValue { Kind: ScalarKind.Text } text:
                byte[] bytes = Encoding.UTF8.GetBytes(text.Text);
                for (int index = 0; index < bytes.Length; index++)
                {
                    elements.Add(MapElement(plan, ScalarValue.FromInteger(bytes[index]), $"{path}[{index}]", state));
                }
                break;
            default:
                elements.Add(MapElement(plan, value, path, state));
                break;
        }
    }

    private Value MapElement(RecordStruct plan, Value element, string path, State state)
    {
        switch (element)
        {
            case ObjectValue record:
                return MapStruct(plan, record, path, state, true, out _);
            case ReferenceValue pointer:
                return MapPointer(plan, pointer, path, state);
            default:
                ObjectValue wrapper = new();
                wrapper.Add(ValueMember, element);
                return MapStruct(plan, wrapper, path, state, false, out _);
        }
    }

    private List<Value> MapPersistentEvents(State state)
    {
        RecordStruct plan = table.Structs[table.PersistentEventBuffer];
        List<Value> elements = new();
        foreach (KeyValuePair<string, ObjectValue> use in state.PersistentEvents)
        {
            if (!use.Value.TryGet(PersistentCallsMember, out Value? holder) || holder is not ObjectValue calls
                || !calls.TryGet(CallsMember, out Value? list) || list is not ArrayValue items)
            {
                continue;
            }
            State callState = state.ForElements(Fnv1a64(use.Key));
            string callsPath = $"{use.Key}.{PersistentCallsMember}.{CallsMember}";
            for (int index = 0; index < items.Items.Count; index++)
            {
                if (items.Items[index] is ObjectValue call)
                {
                    elements.Add(MapStruct(plan, call, $"{callsPath}[{index}]", callState, true, out _));
                }
            }
        }
        return elements;
    }

    private static bool FitsInteger(string type, ScalarValue value)
    {
        Int128 number = value.Kind == ScalarKind.Unsigned ? value.Unsigned : value.Signed;
        return type switch
        {
            "SByte" => number >= sbyte.MinValue && number <= sbyte.MaxValue,
            "Byte" => number >= byte.MinValue && number <= byte.MaxValue,
            "Int16" => number >= short.MinValue && number <= short.MaxValue,
            "UInt16" => number >= ushort.MinValue && number <= ushort.MaxValue,
            "Int32" => number >= int.MinValue && number <= int.MaxValue,
            "UInt32" => number >= uint.MinValue && number <= uint.MaxValue,
            "Int64" => number >= long.MinValue && number <= long.MaxValue,
            "UInt64" => number >= 0 && number <= ulong.MaxValue,
            _ => false,
        };
    }

    private Value? Mismatch(string where, Value value, string wanted)
    {
        gaps.Add(GapCategories.ShapeMismatch, $"{where}: expected {wanted}, found {value.GetType().Name}", file);
        return null;
    }

    private static ulong Fnv1a64(string text)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte item in Encoding.UTF8.GetBytes(text))
        {
            hash = unchecked((hash ^ item) * FnvPrime);
        }
        return hash;
    }
}
