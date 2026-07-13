using System;

namespace Ruri.RipperHook.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperHookAttribute : GameHookAttribute
{
    public GameType GameType { get; }
    public override string GameName { get => GameType.ToString(); }
    public override string Version { get; }
    public override string BaseEngineVersion { get; }

    /// <summary>
    /// Extra version strings this same class also answers to -- for a version whose resolved
    /// capability set (see <see cref="Ruri.RipperHook.Core.Capabilities.CapabilityResolver"/>) is
    /// identical to <see cref="Version"/>. Compatibility facts like "1.3.3 behaves like 1.2.4"
    /// become a literal, greppable string next to the code that describes it instead of an
    /// undocumented convention the operator has to remember.
    /// </summary>
    public override string[] AlsoCoversVersions { get; }

    public RipperHookAttribute(GameType gameType, string version = "", string baseEngineVersion = "", params string[] alsoCoversVersions)
    {
        GameType = gameType;
        Version = version;
        BaseEngineVersion = baseEngineVersion;
        AlsoCoversVersions = alsoCoversVersions ?? Array.Empty<string>();
    }
}
