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

        Stopwatch whole = Stopwatch.StartNew();
        Stopwatch phase = Stopwatch.StartNew();
        List<ShaderMapTarget> targets = Resolve(request, log, logError);
        long resolveMs = phase.ElapsedMilliseconds;
        if (targets.Count == 0)
        {
            log("[ShaderSource] nothing named a compiled shader map.");
            return new ShaderSourceSummary(0, 0, 0, 0, []);
        }

        phase.Restart();
        string gameVersion = request.Provider.Versions.Game.ToString();
        EngineMetadata metadata = EngineMetadata.Cached(request.EngineUbMetadataDirectory, gameVersion, log, logError);
        MaterialConstantBufferReader.Opcodes = metadata.PreshaderOpcodes;
        MaterialUniformBufferRecipe.Current = metadata.MaterialUniformBuffer;
        long metadataMs = phase.ElapsedMilliseconds;

        phase.Restart();
        ShaderMapCatalog catalog = ShaderMapCatalog.For(request.Provider);
        Dictionary<string, List<(ShaderMapTarget Target, ShaderMapCatalog.Placement Placement)>> byArchive =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ShaderMapTarget target in targets)
        {
            if (!catalog.TryPlace(target.ShaderMapHash, log, logError, out ShaderMapCatalog.Placement placement))
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
        log($"[ShaderSource] timing: named maps in {resolveMs} ms, engine facts in {metadataMs} ms, placed in {phase.ElapsedMilliseconds} ms ({catalog.OpenedArchiveCount} archive(s) open, {catalog.IndexedMapCount} maps indexed).");

        int maps = 0, decompiled = 0, skipped = 0, failed = 0, alreadyWritten = 0;
        List<ShaderSourceArchive> archives = new(byArchive.Count);
        foreach ((string archiveName, var group) in byArchive.OrderBy(static one => one.Key, StringComparer.OrdinalIgnoreCase))
        {
            string outputDirectory = Path.Combine(request.OutputDirectory, archiveName).Replace('\\', '/');
            List<(ShaderMapTarget Target, ShaderMapCatalog.Placement Placement)> pending = group;
            if (request.ResumeFromOutput)
            {
                HashSet<string> written = ShaderLabEmitter.Written(outputDirectory);
                pending = group.Where(one => !written.Contains(ShaderLabEmitter.HashPrefix(one.Target.ShaderMapHash))).ToList();
                alreadyWritten += group.Count - pending.Count;
                if (pending.Count == 0)
                {
                    continue;
                }
            }
            ShaderSourceState state = new(request, pending[0].Placement.Library, archiveName, outputDirectory);
            state.EngineUbRegistry = metadata.UniformBuffers;
            state.ShaderTypeSeedRegistry = metadata.ShaderTypes;

            Stopwatch stopwatch = Stopwatch.StartNew();
            Build(state, pending, metadata);
            ShaderLabProperties.Build(state);
            ShaderLabRenderState.Build(state);
            long describedMs = stopwatch.ElapsedMilliseconds;
            ShaderBinaries.Build(state);
            long fetchedMs = stopwatch.ElapsedMilliseconds - describedMs;
            long decompiledMs = 0, emittedMs = 0;

            long beforeDecompile = stopwatch.ElapsedMilliseconds;
            using (ShaderDecompilerEngine engine = new(outputDirectory))
            {
                Decompile(state, engine);
            }
            decompiledMs = stopwatch.ElapsedMilliseconds - beforeDecompile;
            Parallel.ForEach(state.ShaderMaps, map => ShaderLabEmitter.Emit(state, map));
            emittedMs = stopwatch.ElapsedMilliseconds - beforeDecompile - decompiledMs;
            stopwatch.Stop();
            ShaderLibrary library = pending[0].Placement.Library;
            log($"[ShaderSource] {archiveName}: shader-maps={state.ShaderMaps.Count} decompiled={state.Decompiled} skipped={state.Skipped} failed={state.Failed}, read {library.BytesRead / (1024 * 1024)} MB of the archive's {library.Size / (1024 * 1024)} MB, in {stopwatch.ElapsedMilliseconds} ms (described {describedMs}, fetched {fetchedMs}, decompiled {decompiledMs}, emitted {emittedMs}) -> {outputDirectory}");

            archives.Add(new ShaderSourceArchive(archiveName, state.ShaderMaps.Count, state.Decompiled, outputDirectory));
            maps += state.ShaderMaps.Count;
            decompiled += state.Decompiled;
            skipped += state.Skipped;
            failed += state.Failed;
        }
        log($"[ShaderSource] whole run {whole.ElapsedMilliseconds} ms: {maps} map(s), {decompiled} decompiled, {failed} failed"
            + (alreadyWritten > 0 ? $", {alreadyWritten} map(s) already written." : "."));
        return new ShaderSourceSummary(maps, decompiled, skipped, failed, archives);
    }

    /// <summary>
    /// Every shader this archive's maps name, decompiled as ONE batch.
    ///
    /// The batch runner spreads its queue over a worker per core, so the batch is only as wide
    /// as what it is handed. Handing it one map at a time handed it forty-odd shaders whose
    /// costs differ by orders of magnitude: the workers finished early and waited on the one
    /// slow shader, and the pool, the gate and a decompiler per worker were built again for the
    /// next map. A pass already holds every result it has decompiled until it has written them,
    /// so batching the pass whole costs no more memory than batching it a map at a time.
    /// </summary>
    private static void Decompile(ShaderSourceState state, ShaderDecompilerEngine engine)
    {
        var pending = new List<ShaderPrep>(state.ShaderPrepByIndex.Count);
        var seen = new HashSet<int>();
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            foreach (ShaderMapMember member in map.Members)
            {
                if (!state.ShaderPrepByIndex.TryGetValue(member.ArchiveShaderIndex, out ShaderPrep? prep)) continue;
                if (!seen.Add(prep.ShaderIndex)) continue;
                pending.Add(prep);
            }
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
    /// <summary>
    /// Every DISTINCT shader map the subjects name, in the order they were first named, each
    /// carrying every asset that named it.
    ///
    /// The map is the unit of work because the map is the unit the engine compiled: two materials
    /// that name the same hash name the same bytes, and the source written for them would be
    /// identical but for the folder it landed in.
    /// </summary>
    private static List<ShaderMapTarget> Resolve(ShaderSourceRequest request, Action<string> log, Action<string> logError)
    {
        List<ShaderMapTarget> targets = new();
        Dictionary<string, ShaderMapTarget> byHash = new(StringComparer.OrdinalIgnoreCase);
        int named = 0;
        foreach (IShaderMapSubject subject in request.Subjects)
        {
            foreach (ShaderMapTarget target in subject.Resolve(request.Provider, log, logError))
            {
                named++;
                if (byHash.TryGetValue(target.ShaderMapHash, out ShaderMapTarget? already))
                {
                    if (!already.NamedBy.Contains(target.AssetPath, StringComparer.OrdinalIgnoreCase))
                    {
                        already.NamedBy.Add(target.AssetPath);
                    }
                    continue;
                }
                target.NamedBy.Add(target.AssetPath);
                byHash[target.ShaderMapHash] = target;
                targets.Add(target);
            }
        }
        log($"[ShaderSource] {request.Subjects.Count} subject(s) named {named} map(s), {targets.Count} of them distinct.");
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
                Assets = [.. target.NamedBy],
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
                foreach (string namer in target.NamedBy)
                {
                    usedBy.Add(namer);
                }
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
}

/// <summary>
/// What the engine's own type registrations say, dumped per engine version: the uniform buffers a
/// shader type binds, and the names behind the hashes a cooked map states.
/// </summary>
internal sealed class EngineMetadata
{
    private EngineMetadata(EngineUbMetadataRegistry uniformBuffers, ShaderTypeSeedRegistry shaderTypes,
        HashNameIndex vertexFactoryTypes, HashNameIndex pipelineTypes, MaterialPreshaderOpcodes preshaderOpcodes,
        MaterialUniformBufferRecipe materialUniformBuffer)
    {
        UniformBuffers = uniformBuffers;
        ShaderTypes = shaderTypes;
        VertexFactoryTypes = vertexFactoryTypes;
        PipelineTypes = pipelineTypes;
        PreshaderOpcodes = preshaderOpcodes;
        MaterialUniformBuffer = materialUniformBuffer;
    }

    public EngineUbMetadataRegistry UniformBuffers { get; }
    public ShaderTypeSeedRegistry ShaderTypes { get; }
    public HashNameIndex VertexFactoryTypes { get; }
    public HashNameIndex PipelineTypes { get; }
    public MaterialPreshaderOpcodes PreshaderOpcodes { get; }
    public MaterialUniformBufferRecipe MaterialUniformBuffer { get; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, string Game), EngineMetadata> Loaded = new();

    /// <summary>
    /// The engine facts for one metadata folder and game, read once per process. They are files
    /// on disk that do not change while the process runs, and reading a few hundred of them was
    /// paid again on every request when it need only ever be paid on the first.
    /// </summary>
    public static EngineMetadata Cached(string? directory, string gameVersion, Action<string> log, Action<string> logError)
    {
        string root = directory ?? ShaderSourceRequest.DefaultEngineUbMetadataDirectory;
        return Loaded.GetOrAdd((root, gameVersion), key => Load(key.Root, key.Game, log, logError));
    }

    public static EngineMetadata Load(string? directory, string gameVersion, Action<string> log, Action<string> logError)
    {
        string root = directory ?? ShaderSourceRequest.DefaultEngineUbMetadataDirectory;
        bool tryBase = ShaderDecompilerSettingsAccess.Current.TryMatchBaseEngineVersion;
        string? game = string.IsNullOrEmpty(gameVersion) ? null : gameVersion;
        return new EngineMetadata(
            EngineUbMetadataRegistry.LoadForGame(root, game, tryBase, log, logError),
            ShaderTypeSeedRegistry.LoadForGame(root, game, tryBase, log, logError),
            HashNameIndex.LoadForGame(root, "_VertexFactoryType", game, tryBase, log, logError),
            HashNameIndex.LoadForGame(root, "_ShaderPipelineType", game, tryBase, log, logError),
            MaterialPreshaderOpcodes.LoadForGame(root, game, tryBase, log, logError),
            MaterialUniformBufferRecipe.LoadForGame(root, game, tryBase, log, logError));
    }
}
