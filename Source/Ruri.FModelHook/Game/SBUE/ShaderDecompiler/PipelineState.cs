using System;
using System.Collections.Generic;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Caller-facing inputs to the pipeline. Filled once per .ushaderlib run.
public sealed class LibraryDecompileOptions
{
    public string LibraryPath { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public string? UnifiedMetadataPath { get; init; }
    public string? MaterialFilter { get; init; }
    public IReadOnlyCollection<int>? ShaderIndexFilter { get; init; }
    public uint ShaderModel { get; init; } = 51;
    // false → keep an existing output dir (incremental runs from FModelHook
    // dispatching one library at a time); true → wipe & recreate (CLI batch).
    public bool RecreateOutputDirectory { get; init; } = true;
    // Default-on: every per-shader Decompile failure dumps its inputs/
    // intermediates/error under `<OutputDirectory>/_failures/<stem>/`,
    // letting users diff pre-rewrite vs post-rewrite vs post-patch
    // SPIR-V offline.
    public bool DumpFailures { get; init; } = true;
    // When true and a shader-map's stage has more than one variant, each
    // variant's HLSL body is emitted to a sibling `<stem>/<variant>.hlsl`
    // file and the .shader file uses `#include` lines per `#if defined`
    // branch. When false (default), all variants stay inline inside the
    // .shader file under their `#if defined(VARIANT_*)` blocks (legacy
    // single-file layout). Stages with exactly one variant always inline,
    // regardless of this flag — distribution is only useful when there's
    // actually a chain to slim down.
    public bool SplitVariantsToHlslFiles { get; init; }
    public Action<string>? Log { get; init; }
    public Action<string>? LogError { get; init; }
}

public sealed record DecompileSummary(int TotalShaders, int Decompiled, int Skipped, int Failed);

// Mutable bag passed pass-to-pass. Each pass reads what previous passes
// produced and writes its own outputs. Deferred-rendering analogy: each
// pass writes one G-buffer attachment, the final pass reads them all.
internal sealed class PipelineState
{
    public LibraryDecompileOptions Options { get; }
    public Action<string> Log { get; }
    public Action<string> LogError { get; }

    // Pass 000 outputs
    public ShaderLibrary? Library { get; set; }

