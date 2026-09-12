using System.Diagnostics;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports.Material;
using Ruri.ShaderTools;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;
using ShaderDecompilerEngine = Ruri.ShaderTools.ShaderDecompiler;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The source of the shaders a set of assets compiled to.
///
/// Six steps, each a function of the one before it, and none of them install-wide: the assets
/// asked about name their shader maps, the catalog says which archive carries each, the library
/// reads exactly those shaders out of it, the material's own map says what each shader is, the
/// engine decompiles it and the emitter writes it. There is nothing to filter afterwards because
/// nothing was gathered that was not asked for.
/// </summary>
public static class ShaderSourceRun
{
    public static ShaderSourceSummary Execute(ShaderSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new ArgumentException("A run writes source files; state where with OutputDirectory.", nameof(request));
        }
        Action<string> log = request.Log ?? (_ => { });
        Action<string> logError = request.LogError ?? (_ => { });

        List<ShaderMapTarget> targets = Resolve(request, log, logError);
        if (targets.Count == 0)
        {
            log("[ShaderSource] nothing named a compiled shader map.");
            return new ShaderSourceSummary(0, 0, 0, 0, []);
        }

        string gameVersion = request.Provider.Versions.Game.ToString();
        MaterialConstantBufferReader.PreshaderVersion = PreshaderVersionOf(gameVersion, log);
        EngineMetadata metadata = EngineMetadata.Load(request.EngineUbMetadataDirectory, gameVersion, log, logError);

