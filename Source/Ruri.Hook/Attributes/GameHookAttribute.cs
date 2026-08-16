using System;

namespace Ruri.Hook.Attributes
{
    /// <summary>
    /// One game's decoder. Every value here is the identity the BUILD ITSELF carries, so a
    /// hook id joins to an install by string equality and nothing in between translates:
    /// <see cref="GameName"/> is the player's Unity productName, <see cref="Version"/> is the
    /// bundleVersion it applies FROM (see <see cref="Core.HookCatalog.Resolve"/>), and
    /// <see cref="EngineVersion"/> is the Unity version that build reports.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class GameHookAttribute : Attribute
    {
        public abstract string GameName { get; }

        public abstract string Version { get; }

        public abstract string EngineVersion { get; }
    }
}
