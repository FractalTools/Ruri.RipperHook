using System;

namespace Ruri.Hook.Attributes
{
    /// <summary>
    /// Declares that this class can undo the transform ONE game puts on the files it
    /// publishes, so the generic reader can parse them like any other build's.
    ///
    /// The class must expose <c>public static bool TryDecrypt(byte[] data)</c>: recognise its
    /// own marker, undo the transform in place and return true, or leave the buffer alone and
    /// return false. It is asked only when the generic parse has already failed, and it is
    /// asked without any game being selected first -- which is the point, since reading a
    /// build's identity is what selects the game. Declaring the attribute without that method
    /// is an error raised at the first probe, not a silent absence.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public abstract class InstallVersionReaderAttribute : Attribute
    {
    }
}
