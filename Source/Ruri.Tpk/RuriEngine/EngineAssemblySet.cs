using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ruri.Tpk.RuriEngine;

internal sealed class EngineAssemblySet : IDisposable
{
    public const string ManifestName = "ScriptingAssemblies.json";
    private const string ModsDirectoryName = "Mods";
    private const string ManagedDirectoryName = "Managed";

    private readonly MetadataLoadContext context;

    private EngineAssemblySet(MetadataLoadContext context, IReadOnlyList<Assembly> assemblies)
    {
        this.context = context;
        Assemblies = assemblies;
    }

    public IReadOnlyList<Assembly> Assemblies { get; }

    public static EngineAssemblySet Open(string directory)
    {
        string root = ResolveRoot(directory);
        List<string> declared = Declared(root);
        if (declared.Count == 0)
        {
            throw new FileNotFoundException($"{ManifestName} names no assembly under {root}.", root);
        }

        Dictionary<string, string> resolvable = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in declared)
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.dll"))
            {
                resolvable.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        }
        foreach (string file in Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
        {
            resolvable.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }

        MetadataLoadContext context = new(new PathAssemblyResolver(resolvable.Values), typeof(object).Assembly.GetName().Name);
        List<Assembly> assemblies = new(declared.Count);
        foreach (string path in declared)
        {
            assemblies.Add(context.LoadFromAssemblyPath(path));
        }
        return new EngineAssemblySet(context, assemblies);
    }

    public void Dispose() => context.Dispose();

    private static string ResolveRoot(string directory)
    {
        if (File.Exists(Path.Combine(directory, ManifestName)))
        {
            return directory;
        }
        DirectoryInfo? parent = Directory.GetParent(directory);
        if (parent is not null
            && string.Equals(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), ManagedDirectoryName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(parent.FullName, ManifestName)))
        {
            return parent.FullName;
        }
        throw new FileNotFoundException($"{directory} holds no {ManifestName}; point at the player data directory that holds {ManagedDirectoryName} and {ModsDirectoryName}.", directory);
    }

    private static List<string> Declared(string root)
    {
        List<string> manifests = [Path.Combine(root, ManifestName)];
        string mods = Path.Combine(root, ModsDirectoryName);
        if (Directory.Exists(mods))
        {
            manifests.AddRange(Directory.EnumerateFiles(mods, ManifestName, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
        }

        List<string> assemblies = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string manifest in manifests)
        {
            string baseDirectory = Path.GetDirectoryName(manifest)!;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                string relative = entry.Value.GetProperty("Path").GetString()!;
                string full = Path.GetFullPath(Path.Combine(baseDirectory, relative));
                if (File.Exists(full) && seen.Add(full))
                {
                    assemblies.Add(full);
                }
            }
        }
        return assemblies;
    }
}
