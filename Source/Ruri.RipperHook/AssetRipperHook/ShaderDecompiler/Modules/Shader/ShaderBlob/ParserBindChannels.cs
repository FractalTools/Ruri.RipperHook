namespace AssetRipper.Export.Modules.Shaders.ShaderBlob;

public sealed class ParserBindChannels
{
	public ParserBindChannels() { }

	public ParserBindChannels(ShaderBindChannel[] channels, int sourceMap)
	{
		Channels = channels;
		SourceMap = sourceMap;
	}

	public ShaderBindChannel[] Channels { get; set; } = [];
	public int SourceMap { get; set; }
}
