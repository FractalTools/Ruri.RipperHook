using System;
using System.Collections.Generic;

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

    // Pass 001 outputs (asset/usage/name index, sidecar+unified merged)
    public Dictionary<int, HashSet<string>> UsageByShaderIndex { get; } = new();
    public Dictionary<int, string> NameByShaderIndex { get; } = new();

    // Pass 002 outputs (cached per-material symbol sources)
    public UnifiedMaterialReader? UnifiedMaterialReader { get; set; }
    public MaterialJsonSymbolReader? MaterialJsonSymbolReader { get; set; }

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