        using ShaderMapCatalog catalog = ShaderMapCatalog.Open(request.Provider, log, logError);
        Dictionary<string, List<(ShaderMapTarget Target, ShaderMapCatalog.Placement Placement)>> byArchive =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ShaderMapTarget target in targets)
        {
            if (!catalog.TryPlace(target.ShaderMapHash, out ShaderMapCatalog.Placement placement))
            {
                logError($"[ShaderSource] '{target.AssetPath}': no archive carries shader map {target.ShaderMapHash}.");
                continue;
            }
            if (!byArchive.TryGetValue(placement.ArchiveName, out var group))
            {
                group = new List<(ShaderMapTarget, ShaderMapCatalog.Placement)>();
                byArchive[placement.ArchiveName] = group;
            }
            group.Add((target, placement));
        }

        int maps = 0, decompiled = 0, skipped = 0, failed = 0;
        List<ShaderSourceArchive> archives = new(byArchive.Count);
        foreach ((string archiveName, var group) in byArchive.OrderBy(static one => one.Key, StringComparer.OrdinalIgnoreCase))
        {
            string outputDirectory = Path.Combine(request.OutputDirectory, archiveName).Replace('\\', '/');
            ShaderSourceState state = new(request, group[0].Placement.Library, archiveName, outputDirectory);
            state.EngineUbRegistry = metadata.UniformBuffers;
            state.ShaderTypeSeedRegistry = metadata.ShaderTypes;

            Stopwatch stopwatch = Stopwatch.StartNew();
            Build(state, group, metadata);
            ShaderLabProperties.Build(state);
            ShaderLabRenderState.Build(state);
            ShaderBinaries.Build(state);

            using (ShaderDecompilerEngine engine = new(outputDirectory))
            {
                foreach (ShaderMapInfo map in state.ShaderMaps.OrderBy(static one => one.PrimaryName, StringComparer.OrdinalIgnoreCase))
                {
                    Decompile(state, engine, map);
                    ShaderLabEmitter.Emit(state, map);
                }
            }
            stopwatch.Stop();
            ShaderLibrary library = group[0].Placement.Library;
            log($"[ShaderSource] {archiveName}: shader-maps={state.ShaderMaps.Count} decompiled={state.Decompiled} skipped={state.Skipped} failed={state.Failed}, read {library.BytesRead / (1024 * 1024)} MB of the archive's {library.Size / (1024 * 1024)} MB, in {stopwatch.ElapsedMilliseconds} ms -> {outputDirectory}");

            archives.Add(new ShaderSourceArchive(archiveName, state.ShaderMaps.Count, state.Decompiled, outputDirectory));
            maps += state.ShaderMaps.Count;
            decompiled += state.Decompiled;
            skipped += state.Skipped;
            failed += state.Failed;
        }
        return new ShaderSourceSummary(maps, decompiled, skipped, failed, archives);
    }

    /// <summary>One map's shaders, decompiled in one batch, skipping any another map already did.</summary>
    private static void Decompile(ShaderSourceState state, ShaderDecompilerEngine engine, ShaderMapInfo map)
    {
        var pending = new List<ShaderPrep>(map.Members.Count);
        var seen = new HashSet<int>();
        foreach (ShaderMapMember member in map.Members)
        {
            if (state.DecompileResultByIndex.ContainsKey(member.ArchiveShaderIndex)) continue;
            if (!state.ShaderPrepByIndex.TryGetValue(member.ArchiveShaderIndex, out ShaderPrep? prep)) continue;
            if (!seen.Add(prep.ShaderIndex)) continue;
            pending.Add(prep);
        }
        if (pending.Count == 0) return;

        var batch = new (byte[] Binary, EngineDecompileOptions Options)[pending.Count];
        for (int i = 0; i < pending.Count; i++) batch[i] = (pending[i].StrippedCode, pending[i].EngineOptions);
        DecompileResult[] results = engine.Decompile(batch);
        for (int i = 0; i < pending.Count; i++)
        {
            state.DecompileResultByIndex[pending[i].ShaderIndex] = results[i];
        }
    }
    /// <summary>Every shader map the subjects name, once each, in the order they were named.</summary>
    private static List<ShaderMapTarget> Resolve(ShaderSourceRequest request, Action<string> log, Action<string> logError)
    {
        List<ShaderMapTarget> targets = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (IShaderMapSubject subject in request.Subjects)
        {
            foreach (ShaderMapTarget target in subject.Resolve(request.Provider, log, logError))
            {
                if (seen.Add(target.ShaderMapHash + "\n" + target.AssetPath))
                {
                    targets.Add(target);
                }
            }
        }
        log($"[ShaderSource] {request.Subjects.Count} subject(s) named {targets.Count} shader map(s).");
        return targets;
    }

    /// <summary>
    /// One archive's maps as the emitters take them: the shaders each map owns, what each shader
    /// is, and what the map is named after.
    /// </summary>
    private static void Build(
        ShaderSourceState state,
        List<(ShaderMapTarget Target, ShaderMapCatalog.Placement Placement)> group,
        EngineMetadata metadata)
    {
        foreach ((ShaderMapTarget target, ShaderMapCatalog.Placement placement) in group)
        {
            string primaryName = Path.GetFileNameWithoutExtension(target.AssetPath);
            if (string.IsNullOrWhiteSpace(primaryName))
            {
                primaryName = "UnknownMaterial";
            }

            List<ShaderMapMember> members = new((int)placement.Map.NumShaders);
            for (uint member = 0; member < placement.Map.NumShaders; member++)
            {
                long offset = placement.Map.ShaderIndicesOffset + member;
                if (offset < 0 || offset >= placement.Library.ShaderIndices.Length)
                {
                    continue;
                }
                int shaderIndex = (int)placement.Library.ShaderIndices[offset];
                if (shaderIndex < 0 || shaderIndex >= placement.Library.ShaderEntries.Length)
                {
                    continue;
                }
                members.Add(new ShaderMapMember { RelativeIndex = (int)member, ArchiveShaderIndex = shaderIndex });
            }

            Dictionary<int, ShaderContainerInfo> containers = ShaderMapIdentity.Of(
                target, placement, primaryName,
                metadata.ShaderTypes, metadata.VertexFactoryTypes, metadata.PipelineTypes);

            ShaderMapInfo map = new()
            {
                Target = target,
                Assets = [target.AssetPath],
                PrimaryAsset = target.AssetPath,
                PrimaryName = primaryName,
                Members = members,
                ContainerByShaderIndex = containers,
            };
            state.ShaderMaps.Add(map);

            foreach (ShaderMapMember member in members)
            {
                if (containers.TryGetValue(member.ArchiveShaderIndex, out ShaderContainerInfo? info))
                {
                    state.ContainerByShaderIndex[member.ArchiveShaderIndex] = info;
                }
                if (!state.UsageByShaderIndex.TryGetValue(member.ArchiveShaderIndex, out HashSet<string>? usedBy))
                {
                    usedBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    state.UsageByShaderIndex[member.ArchiveShaderIndex] = usedBy;
                }
                usedBy.Add(target.AssetPath);
                state.NameByShaderIndex.TryAdd(member.ArchiveShaderIndex, primaryName);
            }

            ParameterMaps(state, target, members);
        }
    }

    /// <summary>
    /// Each shader's parameter map as the material states it, joined to the archive by the
    /// resource index the map counts its shaders along.
    /// </summary>
    private static void ParameterMaps(ShaderSourceState state, ShaderMapTarget target, List<ShaderMapMember> members)
    {
        if (target.ShaderMap?.Content is not FMaterialShaderMapContent content)
        {
            return;
        }
        Dictionary<int, FShaderParameterMapInfo> byResourceIndex = new();
        Collect(content.Shaders, byResourceIndex);
        foreach (FMeshMaterialShaderMap meshMap in content.OrderedMeshShaderMaps ?? [])
        {
            Collect(meshMap?.Shaders, byResourceIndex);
        }
        if (byResourceIndex.Count == 0)
        {
            return;
        }
        foreach (ShaderMapMember member in members)
        {
            if (byResourceIndex.TryGetValue(member.RelativeIndex, out FShaderParameterMapInfo? parameterMap))
            {
                state.ShaderParameterMapInfoByArchiveIndex[member.ArchiveShaderIndex] = parameterMap;
            }
        }
    }

    private static void Collect(FShader[]? shaders, Dictionary<int, FShaderParameterMapInfo> destination)
    {
        foreach (FShader shader in shaders ?? [])
        {
            if (shader?.ParameterMapInfo is { } parameterMap)
            {
                destination[shader.ResourceIndex] = parameterMap;
            }
        }
    }

    /// <summary>The preshader opcode layout an engine version writes, from the game's EGame name.</summary>
    internal static UeMaterialPreshaderVersion PreshaderVersionOf(string? gameVersionEnum, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(gameVersionEnum))
        {
            return UeMaterialPreshaderVersion.Ue51;
        }
        string? baseUe = gameVersionEnum!.StartsWith("GAME_UE5_", StringComparison.Ordinal)
            ? gameVersionEnum
            : EngineUbMetadataRegistry.TryDeriveBaseUeFromEGameForShaderTypes(gameVersionEnum, out string derived) ? derived : null;
        const string prefix = "GAME_UE5_";
        if (string.IsNullOrEmpty(baseUe) || !baseUe!.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(baseUe.AsSpan(prefix.Length), out int minor))
        {
            return UeMaterialPreshaderVersion.Ue51;
        }
        UeMaterialPreshaderVersion picked =
            minor >= 5 ? UeMaterialPreshaderVersion.Ue55 :
            minor >= 4 ? UeMaterialPreshaderVersion.Ue54 :
                         UeMaterialPreshaderVersion.Ue51;
        log?.Invoke($"[ShaderSource] preshader-opcode layout = {picked} (from {gameVersionEnum}{(baseUe == gameVersionEnum ? "" : $" -> {baseUe}")})");
        return picked;
    }
}

