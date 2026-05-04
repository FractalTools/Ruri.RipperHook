using System;
using System.Diagnostics;
using System.IO;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Top-level orchestrator. Mirrors the AssemblyDumper Pass design
// (`PassNNN_{Verb}.DoPass(state)` + per-pass timing). Conceptual order
// is the deferred-rendering G-buffer analogy:
//
//   Pass 000 — Read .ushaderlib bytes
//   Pass 001 — Build asset / usage / name index from sidecars + unified
//              metadata. Both sources iterated in one pass since they
//              feed the same map.
//   Pass 002 — Spin up per-material symbol-source readers. Lazy: actual
//              JSON I/O only happens during Pass 003 lookups.
//   Pass 003 — Per-shader decompile loop ("Light" stage). Each iteration:
//              strip UE wrapper → assemble metadata → hand to
//              ShaderDecompiler.Decompile → write outputs.
public static class DecompilePipeline
{
    public static DecompileSummary Run(LibraryDecompileOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.LibraryPath)) throw new ArgumentException("LibraryPath is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("OutputDirectory is required.", nameof(options));
        if (!File.Exists(options.LibraryPath)) throw new FileNotFoundException("UE shader library not found.", options.LibraryPath);

        PipelineState state = new(options);

        using (new TimingCookie(state, "Pass 000: Read .ushaderlib")) Pass000_ReadLibrary.DoPass(state);
        using (new TimingCookie(state, "Pass 001: Build asset index")) Pass001_BuildAssetIndex.DoPass(state);
        using (new TimingCookie(state, "Pass 002: Load symbol sources")) Pass002_LoadSymbolSources.DoPass(state);
        using (new TimingCookie(state, "Pass 003: Decompile shaders")) Pass003_DecompileShaders.DoPass(state);

        return new DecompileSummary(
            state.Library?.ShaderEntries.Length ?? 0,
            state.Decompiled,
            state.Skipped,
            state.Failed);
    }

    private readonly struct TimingCookie : IDisposable
    {
        private readonly PipelineState _state;
        private readonly string _label;
        private readonly Stopwatch _stopwatch;

        public TimingCookie(PipelineState state, string label)
        {
            _state = state;
            _label = label;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _state.Log($"  {_label} — {_stopwatch.ElapsedMilliseconds} ms");
        }
    }
}
