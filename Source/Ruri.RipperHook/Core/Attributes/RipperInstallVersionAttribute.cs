using System;

namespace Ruri.RipperHook.Attributes;

/// <summary>
/// This class states the engine version of an install of <see cref="GameType"/> that the
/// generic reader cannot: a build whose published files are transformed answers only through
/// its own game's code. See <see cref="InstallVersionReaderAttribute"/> for the contract.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperInstallVersionAttribute : InstallVersionReaderAttribute
{
    public GameType GameType { get; }

    public override string Product { get => GameType.ToString(); }

    public RipperInstallVersionAttribute(GameType gameType)
    {
        GameType = gameType;
    }
}
