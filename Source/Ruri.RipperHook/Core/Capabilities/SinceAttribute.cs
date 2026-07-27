using System;

namespace Ruri.RipperHook.Core.Capabilities;

/// <summary>
/// Marks the game version (inclusive lower bound) from which a capability method is the active
/// implementation for its slot. Multiple capabilities can share a slot (a retarget target, a
/// module's swappable delegate, a type tree node override) across different versions;
/// <see cref="CapabilityResolver"/> picks the one with the highest <see cref="Version"/> not
/// exceeding the version the game hook declares. A version that changes nothing needs no capability
/// at all; one that changes one thing needs exactly one new tagged method sharing the slot of what
/// it replaces.
///
/// The version is the game's own string, exactly as the hook declares it in
/// <c>[RipperHook(..., "1.4.4")]</c>, ordered by <see cref="VersionKey"/>. It used to be an int,
/// which forced every build to be flattened into digits (<c>1.4.4</c> -&gt; <c>1404</c>) -- lossy
/// past five digits, and ambiguous besides, since <c>1.0.14</c> and <c>10.1.4</c> both became 1014.
/// Use <c>"0"</c> for "every version of this game".
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SinceAttribute : Attribute
{
    public string Version { get; }

    public SinceAttribute(string version)
    {
        Version = version;
    }
}
