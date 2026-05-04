using Ruri.Hook.Attributes;

namespace Ruri.FModelHook.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FModelHookAttribute : GameHookAttribute
{
    public GameType GameType { get; }
    public override string GameName { get => GameType.ToString(); }
    public override string Version { get; }
    public override string BaseEngineVersion { get; }

    public FModelHookAttribute(GameType gameType, string version = "", string baseEngineVersion = "")
    {
        GameType = gameType;
        Version = version;
        BaseEngineVersion = baseEngineVersion;
    }
}
