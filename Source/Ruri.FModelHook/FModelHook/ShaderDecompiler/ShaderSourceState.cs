using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.FileProvider.Vfs;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// What a run is asked for: which assets to answer about, where the source goes, and how much of
/// each variant to write. There is no filter here and no scope: what is named IS the work, so
/// nothing is gathered that a later step would have to throw away.
/// </summary>
public sealed class ShaderSourceRequest
{
    /// <summary>
    /// Where an install's shader source goes when the caller has nothing better to say:
    /// a folder of its own under the install itself, so two installs never write into
    /// each other's and nobody has to remember a path per game.
    /// </summary>
    public const string DefaultFolderName = "RuriShaderOutput";

    /// <summary>That folder for one install root, or empty when no root is known.</summary>
    public static string DefaultOutputDirectory(string? installRoot) =>
        string.IsNullOrWhiteSpace(installRoot) ? string.Empty : Path.Combine(installRoot, DefaultFolderName);

    public required AbstractVfsFileProvider Provider { get; init; }

    public required IReadOnlyList<IShaderMapSubject> Subjects { get; init; }

    public required string OutputDirectory { get; init; }

    public uint ShaderModel { get; init; } = 51;

    public bool DumpFailures { get; init; } = true;

    public bool SplitVariantsToHlslFiles { get; init; }

    /// <summary>
    /// Whether the source already in the output folder counts as work done.
    ///
    /// A shader map is compiled once and named by every material that shares it, so asking about
    /// a whole install names the same map again and again -- one install's materials name three
    /// times as many maps as the archives hold. Answering each time writes the same source to a
    /// second folder under a second material's name, and a run of that size cannot be finished in
    /// one sitting anyway. With this stated, a map whose folder is already there is left alone,
    /// so a run adds what is missing and a stopped run resumes by being started again.
    ///
    /// Left unstated for a request that NAMES its materials: what was asked for is written, every
    /// time, which is what asking for it means.
    /// </summary>
    public bool ResumeFromOutput { get; init; }

    public string? EngineUbMetadataDirectory { get; init; }

    /// <summary>
    /// Where the engine dumps live when a request names no folder: beside the assembly that reads
    /// them, which is where they are built. Asking the PROCESS for its base directory answers with
    /// the host's folder and, in a host that supplies none at all, with nothing -- and a relative
    /// path never resolved, so every engine fact silently read as absent under that host.
    /// </summary>
    public static string DefaultEngineUbMetadataDirectory => Path.Combine(
        Path.GetDirectoryName(typeof(ShaderSourceRequest).Assembly.Location) is { Length: > 0 } beside
            ? beside
            : AppContext.BaseDirectory,
        "EngineUbMetadata");

    public Action<string>? Log { get; init; }

    public Action<string>? LogError { get; init; }
}

/// <summary>One archive a run read from: what of it was asked for, and where that landed.</summary>
public sealed record ShaderSourceArchive(string Archive, int ShaderMaps, int Decompiled, string OutputDirectory);

public sealed record ShaderSourceSummary(int ShaderMaps, int Decompiled, int Skipped, int Failed, IReadOnlyList<ShaderSourceArchive> Archives);

/// <summary>One archive's worth of a run: the maps asked about that it carries, and what came of them.</summary>
internal sealed class ShaderSourceState
{
    public ShaderSourceState(ShaderSourceRequest request, ShaderLibrary library, string archiveName, string outputDirectory)
    {
        Request = request;
        Library = library;
        ArchiveName = archiveName;
        OutputDirectory = outputDirectory;
        FailuresRoot = Path.Combine(outputDirectory, "_failures");
        Variants = new VariantPool(outputDirectory);
        Log = request.Log ?? (_ => { });
        LogError = request.LogError ?? (_ => { });
    }

    /// <summary>Where this archive's shader variants land, one file per distinct text.</summary>
    public VariantPool Variants { get; }

    public ShaderSourceRequest Request { get; }
    public Action<string> Log { get; }
    public Action<string> LogError { get; }

    public ShaderLibrary Library { get; }
    public string ArchiveName { get; }
    public string OutputDirectory { get; }
    public string FailuresRoot { get; }

    public List<ShaderMapInfo> ShaderMaps { get; } = new();

    public Dictionary<int, HashSet<string>> UsageByShaderIndex { get; } = new();
    public Dictionary<int, string> NameByShaderIndex { get; } = new();
    public Dictionary<int, ShaderContainerInfo> ContainerByShaderIndex { get; } = new();
    public Dictionary<int, FShaderParameterMapInfo> ShaderParameterMapInfoByArchiveIndex { get; } = new();

    public EngineUbMetadataRegistry EngineUbRegistry { get; set; } = EngineUbMetadataRegistry.Empty;
    public ShaderTypeSeedRegistry ShaderTypeSeedRegistry { get; set; } = ShaderTypeSeedRegistry.Empty;

    public Dictionary<int, ShaderPrep> ShaderPrepByIndex { get; } = new();
    public Dictionary<int, DecompileResult> DecompileResultByIndex { get; } = new();

    public int Decompiled;
    public int Skipped;
    public int Failed;
}

internal sealed class ShaderContainerInfo
{
    public string ContainerKey { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string ShaderMapHash { get; init; } = string.Empty;
    public string ShaderTypeHash { get; init; } = string.Empty;
    public string ShaderTypeName { get; set; } = string.Empty;
    public string VertexFactoryTypeHash { get; init; } = string.Empty;
    public string VertexFactoryTypeName { get; set; } = string.Empty;
    public string PipelineTypeHash { get; init; } = string.Empty;
    public string PipelineTypeName { get; set; } = string.Empty;
    public int PermutationId { get; init; }
    public int ResourceIndex { get; init; }
    public byte Frequency { get; init; }
    public string ShaderHash { get; init; } = string.Empty;
}

internal sealed class ShaderMapInfo
{
    public required ShaderMapTarget Target { get; init; }
    public string ShaderMapHash => Target.ShaderMapHash;
    public List<string> Assets { get; init; } = new();
    public string PrimaryAsset { get; init; } = string.Empty;
    public string PrimaryName { get; init; } = string.Empty;
    public List<ShaderMapMember> Members { get; init; } = new();
    public Dictionary<int, ShaderContainerInfo> ContainerByShaderIndex { get; init; } = new();
    public string PropertiesBlock { get; set; } = string.Empty;

    public List<string> MaterialTextureOrder { get; set; } = new();

    public List<int> MaterialTextureBuckets { get; set; } = new();




    public string SubShaderTags { get; set; } = string.Empty;
    public string PassCommands { get; set; } = string.Empty;

    /// <summary>The expression set this map compiled from, which states every symbol its shaders bind.</summary>
    public FUniformExpressionSet? UniformExpressions =>
        (Target.ShaderMap?.Content as FMaterialShaderMapContent)?.MaterialCompilationOutput?.UniformExpressionSet;
}

internal sealed class ShaderMapMember
{
    public int RelativeIndex { get; init; }
    public int ArchiveShaderIndex { get; init; }
}

internal sealed class ShaderPrep
{
    public required int ShaderIndex { get; init; }
    public required string ContainerKey { get; init; }
    public required string MaterialName { get; init; }
    public required string VariantSuffix { get; init; }
    public required byte[] StrippedCode { get; init; }
    public required DecompileOptions EngineOptions { get; init; }
    public required string ProvisionalStem { get; init; }
    public required SerializedProgramData Metadata { get; init; }
    public ShaderContainerInfo? ContainerInfo { get; init; }
    public HashSet<string>? UsedBy { get; init; }
}
