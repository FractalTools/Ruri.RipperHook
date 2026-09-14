using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

internal sealed class EngineUbMetadata
{
    public string Name { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string EngineSource { get; set; } = string.Empty;

    [JsonPropertyName("LayoutHash")]
    public string LayoutHashHex { get; set; } = string.Empty;

    /// <summary>
    /// Which hash <see cref="LayoutHashHex"/> is, as the dumper read it off the engine: one that
    /// folds the static slot's index into its low byte can only be matched around that byte,
    /// because the index is assigned at engine start and no source states it.
    /// </summary>
    public string HashFormula { get; set; } = string.Empty;

    public string BindingFlags { get; set; } = string.Empty;

    public string UsageFlags { get; set; } = string.Empty;

    /// <summary>The formula whose low byte is a static-slot index rather than part of the value.</summary>
    public const string SizeAndStaticSlot = "SizeAndStaticSlot";

    /// <summary>Whether a cook's hash names this seed's layout, allowing for the slot byte where the formula carries one.</summary>
    public bool Matches(uint cookHash)
    {
        uint own = ParsedHash();
        if (own == cookHash)
        {
            return true;
        }
        return HashFormula == SizeAndStaticSlot && ((own ^ cookHash) & 0xFFFFFF00u) == 0;
    }

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
