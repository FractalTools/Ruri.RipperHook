using System;

namespace Ruri.RipperHook.Core.Capabilities;

/// <summary>
/// Marks a static class as a container of <see cref="SinceAttribute"/>-tagged capability methods
/// for a game. <see cref="CapabilityResolver"/> scans every loaded assembly for classes carrying
/// this attribute to find a game's full capability surface -- the only place a new capability
/// container needs to announce itself. Capability classes are organised by concern (decryption,
/// mesh layout, shader binding, ...), not by version -- a version is a resolved build number,
/// never a class or a file.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class GameCapabilitiesAttribute : Attribute
{
    public GameType GameType { get; }

    public GameCapabilitiesAttribute(GameType gameType)
    {
        GameType = gameType;
    }
}
