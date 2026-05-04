using System;

namespace Ruri.Hook.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class GameHookAttribute : Attribute
    {
        public virtual string GameName { get; }
        public virtual string Version { get; }
        public virtual string BaseEngineVersion { get; }
    }
}
