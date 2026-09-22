using System;

namespace Ruri.Hook.Attributes
{
    /// <summary>
    /// Declares that this class can read the identity of an install built on an engine the
    /// generic Unity probe does not understand, so the host learns which product and which
    /// engine a folder holds before any decoder is selected.
    ///
    /// The class must expose <c>public static IEnumerable&lt;PlayerIdentity&gt; Probe(string gameRoot)</c>:
    /// return every player it recognises under that folder, or an empty sequence when the
    /// folder is not its engine's install. It is asked without any game being selected first --
    /// reading the identity is what selects the game. Declaring the attribute without that
    /// method is an error raised at the first probe, not a silent absence.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class InstallProbeAttribute : Attribute
    {
    }
}
