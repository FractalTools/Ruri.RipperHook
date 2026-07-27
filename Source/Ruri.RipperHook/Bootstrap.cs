using System;
using System.Runtime.Loader;
using Ruri.Hook.Config;

namespace Ruri.RipperHook;

/// <summary>
/// Shared startup helpers used by both the CLI and GUI entry-point executables.
/// </summary>
public static class Bootstrap
{
    private static bool _resolverInstalled;

    /// <summary>
    /// Install the assembly version-mismatch resolver (Ruri.SourceGenerated, etc., target older
    /// AssetRipper.Assets versions and otherwise fail to bind).
    /// </summary>
    public static void InstallAssemblyResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name == name.Name)
                    return loaded;
            }
            return null;
        };
    }

    /// <summary>
    /// Apply every enabled hook in <paramref name="config"/>. Wraps
    /// <see cref="Ruri.Hook.RuriHook.ApplyHooks"/> for callers that don't want to reach into the
    /// Ruri.Hook namespace directly.
    /// </summary>
    public static void ApplyHooks(HookConfig config)
    {
        Hook.RuriHook.ApplyHooks(config);
    }
}
