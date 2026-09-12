using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook;

/// <summary>
/// Process-wide wiring every host runs before hooks: assembly resolution, and the module
/// assemblies a configuration names. A module is one more hook assembly -- its decoders and
/// probes join the catalog the moment it loads, since the catalog scans every loaded assembly --
/// living with its own dependencies, managed and native, in its own folder. That folder is what
/// the resolver probes for anything the default context cannot find on its own, so a module
/// built into another host's output (FModel's, for the Unreal decoder) loads here unchanged.
/// </summary>
public static class Bootstrap
{
    private static readonly object gate = new();
    private static readonly List<string> moduleDirectories = new();
    private static bool resolverInstalled;

    static Bootstrap()
    {
        HookCatalog.DeclareHost(typeof(RipperHookAttribute));
    }

    public static void InstallAssemblyResolver()
    {
        lock (gate)
        {
            if (resolverInstalled)
            {
                return;
            }
            resolverInstalled = true;
        }
        AssemblyLoadContext.Default.Resolving += ResolveManaged;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
    }

    public const string ModulesFolder = "Modules";
    public const string ModuleDeclarationExtension = ".module";

    /// <summary>
    /// Load every module the build declared beside this assembly: a module's build writes
    /// <c>Modules/&lt;name&gt;.module</c> into the kernel's output, one line naming the module
    /// assembly's absolute path, so a host needs no setting to find the decoders built into
    /// another output (FModel's, for the Unreal decoder) and an Unreal install is recognized the
    /// moment any host looks at it. A declared path that no longer exists is an error, never a
    /// silent skip.
    /// </summary>
    public static IReadOnlyList<Assembly> LoadDeclaredModules()
    {
        string directory = Path.Combine(Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location)!, ModulesFolder);
        List<Assembly> loaded = new();
        if (!Directory.Exists(directory))
        {
            return loaded;
        }
        foreach (string declaration in Directory.GetFiles(directory, "*" + ModuleDeclarationExtension).OrderBy(static file => file, StringComparer.OrdinalIgnoreCase))
        {
            string path = File.ReadLines(declaration).Select(static line => line.Trim()).FirstOrDefault(static line => line.Length > 0)
                ?? throw new InvalidDataException($"[Bootstrap] '{declaration}' declares no module path.");
            loaded.Add(LoadModule(path));
        }
        return loaded;
    }

    /// <summary>
    /// Load one hook assembly by path. Loading the same module twice yields the assembly already
    /// in the process; a path that does not exist is an error, never a silent skip.
    /// </summary>
    public static Assembly LoadModule(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Module assembly not found.", fullPath);
        }
        InstallAssemblyResolver();
        string directory = Path.GetDirectoryName(fullPath)!;
        lock (gate)
        {
            if (!moduleDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                moduleDirectories.Add(directory);
            }
        }
        AssemblyName name = AssemblyName.GetAssemblyName(fullPath);
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }
        Assembly module = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        HookLogger.Log($"[Bootstrap] Module loaded: {module.GetName().Name} from {directory}");
        return module;
    }

    public static void ApplyHooks(HookConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (string module in config.Modules)
        {
            LoadModule(module);
        }
        Hook.RuriHook.ApplyHooks(config);
    }

    private static Assembly? ResolveManaged(AssemblyLoadContext context, AssemblyName name)
    {
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (loaded.GetName().Name == name.Name)
            {
                return loaded;
            }
        }
        foreach (string directory in ModuleDirectories())
        {
            string candidate = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(candidate))
            {
                return context.LoadFromAssemblyPath(candidate);
            }
        }
        return null;
    }

    private static IntPtr ResolveNative(Assembly requesting, string libraryName)
    {
        string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
        foreach (string directory in ModuleDirectories())
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }
        return IntPtr.Zero;
    }

    private static string[] ModuleDirectories()
    {
        lock (gate)
        {
            return moduleDirectories.ToArray();
        }
    }
}
