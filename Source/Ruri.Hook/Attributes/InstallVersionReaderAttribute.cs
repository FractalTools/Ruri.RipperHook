using System;

namespace Ruri.Hook.Attributes
{
    /// <summary>
    /// Declares that this class can state the engine version of an install of
    /// <see cref="Product"/> whose files the generic reader cannot parse -- a build that
    /// transforms what it publishes answers only through its own game's code.
    ///
    /// The class must expose <c>public static string ReadEngineVersion(string dataFolder)</c>,
    /// returning "" when it finds nothing. Declaring the attribute without that method is an
    /// error raised at the first probe, not a silent absence.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class InstallVersionReaderAttribute : Attribute
    {
        public abstract string Product { get; }
    }
}
