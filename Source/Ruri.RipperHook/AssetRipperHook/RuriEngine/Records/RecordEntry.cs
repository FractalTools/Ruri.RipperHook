namespace Ruri.RipperHook.RuriEngine;

internal sealed record RecordEntry(string ClassName, bool Enabled, string Name, string HostName, ObjectValue Fields, Guid Self, Func<IReadOnlyList<IReadOnlyList<ChildRecord>>> Children);

internal sealed record ChildRecord(RecordClass Row, ObjectValue Fields, ReferenceValue Reference);
