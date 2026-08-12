using System;

namespace Ruri.RipperHook.Core.Capabilities;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FeedsModuleAttribute : Attribute
{
    public Type ModuleType { get; }
    public string StaticFieldName { get; }

    public FeedsModuleAttribute(Type moduleType, string staticFieldName)
    {
        ModuleType = moduleType;
        StaticFieldName = staticFieldName;
    }
}