/// <summary>
/// What the engine's own type registrations say, dumped per engine version: the uniform buffers a
/// shader type binds, and the names behind the hashes a cooked map states.
/// </summary>
internal sealed class EngineMetadata
{
    private EngineMetadata(EngineUbMetadataRegistry uniformBuffers, ShaderTypeSeedRegistry shaderTypes,
        HashNameIndex vertexFactoryTypes, HashNameIndex pipelineTypes)
    {
        UniformBuffers = uniformBuffers;
        ShaderTypes = shaderTypes;
        VertexFactoryTypes = vertexFactoryTypes;
        PipelineTypes = pipelineTypes;
    }

    public EngineUbMetadataRegistry UniformBuffers { get; }
    public ShaderTypeSeedRegistry ShaderTypes { get; }
    public HashNameIndex VertexFactoryTypes { get; }
    public HashNameIndex PipelineTypes { get; }

    public static EngineMetadata Load(string? directory, string gameVersion, Action<string> log, Action<string> logError)
    {
        string root = directory ?? Path.Combine(AppContext.BaseDirectory, "EngineUbMetadata");
        bool tryBase = ShaderDecompilerSettingsAccess.Current.TryMatchBaseEngineVersion;
        string? game = string.IsNullOrEmpty(gameVersion) ? null : gameVersion;
        return new EngineMetadata(
            EngineUbMetadataRegistry.LoadForGame(root, game, tryBase, log, logError),
            ShaderTypeSeedRegistry.LoadForGame(root, game, tryBase, log, logError),
            HashNameIndex.LoadForGame(root, "_VertexFactoryType", game, tryBase, log, logError),
            HashNameIndex.LoadForGame(root, "_ShaderPipelineType", game, tryBase, log, logError));
    }
}
