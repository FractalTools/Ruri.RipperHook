namespace Ruri.RipperHook.RuriEngine;

internal sealed record RecordEntry(string ClassName, bool Enabled, string Name, ObjectValue Fields, Guid Self, int DocumentOrder);
