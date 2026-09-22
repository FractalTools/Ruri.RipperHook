using System;

namespace Ruri.RipperHook.Core.Capabilities;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SinceAttribute : Attribute
{
    public string Version { get; }

    public SinceAttribute(string version)
    {
        Version = version;
    }
}
