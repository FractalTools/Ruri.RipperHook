using System;

namespace Ruri.RipperHook.Attributes;

/// <summary>
/// One game's decoder. <paramref name="version"/> is the game's own bundleVersion that this
/// decoder applies FROM: a build is read by the newest decoder at or below its version, so a
/// patch that changes nothing needs no edit here and a patch that breaks something gets its own
/// class. <paramref name="engineVersion"/> is the Unity version those builds report, which the
/// panel checks the install against.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperHookAttribute : GameHookAttribute
{
    public GameType GameType { get; }
    public override string GameName { get => GameType.ToString(); }
    public override string Version { get; }
    public override string EngineVersion { get; }

    public RipperHookAttribute(GameType gameType, string version = "", string engineVersion = "")
    {
        GameType = gameType;
        Version = version;
        EngineVersion = engineVersion;
    }
}
