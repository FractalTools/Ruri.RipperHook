using System;
using System.Globalization;

namespace Ruri.RipperHook.Core.TypeTree;

public readonly record struct TypeTreeVersion(string Lineage, string Version)
{
    public TypeTreeVersion(CustomEngineType engine, string version)
        : this(((int)engine).ToString(CultureInfo.InvariantCulture), version)
    {
    }

    public bool IsEmpty => string.IsNullOrEmpty(Lineage) || string.IsNullOrEmpty(Version);

    public override string ToString() => $"{Lineage}/{Version}";
}
