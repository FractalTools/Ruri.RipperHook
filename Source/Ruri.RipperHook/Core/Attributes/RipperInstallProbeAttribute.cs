using System;
using Ruri.Hook.Attributes;

namespace Ruri.RipperHook.Attributes;

/// <summary>
/// This class reads the identity of installs built on an engine the generic Unity probe does
/// not understand. See <see cref="InstallProbeAttribute"/> for the contract.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RipperInstallProbeAttribute : InstallProbeAttribute
{
}
