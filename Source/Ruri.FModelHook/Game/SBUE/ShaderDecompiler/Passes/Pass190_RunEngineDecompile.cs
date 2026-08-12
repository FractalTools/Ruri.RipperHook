using System;
using System.Collections.Generic;
using System.IO;
using Ruri.ShaderTools;
using ShaderDecompilerEngine = Ruri.ShaderTools.ShaderDecompiler;
using EngineDecompileOptions = Ruri.ShaderTools.DecompileOptions;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class Pass190_RunEngineDecompile
{
    public static void DoPass(PipelineState state)
    {
        if (state.ShaderPrepByIndex.Count == 0)
        {
            state.Log("    RunEngineDecompile: no prepped shaders, skipping.");
            return;
        }

        string outputDir = string.IsNullOrEmpty(state.OutputDirectory)
            ? Path.GetFullPath(state.Options.OutputDirectory)
            : state.OutputDirectory;

        using ShaderDecompilerEngine engine = new(outputDir);

        var preps = new List<ShaderPrep>(state.ShaderPrepByIndex.Count);
        foreach (var kvp in state.ShaderPrepByIndex)
        {
            preps.Add(kvp.Value);
        }
        preps.Sort(static (a, b) => a.ShaderIndex.CompareTo(b.ShaderIndex));

        var batch = new (byte[] Binary, EngineDecompileOptions Options)[preps.Count];
        for (int i = 0; i < preps.Count; i++)
        {
            batch[i] = (preps[i].StrippedCode, preps[i].EngineOptions);
        }

        DecompileResult[] results = engine.Decompile(batch);
        for (int i = 0; i < preps.Count; i++)
        {
            state.DecompileResultByIndex[preps[i].ShaderIndex] = results[i];
        }

        state.Log($"    RunEngineDecompile: decompiled {state.DecompileResultByIndex.Count} unique binaries.");
    }

    public static void DoPassForOneMap(PipelineState state, ShaderDecompilerEngine engine, ShaderMapInfo map)
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
}
