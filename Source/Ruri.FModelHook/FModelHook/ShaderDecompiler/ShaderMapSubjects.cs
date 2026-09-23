using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Niagara;
using CUE4Parse.FileProvider.Vfs;
using Ruri.FModelHook.BlenderBridge;
using Ruri.FModelHook.BlenderBridge.Readers;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// One shader map a run was asked about: the hash the archives key it by, the asset that named
/// it, and -- when the asset is a material -- the material's own compiled map, which is where
/// every shader's identity and every symbol in its constant buffer is stated.
/// </summary>
public sealed class ShaderMapTarget
{
    public required string ShaderMapHash { get; init; }

    /// <summary>The asset the caller named, which is what the emitted source is named after.</summary>
    public required string AssetPath { get; init; }

    /// <summary>Where the compiled map actually lives: an instance inherits its parent template's.</summary>
    public required string OwningAssetPath { get; init; }

    public string ShaderPlatform { get; init; } = string.Empty;

    /// <summary>The material as the caller named it, which is what the render state of the emitted source is.</summary>
    public UMaterialInterface? Material { get; init; }

    /// <summary>The material the compiled map belongs to, which is what states every shader's identity and symbols.</summary>
    public UMaterialInterface? Owner { get; init; }

    public FMaterialShaderMap? ShaderMap { get; init; }

    /// <summary>
    /// Every asset that named this map, the first of them being what the source is named after.
    ///
    /// A shader map is compiled ONCE and keyed by the hash of what it compiled from, so every
    /// material whose compilation produced the same one shares it -- an instance and its template,
    /// and every instance of that template that overrides only values. They are namers of one map,
    /// not one map each: answering per namer decompiles identical bytes again for every one of
    /// them and writes them out again under a different folder, which on a whole-install run is
    /// the difference between the install's shader inventory and its material count.
    /// </summary>
    public List<string> NamedBy { get; } = [];
}

/// <summary>
/// One way of naming shader maps. Everything a run can be asked about is one of these, and the
/// run itself knows none of them by name -- a new kind of subject is a new implementation and
/// nothing else changes.
/// </summary>
public interface IShaderMapSubject
{
    string Named { get; }

    IEnumerable<ShaderMapTarget> Resolve(AbstractVfsFileProvider provider, Action<string> log, Action<string> logError);
}

/// <summary>
/// One material, and the compiled map it uses -- its own, or the one it inherits from the
/// template up its parent chain. Only this package and its templates are loaded.
/// </summary>
public sealed class MaterialSubject : IShaderMapSubject
{
    private const int ParentChainLimit = 16;

    private readonly string materialPath;

    public MaterialSubject(string materialPath)
    {
        this.materialPath = materialPath ?? throw new ArgumentNullException(nameof(materialPath));
    }

    public string Named => materialPath;

    public IEnumerable<ShaderMapTarget> Resolve(AbstractVfsFileProvider provider, Action<string> log, Action<string> logError)
    {
        string key = UnrealDataTables.Key(provider, materialPath);
        IPackage package;
        try
        {
            package = provider.LoadPackage(key);
        }
        catch (Exception exception)
        {
            logError($"[ShaderSource] '{materialPath}' could not be loaded: {exception.GetType().Name}: {exception.Message}");
            yield break;
        }

        foreach (UObject export in package.GetExports())
        {
            if (export is not UMaterialInterface material)
            {
                continue;
            }
            UMaterialInterface owner = material;
            int walked = 0;
            while (owner.LoadedMaterialResources is not { Count: > 0 }
                   && owner is UMaterialInstance instance
                   && instance.Parent is UMaterialInterface parent
                   && walked < ParentChainLimit)
            {
                owner = parent;
                walked++;
            }
            if (owner.LoadedMaterialResources is not { Count: > 0 })
            {
                log($"[ShaderSource] '{materialPath}': no compiled shader map up the parent chain (walked {walked}, stopped at '{owner.Name}').");
                continue;
            }

            string owningPath = ReferenceEquals(owner, material) ? key : owner.GetPathName();
            foreach (FMaterialResource resource in owner.LoadedMaterialResources)
            {
                FMaterialShaderMap? shaderMap = resource.LoadedShaderMap;
                string? hash = shaderMap?.ResourceHash?.ToString() ?? shaderMap?.Code?.ResourceHash.ToString();
                if (shaderMap is null || string.IsNullOrWhiteSpace(hash))
                {
                    continue;
                }
                yield return new ShaderMapTarget
                {
                    ShaderMapHash = hash!,
                    AssetPath = key,
                    OwningAssetPath = owningPath,
                    ShaderPlatform = shaderMap.ShaderPlatform.ToString(),
                    Material = material,
                    Owner = owner,
                    ShaderMap = shaderMap,
                };
            }
        }
    }
}

/// <summary>
/// Whatever ONE package compiled to, whichever kind of asset it is: a material answers for
/// itself, a mesh or an actor answers for every material it names -- the same set an import of
/// it builds -- and an effect answers for its own scripts.
///
/// Stated as one subject because that is the question a caller with an asset in front of it
/// actually has. Which of those a package turns out to be is read off the package, never
/// guessed from its name or its folder.
/// </summary>
public sealed class PackageSubject : IShaderMapSubject
{
    private readonly string packagePath;

