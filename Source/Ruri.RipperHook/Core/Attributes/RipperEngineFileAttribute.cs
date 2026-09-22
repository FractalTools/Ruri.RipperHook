using System;

namespace Ruri.RipperHook.Attributes;

/// <summary>
/// This class undoes the transform its game puts on the files that game publishes, so the
/// install probe can read them like any other build's. See
/// <see cref="InstallVersionReaderAttribute"/> for the contract.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperEngineFileAttribute : InstallVersionReaderAttribute
{
}
