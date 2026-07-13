using System;

namespace Ruri.RipperHook.Core.Capabilities;

/// <summary>
/// Marks a static method as the implementation feeding a hook module's swappable static delegate
/// field for a given engine build (paired with <see cref="SinceAttribute"/>). The method's
/// signature must match the field's delegate type exactly -- <see cref="CapabilityResolver"/>
/// builds a delegate from it via <c>Delegate.CreateDelegate</c> and assigns it directly to the
/// field, without constructing a module instance or calling
/// <c>RipperHookCommon.RegisterModule</c>. The module's own <c>[RetargetMethod]</c> trampoline is
/// installed at most once per resolved build, the first time any capability wins its slot --
/// switching which capability wins never re-installs it, since the trampoline only ever reads the
/// swappable field and never varies itself.
/// </summary>
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
