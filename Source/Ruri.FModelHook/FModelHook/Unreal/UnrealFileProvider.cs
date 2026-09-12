using AssetRipper.Import.Logging;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Versions;
using Ruri.FModelHook.ShaderDecompiler.Semantics;
using System.Collections.Concurrent;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// The provider with one instance per package: every path into a package -- the loader asking
/// for it, another package's import map reaching into it -- meets the same object graph, so a
/// package is read and deserialized once per load however many packages import it, and a
/// material's textures or a mesh's skeleton are not held in a private copy by every importer.
/// A load forgets each package once its assets are filled and releases the rest when done.
/// The material semantics read off the mounted game's compiled base passes are the mount's too:
/// the shader libraries are the archives', so the index over them and what each shader map was
/// found to sample live as long as the mount, whichever loads ask.
/// </summary>
public sealed class UnrealFileProvider : DefaultFileProvider
{
    private readonly ConcurrentDictionary<string, Lazy<IPackage>> packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<MaterialSemanticsResolver> semantics;

    public UnrealFileProvider(DirectoryInfo directory, DirectoryInfo[] extraDirectories, SearchOption searchOption, VersionContainer versions, StringComparer pathComparer)
        : base(directory, extraDirectories, searchOption, versions, pathComparer)
    {
        semantics = new Lazy<MaterialSemanticsResolver>(
            () => new MaterialSemanticsResolver(this, message => Logger.Info(LogCategory.Import, message), message => Logger.Verbose(LogCategory.Import, message)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Held => packages.Count;

    /// <summary>The material semantics of this mount, built on first use and kept until the mount is disposed.</summary>
    public MaterialSemanticsResolver Semantics => semantics.Value;

    public override IPackage LoadPackage(GameFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Lazy<IPackage> entry = packages.GetOrAdd(file.Path, _ => new Lazy<IPackage>(() => base.LoadPackage(file), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return entry.Value;
        }
        catch
        {
            packages.TryRemove(new KeyValuePair<string, Lazy<IPackage>>(file.Path, entry));
            throw;
        }
    }

    /// <summary>The package read afresh and kept by nobody, for a scan that only reads its header.</summary>
    public IPackage LoadUncached(GameFile file) => base.LoadPackage(file);

    /// <summary>Drops the instance of one package; whoever still holds it keeps it, the next reference reads it again.</summary>
    public void Forget(string path) => packages.TryRemove(path, out _);

    public void Release() => packages.Clear();

    public override void Dispose()
    {
        if (semantics.IsValueCreated)
        {
            semantics.Value.Dispose();
        }
        packages.Clear();
        base.Dispose();
    }
}
