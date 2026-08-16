using System;
using System.Runtime.Loader;
using Ruri.Hook.Config;

namespace Ruri.RipperHook;

public static class Bootstrap
{
    private static bool _resolverInstalled;

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

    public static readonly string[] AlwaysOnHookIds = new[] { "SerializeReference" };

    public static void ApplyHooks(HookConfig config)
    {
        foreach (string id in AlwaysOnHookIds)
        {
            config.EnabledHooks.Add(id);
        }
        Hook.RuriHook.ApplyHooks(config);
    }
}
