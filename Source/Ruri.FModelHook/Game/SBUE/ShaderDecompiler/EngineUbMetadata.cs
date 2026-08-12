using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal sealed class EngineUbMetadata
{
    public string Name { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string EngineSource { get; set; } = string.Empty;

    [JsonPropertyName("LayoutHash")]
    public string LayoutHashHex { get; set; } = string.Empty;

    public string BindingFlags { get; set; } = string.Empty;

    public ConstantBufferParameter? ConstantBuffer { get; set; }

    public List<TextureParameter> Textures { get; set; } = new();
    public List<SamplerParameter> Samplers { get; set; } = new();
    public List<BufferBindingParameter> Buffers { get; set; } = new();
    public List<UAVParameter> UAVs { get; set; } = new();

    public List<EngineUbResourceSlot> Resources { get; set; } = new();

    public Dictionary<string, object>? Debug { get; set; }

    public uint ParsedHash()
    {
        string s = LayoutHashHex;
        if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X")) s = s.Substring(2);
        return uint.Parse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }

    [JsonIgnore]
    public int ConstantBufferSize => ConstantBuffer?.Size ?? 0;
}

internal sealed class EngineUbResourceSlot
{
    public int Index { get; set; }
    public uint Offset { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UbmtType { get; set; } = string.Empty;
    public string ShaderType { get; set; } = string.Empty;
}
