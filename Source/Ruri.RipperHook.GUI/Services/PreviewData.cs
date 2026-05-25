namespace Ruri.RipperHook.GUI.Services;

internal enum PreviewKind
{
	Info,
	Text,
	Yaml,
	Image,
	Mesh,
	Audio,
}

internal sealed class PreviewData
{
	public PreviewKind Kind { get; private init; }
	public string InfoText { get; private init; } = string.Empty;
	public string? TextContent { get; private init; }
	public byte[]? Data { get; private init; }
	public string? Extension { get; private init; }
	public object? Payload { get; private init; }

	public static PreviewData Info(string infoText) => new() { Kind = PreviewKind.Info, InfoText = infoText };
	public static PreviewData Text(string text, string infoText) => new() { Kind = PreviewKind.Text, TextContent = text, InfoText = infoText };
	public static PreviewData Yaml(string text, string infoText) => new() { Kind = PreviewKind.Yaml, TextContent = text, InfoText = infoText };
	public static PreviewData Image(byte[] data, string infoText) => new() { Kind = PreviewKind.Image, Data = data, InfoText = infoText };
	public static PreviewData Mesh(object payload, string infoText) => new() { Kind = PreviewKind.Mesh, Payload = payload, InfoText = infoText };
	public static PreviewData Audio(byte[] data, string? extension, string infoText) => new() { Kind = PreviewKind.Audio, Data = data, Extension = extension, InfoText = infoText };
}
