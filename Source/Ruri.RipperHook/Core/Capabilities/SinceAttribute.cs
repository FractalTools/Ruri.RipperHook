using System;

namespace Ruri.RipperHook.Core.Capabilities;

/// <summary>
/// Marks the engine build number (inclusive lower bound) from which a capability method is the
/// active implementation for its slot. Multiple capabilities can share a slot (a retarget target,
/// or a module's swappable delegate) across different builds; <see cref="CapabilityResolver"/>
/// picks the one with the highest <see cref="Build"/> not exceeding the resolved game's build.
/// A version that changes nothing needs no new capability at all; one that changes one thing
/// needs exactly one new tagged method sharing the slot of what it replaces.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SinceAttribute : Attribute
{
    public int Build { get; }

    public SinceAttribute(int build)
    {
        Build = build;
    }
}
