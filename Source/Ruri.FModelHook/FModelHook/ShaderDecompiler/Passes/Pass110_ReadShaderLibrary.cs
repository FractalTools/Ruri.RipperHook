namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass110_ReadShaderLibrary
{
    public static void DoPass(PipelineState state)
    {
        state.Library = ShaderLibrary.Read(File.Open(state.Options.LibraryPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        state.Log($"    Library v{state.Library.Version}: {state.Library.ShaderEntries.Length} shaders, {state.Library.ShaderMapHashes.Count} shader-map hashes, code-body={state.Library.CodeBodyLength:N0} bytes.");
    }
}
