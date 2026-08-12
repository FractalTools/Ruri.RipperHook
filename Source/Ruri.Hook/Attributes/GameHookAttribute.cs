using System;

namespace Ruri.Hook.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class GameHookAttribute : Attribute
    {
        public abstract string GameName { get; }
        public abstract string Version { get; }
        public abstract string BaseEngineVersion { get; }

        public virtual bool IsGameSpecific => true;

        public virtual string[] AlsoCoversVersions { get; } = Array.Empty<string>();
    }
}