    public PackageSubject(string packagePath)
    {
        this.packagePath = packagePath ?? throw new ArgumentNullException(nameof(packagePath));
    }

    public string Named => packagePath;

    public IEnumerable<ShaderMapTarget> Resolve(AbstractVfsFileProvider provider, Action<string> log, Action<string> logError)
    {
        string key = UnrealDataTables.Key(provider, packagePath);
        if (!provider.Files.TryGetValue(key, out GameFile? file))
        {
            throw new FileNotFoundException($"[ShaderSource] the mount holds no package '{packagePath}'.", packagePath);
        }

        HashSet<string> materials = new(StringComparer.OrdinalIgnoreCase);
        bool isMaterial = false;
        bool isEffect = false;
        foreach (UObject export in provider.LoadPackage(file).GetExports())
        {
            isMaterial |= export is UMaterialInterface;
            isEffect |= export is UNiagaraScript;
            foreach (string path in UnrealComponents.MaterialPaths(export, []))
            {
                if (path.Length > 0)
                {
                    materials.Add(UnrealDataTables.Key(provider, path));
                }
            }
        }
        if (isMaterial)
        {
            materials.Add(key);
        }
        if (materials.Count == 0 && !isEffect)
        {
            log($"[ShaderSource] '{packagePath}' is neither a material, nor names one, nor carries a script, so it compiled no shader.");
            yield break;
        }

        foreach (string material in materials.OrderBy(static one => one, StringComparer.OrdinalIgnoreCase))
        {
            foreach (ShaderMapTarget target in new MaterialSubject(material).Resolve(provider, log, logError))
            {
                yield return target;
            }
        }
        if (!isEffect)
        {
            yield break;
        }
        foreach (ShaderMapTarget target in new NiagaraSubject(key).Resolve(provider, log, logError))
        {
            yield return target;
        }
    }
}

/// <summary>
/// One shader ARCHIVE, as the subject: every map it states, named after the archive.
///
/// A GLOBAL shader -- the tonemapper, the deferred lighting, the blurs -- belongs to no
/// material and no package, so nothing in the content tree names it and the per-asset road
/// cannot reach it at all. The archive itself is the only thing that can say what is in it,
/// which is why this is a subject of its own rather than a mode on another one.
///
/// A map here carries no material shader map (the same shape a script's does), so its shaders
/// are named by what the archive states about them.
/// </summary>
public sealed class ShaderArchiveSubject : IShaderMapSubject
{
    private readonly string archiveName;

    public ShaderArchiveSubject(string archiveName)
    {
        this.archiveName = archiveName ?? throw new ArgumentNullException(nameof(archiveName));
    }

    public string Named => archiveName;

    public IEnumerable<ShaderMapTarget> Resolve(AbstractVfsFileProvider provider, Action<string> log, Action<string> logError)
    {
        ShaderMapCatalog catalog = ShaderMapCatalog.For(provider);
        IReadOnlyList<string> hashes = catalog.MapHashesOf(archiveName, log, logError);
        if (hashes.Count == 0)
        {
            IReadOnlyList<string> available = catalog.ArchiveNames(log, logError);
            throw new InvalidOperationException(
                $"[ShaderSource] this mount ships no shader archive named '{archiveName}'. "
                + $"It ships: {string.Join(", ", available)}.");
        }
        log($"[ShaderSource] archive '{archiveName}' states {hashes.Count} shader map(s).");
        foreach (string hash in hashes)
        {
            yield return new ShaderMapTarget
            {
                ShaderMapHash = hash,
                AssetPath = archiveName + "/" + hash,
                OwningAssetPath = archiveName + "/" + hash,
            };
        }
    }
}

/// <summary>
/// One Niagara asset's compiled scripts. An effect's programs live in the same archives a
/// material's do and are keyed the same way, but a script carries no material shader map, so its
/// shaders are named by what the archive states about them.
/// </summary>
public sealed class NiagaraSubject : IShaderMapSubject
{
    private readonly string assetPath;

    public NiagaraSubject(string assetPath)
    {
        this.assetPath = assetPath ?? throw new ArgumentNullException(nameof(assetPath));
    }

    public string Named => assetPath;

    public IEnumerable<ShaderMapTarget> Resolve(AbstractVfsFileProvider provider, Action<string> log, Action<string> logError)
    {
        string key = UnrealDataTables.Key(provider, assetPath);
        IPackage package;
        try
        {
            package = provider.LoadPackage(key);
        }
        catch (Exception exception)
        {
            logError($"[ShaderSource] '{assetPath}' could not be loaded: {exception.GetType().Name}: {exception.Message}");
            yield break;
        }
        foreach (UObject export in package.GetExports())
        {
            if (export is not UNiagaraScript script || script.LoadedScriptResources is null)
            {
                continue;
            }
            foreach (FNiagaraShaderScript shaderScript in script.LoadedScriptResources)
            {
                string? hash = shaderScript?.RenderingThreadShaderMap?.ResourceHash?.ToString();
                if (string.IsNullOrWhiteSpace(hash))
                {
                    continue;
                }
                yield return new ShaderMapTarget
                {
                    ShaderMapHash = hash!,
                    AssetPath = key,
                    OwningAssetPath = key,
                };
            }
        }
    }
}