    // Pass 020 — `.assetinfo.json` -> on-disk shader-map hash to assets
    public Dictionary<string, HashSet<string>> ShaderMapToAssets { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Pass 030 — `.stableinfo.json` hash-level fan-out (shader-hash ->
    // asset -> set of frequencies). Used to fan unique shader binaries
    // out to materials when the owning shader-map didn't list them.
    public Dictionary<string, Dictionary<string, HashSet<byte>>> ShaderHashToAssetsByFreq { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Pass 040 — `UnifiedShaderMetadata.json` hash -> materials index
    // (PackageShaderMapHashes + CookedShaderMapIdHash + ShaderContentHash
    // folded together). Bridge from on-disk hashes to material paths.
    public Dictionary<string, HashSet<string>> HashToMaterialsFromUnified { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Pass 050 outputs (asset/usage/name index + per-shader-map view)
    public Dictionary<int, HashSet<string>> UsageByShaderIndex { get; } = new();
    public Dictionary<int, string> NameByShaderIndex { get; } = new();
    public Dictionary<int, ShaderContainerInfo> ContainerByShaderIndex { get; } = new();
    // Authoritative per-map view: containersByMapAndIndex[mapHash][archiveShaderIndex]
    // returns the ShaderContainerInfo this shader has WHEN VIEWED FROM that map.
    // The same shader binary can appear in multiple maps with different
    // ShaderType/VertexFactoryType because UE deduplicates compiled bytecode
    // across pipeline permutations. Pass080 emission must consult this so the
    // pass-grouping in the .shader file reflects the map's own truth, not
    // whichever map happened to register the shader last.
    public Dictionary<string, Dictionary<int, ShaderContainerInfo>> ContainersByMapAndIndex { get; set; } = new();
    // Per-shader-map view: each shader-map produces ONE .shader file. A
    // shader binary referenced by multiple maps appears in each owning
    // .shader, decompiled-once-cached-many. This is the "right axis" for
    // grouping: assets-per-map are 1:N (asset-info sidecar truth), but a
    // shader-binary's ownership is one-per-map plus sharing — so emitting
    // per-map keeps UsedMaterials honest (the map's own assets) instead of
    // unioning every material that ever touched the dedup'd binary.
    public List<ShaderMapInfo> ShaderMaps { get; } = new();

    // Pass 002 outputs (cached per-material symbol sources)
    public UnifiedMaterialReader? UnifiedMaterialReader { get; set; }
    public MaterialJsonSymbolReader? MaterialJsonSymbolReader { get; set; }

    // Pass 180 — per-shader-binary prep artefacts (stripped DXBC, engine
    // options, container metadata). Filled by Pass 180; consumed by
    // Pass 190 (decompile) and Pass 200 (emit).
    public Dictionary<int, ShaderPrep> ShaderPrepByIndex { get; } = new();

    // Pass 190 — engine.Decompile result per shader-index, decoded once
    // per unique binary even when shared across many shader-maps.
    public Dictionary<int, DecompileResult> DecompileResultByIndex { get; } = new();

    // Pass 003 running tallies + outputs
    public int Decompiled;
    public int Skipped;
    public int Failed;
    public string FailuresRoot { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;

    public PipelineState(LibraryDecompileOptions options)
    {
        Options = options;
        Log = options.Log ?? (_ => { });
        LogError = options.LogError ?? (_ => { });
    }
}

internal sealed class ShaderContainerInfo
{
    public string ContainerKey { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string ShaderMapHash { get; init; } = string.Empty;
    public string ShaderTypeHash { get; init; } = string.Empty;
    public string ShaderTypeName { get; init; } = string.Empty;
    public string VertexFactoryTypeHash { get; init; } = string.Empty;
    public string VertexFactoryTypeName { get; init; } = string.Empty;
    public string PipelineTypeHash { get; init; } = string.Empty;
    public string PipelineTypeName { get; init; } = string.Empty;
    public int PermutationId { get; init; }
    public int ResourceIndex { get; init; }
    public byte Frequency { get; init; }
    public string ShaderHash { get; init; } = string.Empty;
}

// Per-shader-map record. There is a 1:N relationship from a single
// shader-map to materials (the `Assets` list is the canonical truth from
// the asset-info sidecar). Each shader-map gets its own .shader output.
//
// `MemberIndices` walks the on-disk archive in shader-map order — the
// `i`-th entry is the i-th binary belonging to this map (also = the
// metadata's per-map `ResourceIndex`). Use it when emitting variants so
// permutation ids and shader-type hashes line up the way UE serialised
// them, rather than the global archive ordering which is an arbitrary
// allocation artefact.
internal sealed class ShaderMapInfo
{
    public int ShaderMapIndex { get; init; }
    public string ShaderMapHash { get; init; } = string.Empty;
    public List<string> Assets { get; init; } = new();
    public string PrimaryAsset { get; init; } = string.Empty;
    public string PrimaryName { get; init; } = string.Empty;
    public List<ShaderMapMember> Members { get; init; } = new();
    // Per-map view of each shader binary's type/VF/permutation metadata.
    // The same shader binary can be a member of multiple shader-maps, and
    // each map records its own type/VF/permutation truth — populated from
    // that map's own stableinfo entries, not "whichever map happened to
    // be processed last", which is the bug the global ContainerByShaderIndex
    // dictionary suffered from.
    public Dictionary<int, ShaderContainerInfo> ContainerByShaderIndex { get; init; } = new();
    // Pre-rendered shaderlab `Properties { ... }` block — populated by
    // Pass070 from the primary asset's FUniformExpressionSet, consumed
    // by Pass080 emission. Empty when no UES is available (e.g. global
    // archive shader-maps that have no material side at all).
    public string PropertiesBlock { get; set; } = string.Empty;
    // Pre-rendered SubShader-level Tags + Pass-level render-state lines —
    // populated by Pass175 from the primary asset's RenderState UProperty
    // bag (see UnifiedMaterialMetadata.RenderState). Two outputs:
    //   * SubShaderTags: full `Tags { ... }` block to drop inside SubShader,
    //     before the Pass.
    //   * PassCommands: per-Pass shaderlab commands (Cull, Blend, ZWrite,
    //     ZTest, ColorMask, AlphaToMask) one per line, no leading indent.
    // Both empty when the shader-map has no material backing (global
    // archive entries) or the unified metadata predates render-state writes.
    public string SubShaderTags { get; set; } = string.Empty;
    public string PassCommands { get; set; } = string.Empty;
}

internal sealed class ShaderMapMember
{
    public int RelativeIndex { get; init; }      // 0..NumShaders-1, == metadata ResourceIndex
    public int ArchiveShaderIndex { get; init; } // global archive index
}

// Per-shader artefacts the prep pass (Pass 180) collects so Pass 190 only
// touches binary + options and Pass 200 has everything it needs to write
// outputs without re-reading metadata. Lives at top-level so it can be
// referenced from PipelineState's typed dictionary slot.
internal sealed class ShaderPrep
{
    public required int ShaderIndex { get; init; }
    public required string ContainerKey { get; init; }
    public required string MaterialName { get; init; }
    public required string VariantSuffix { get; init; }
    public required string TypeSuffix { get; init; }
    public required byte[] StrippedCode { get; init; }
    public required DecompileOptions EngineOptions { get; init; }
    public required string ProvisionalStem { get; init; }
    public required SerializedProgramData Metadata { get; init; }
    public ShaderContainerInfo? ContainerInfo { get; init; }
    public HashSet<string>? UsedBy { get; init; }
}
