using System;

namespace Ruri.RipperHook.Attributes;

/// <summary>
/// A capability of the RIPPER, not of a game: an exporter, a processing stage, a dump. It has
/// no game, no version and no exclusivity, and it is never what makes a game readable -- a host
/// turns on the ones its own path needs (see <c>RipperBlenderBridge.HostFeatures</c>) and a
/// user picking games never sees them.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperFeatureAttribute : FeatureHookAttribute
{
    public override string Name { get; }

    public RipperFeatureAttribute(string name)
    {
        Name = name;
    }
}
