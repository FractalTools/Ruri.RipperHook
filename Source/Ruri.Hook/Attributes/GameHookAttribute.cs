using System;

namespace Ruri.Hook.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class GameHookAttribute : Attribute
    {
        public abstract string GameName { get; }
        public abstract string Version { get; }
        public abstract string BaseEngineVersion { get; }

        /// <summary>
        /// Whether this hook is about ONE GAME, as opposed to a host-wide feature.
        /// <para>A game hook owns that title's whole reading side -- decryption where the game has
        /// any, class-layout and type-tree fixes, and the readers that turn its own containers into
        /// data. It is NOT "the hook a game needs to be decrypted": a game with no encryption at all
        /// still has one, because that is where its parsing lives.</para>
        /// <para>Game hooks are MUTUALLY EXCLUSIVE (see <see cref="RuriHook.ApplyHooks"/>): two of
        /// them patch the same methods with different games' layouts, so enabling both does not
        /// produce a host that reads both -- it produces one that reads neither correctly. Feature
        /// hooks say false and combine freely.</para>
        /// </summary>
        public virtual bool IsGameSpecific => true;

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
