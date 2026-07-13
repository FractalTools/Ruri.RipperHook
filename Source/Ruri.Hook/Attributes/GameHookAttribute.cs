using System;

namespace Ruri.Hook.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class GameHookAttribute : Attribute
    {
        public virtual string GameName { get; }
        public virtual string Version { get; }
        public virtual string BaseEngineVersion { get; }

        /// <summary>
        /// Extra version strings this same class also answers to, for a version whose resolved
        /// behavior is identical to <see cref="Version"/>. Each becomes its own selectable, listed
        /// hook id (see <see cref="Ruri.Hook.RuriHook.BuildHookIds"/>) resolving to this same class --
        /// so "this version is compatible with that one" is a literal string an implementation
        /// declares, not tribal knowledge an operator has to remember. Empty by default; a subtype
        /// that has no notion of aliasing never needs to override it.
        /// </summary>
        public virtual string[] AlsoCoversVersions { get; } = Array.Empty<string>();
    }
}
