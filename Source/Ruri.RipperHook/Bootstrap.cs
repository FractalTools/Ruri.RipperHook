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
    /// Install the assembly version-mismatch resolver: a side-loaded assembly compiled against an
    /// older AssetRipper.Assets otherwise fails to bind, so redirect it to whatever is already loaded.
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
    /// Hooks every AssetRipper host runs no matter what was selected, because leaving one off does
    /// not produce a different valid export -- it produces a wrong one.
    /// <para><c>AR_SerializeReference_</c>: without it AssetRipper discards the entire field
    /// structure of any MonoBehaviour carrying a <c>ManagedReferencesRegistry</c> and writes a
    /// field-less shell instead, with no error. EndField's facial-morph avatars go from 700 KB of
    /// ctrl-to-bone table to 416 bytes of nothing that way.</para>
    /// Declared here, ONCE. The CLI, the GUI and the in-process Blender bridge all reach
    /// <see cref="Ruri.Hook.RuriHook.ApplyHooks"/> through <see cref="ApplyHooks"/> below, so no
    /// host can forget one. Each of them used to keep its own copy of this list, and the Blender
    /// bridge's copy was missing AR_SerializeReference_ -- every character's face silently bound to
    /// nothing. A per-host list is a per-host way to be wrong; this is the fix for that, not just
    /// for the one id.
    /// </summary>
    public static readonly string[] AlwaysOnHookIds = new[] { "AR_SerializeReference_" };

    /// <summary>
    /// Apply every enabled hook in <paramref name="config"/>, plus <see cref="AlwaysOnHookIds"/>.
    /// Wraps <see cref="Ruri.Hook.RuriHook.ApplyHooks"/> for callers that don't want to reach into
    /// the Ruri.Hook namespace directly.
    /// </summary>
    public static void ApplyHooks(HookConfig config)
    {
        // Added to the caller's own set on purpose: ApplyHooks already self-heals that set in
        // place and hosts persist the result, so an always-on hook shows up in a host's config
        // honestly instead of being invisibly bolted on somewhere below.
        foreach (string id in AlwaysOnHookIds)
        {
            config.EnabledHooks.Add(id);
        }
        Hook.RuriHook.ApplyHooks(config);
    }
}
