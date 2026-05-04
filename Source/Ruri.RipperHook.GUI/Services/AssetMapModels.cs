using MessagePack;
using System.Text.RegularExpressions;
using Ruri.RipperHook;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.GUI.Services;

[MessagePackObject(AllowPrivate = true)]
internal sealed record AssetMapFile
{
	[Key(0)]
	public GameType GameType { get; set; }

	[Key(1)]
	public List<AssetMapEntry> AssetEntries { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record AssetMapEntry
{
	[Key(0)]
	public string Name { get; set; } = string.Empty;

	[Key(1)]
	public string Container { get; set; } = string.Empty;

	[Key(2)]
	public string Source { get; set; } = string.Empty;

	[Key(3)]
	public long PathID { get; set; }

	[Key(4)]
	public int Type { get; set; }

	[Key(5)]
	public string CAB { get; set; } = string.Empty;

	[IgnoreMember]
	public string PathIDString => PathID.ToString();

	[IgnoreMember]
	public string TypeString => Enum.IsDefined(typeof(ClassIDType), Type)
		? ((ClassIDType)Type).ToString()
		: Type.ToString();

	public bool Matches(IReadOnlyDictionary<string, Regex> filters)
	{
		foreach ((string key, Regex regex) in filters)
		{
			bool matched = key switch
			{
				nameof(Name) => regex.IsMatch(Name),
				nameof(Container) => regex.IsMatch(Container),
				nameof(Source) => regex.IsMatch(Source),
				nameof(PathID) => regex.IsMatch(PathIDString),
				"Type" => regex.IsMatch(TypeString),
				nameof(CAB) => !string.IsNullOrEmpty(CAB) && regex.IsMatch(CAB),
				_ => throw new NotSupportedException($"Unsupported filter field: {key}")
			};

			if (!matched)
			{
				return false;
			}
		}

		return true;
	}
}
