using System.Collections.Generic;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// =====================================================================
// Mirror of UE's FMaterialParameterInfo (Engine/Public/MaterialTypes.h).
// =====================================================================
internal enum EMaterialParameterAssociation { LayerParameter = 0, BlendParameter = 1, GlobalParameter = 2 }

internal sealed class FMaterialParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public EMaterialParameterAssociation Association { get; set; } = EMaterialParameterAssociation.GlobalParameter;
    public int Index { get; set; } = -1;

    public FMaterialParameterInfo() { }
    public FMaterialParameterInfo(string name, EMaterialParameterAssociation association = EMaterialParameterAssociation.GlobalParameter, int index = -1)
    { Name = name; Association = association; Index = index; }

    public override string ToString() => $"{Name}[{Association}:{Index}]";
}

internal sealed class SymbolInputs
{
    public string MaterialPath { get; set; } = string.Empty;
    public string? ShaderPlatform { get; set; }
    public bool UsedLoadedMaterialResources { get; set; }
    public ConstantBufferParameter? MaterialConstantBuffer { get; set; }
    public List<FMaterialParameterInfo> NumericParameterInfos { get; } = new();
    public MaterialUniformBufferLayout.MaterialResourceCounts? MaterialResourceCounts { get; set; }
    // Extra cbuffers the material binds beyond `Material` itself — currently
    // `MaterialCollection<N>` per the material's `ParameterCollectionInfos[N]`.
    // Resolved at metadata-read time from each referenced `UMaterialParameterCollection`
    // asset (`ScalarParameters[]` packed 4-per-vec4 then `VectorParameters[]`).
    public List<ConstantBufferParameter> ExtraConstantBuffers { get; } = new();
}
