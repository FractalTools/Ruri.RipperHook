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
    public override string GameName { get; }
    public override string Version { get; }
    public override string EngineVersion { get; }

    /// <summary>A source-defined product name; no shared enum member is required.
    /// The legacy enum identity remains Unknown for names outside that enum.</summary>
    public RipperHookAttribute(string gameName, string version = "", string engineVersion = "")
        : this(GameType.Unknown, version, engineVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);
        GameName = gameName;
        if (Enum.TryParse(gameName, out GameType known) && known.ToString() == gameName)
            GameType = known;
    }

    public RipperHookAttribute(GameType gameType, string version = "", string engineVersion = "")
    {
        GameType = gameType;
        GameName = gameType.ToString();
        Version = version;
        EngineVersion = engineVersion;
    }
}
