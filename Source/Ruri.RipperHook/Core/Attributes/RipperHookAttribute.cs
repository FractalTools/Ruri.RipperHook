using System;

namespace Ruri.RipperHook.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperHookAttribute : GameHookAttribute
{
    public GameType GameType { get; }
    public override string GameName { get => GameType.ToString(); }
    public override string Version { get; }
    public override string BaseEngineVersion { get; }

    public override bool IsGameSpecific => GameType.IsGame();

    public override string[] AlsoCoversVersions { get; }

    public RipperHookAttribute(GameType gameType, string version = "", string baseEngineVersion = "", params string[] alsoCoversVersions)
    {
        GameType = gameType;
        Version = version;
        BaseEngineVersion = baseEngineVersion;
        AlsoCoversVersions = alsoCoversVersions ?? Array.Empty<string>();
    }
}
