using System;

namespace Ruri.RipperHook.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperHookAttribute : GameHookAttribute
{
    public GameType GameType { get; }
    public override string GameName { get => GameType.ToString(); }
    public override string Version { get; }
    public override string BaseEngineVersion { get; }

    public RipperHookAttribute(GameType gameType, string version = "", string baseEngineVersion = "")
    {
        GameType = gameType;
        Version = version;
        BaseEngineVersion = baseEngineVersion;
    }
}
