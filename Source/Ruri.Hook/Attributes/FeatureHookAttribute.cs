using System;

namespace Ruri.Hook.Attributes
{
    /// <summary>
    /// A hook that is about no game at all: a host capability (an exporter, a processing
    /// stage, a dump). Features carry no version and never exclude each other, which is the
    /// whole difference from <see cref="GameHookAttribute"/> -- a difference that is now the
    /// attribute's TYPE rather than a flag plus a name prefix every host had to re-test.
    ///
    /// A host declares which features its own path needs; a user never picks them to make a
    /// game readable.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class FeatureHookAttribute : Attribute
    {
        public abstract string Name { get; }
    }
}
