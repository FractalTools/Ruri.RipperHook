using System;

namespace Ruri.RipperHook.Core.Capabilities;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class GameCapabilitiesAttribute : Attribute
{
    public GameType GameType { get; }

    public GameCapabilitiesAttribute(GameType gameType)
    {
        GameType = gameType;
    }
}
