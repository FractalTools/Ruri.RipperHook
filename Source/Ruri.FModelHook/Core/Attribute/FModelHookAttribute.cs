using Ruri.Hook.Attributes;

namespace Ruri.FModelHook.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FModelHookAttribute : GameHookAttribute
{
    public GameType GameType { get; }
    public override string GameName { get => GameType.ToString(); }
    public override string Version { get; }
    public override string EngineVersion { get; }

    public FModelHookAttribute(GameType gameType, string version = "", string engineVersion = "")
    {
        GameType = gameType;
        Version = version;
        EngineVersion = engineVersion;
    }
}
