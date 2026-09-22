using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

/// <summary>
/// The installs one session has open, one slot each: the folder, the decoder that reads it,
/// the options it is read with, and the cabmap loaded for it. ONE is active at a time -- the
/// engine's own rule of one decoder per process -- and selecting another re-opens the
/// session onto it. The host keeps nothing but what the user typed; this is the truth of
/// which install is current.
/// </summary>
public static class Installs
{
    public const string InstallsId = "core.installs";

    public sealed class Slot
    {
        public required string Key { get; init; }
        public string Root { get; set; } = string.Empty;
        public string Decoder { get; set; } = string.Empty;
        public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
        public string CabMapPath { get; set; } = string.Empty;
        public CabTable? Map { get; set; }
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.Ordinal);
    private static readonly List<string> Order = [];
    private static string _active = string.Empty;

    /// <summary>How the session is opened onto a decoder and a folder -- stated by the bridge
    /// that owns hook activation, so this registry never reaches into it.</summary>
    public static Action<string, string>? Opener { get; set; }

    public static string ActiveKey => _active;

    public static CabTable? ActiveMap => Slots.TryGetValue(_active, out Slot? slot) ? slot.Map : null;

    public static void Register()
    {
        Datasets.Publish(InstallsId, DataRole.Session, [],
            "Every install this session has open: its key, folder, decoder, read options, cabmap "
            + "path, whether that cabmap is loaded, and which one is active.", Table);
    }

    public static void Open(string key, string root, string decoder, string[] flatOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!Slots.TryGetValue(key, out Slot? slot))
        {
            slot = new Slot { Key = key };
            Slots[key] = slot;
            Order.Add(key);
        }
        slot.Root = (root ?? string.Empty).TrimEnd('/', '\\');
        slot.Decoder = decoder ?? string.Empty;
        slot.Options.Clear();
        for (int index = 0; index + 1 < flatOptions.Length; index += 2)
        {
            slot.Options[flatOptions[index]] = flatOptions[index + 1] ?? string.Empty;
        }
        if (_active.Length == 0 || _active == key)
        {
            Use(key);
        }
        Datasets.ClearCache();
    }

    public static void LoadCabMap(string key, string path)
    {
        Slot slot = SlotOf(key);
        slot.CabMapPath = path;
        slot.Map = CabMap.LoadTable(path);
        Datasets.ClearCache();
    }

    /// <summary>Make one install the live one: its options and decoder become the
    /// session's, re-opened only when they differ from what is active.</summary>
    public static void Use(string key)
    {
        Slot slot = SlotOf(key);
        _active = key;
        Session.SetOptions(slot.Options);
        bool sameHooks = Session.HookIds.Contains(slot.Decoder) || (slot.Decoder.Length == 0 && Session.HookIds.Length <= 2);
        if (!sameHooks || !string.Equals(Session.GameRoot, slot.Root, StringComparison.OrdinalIgnoreCase))
        {
            (Opener ?? throw new InvalidOperationException("no session opener is registered."))(slot.Decoder, slot.Root);
        }
        Datasets.ClearCache();
    }

    public static void Rename(string oldKey, string newKey)
    {
        if (oldKey == newKey || !Slots.Remove(oldKey, out Slot? slot))
        {
            return;
        }
        Slot renamed = new() { Key = newKey, Root = slot.Root, Decoder = slot.Decoder, CabMapPath = slot.CabMapPath, Map = slot.Map };
        foreach ((string name, string value) in slot.Options)
        {
            renamed.Options[name] = value;
        }
        Slots[newKey] = renamed;
        Order[Order.IndexOf(oldKey)] = newKey;
        if (_active == oldKey)
        {
            _active = newKey;
        }
    }

    public static void Close(string key)
    {
        if (Slots.Remove(key))
        {
            Order.Remove(key);
        }
        if (_active == key)
        {
            _active = string.Empty;
        }
        Datasets.ClearCache();
    }

    private static Slot SlotOf(string key) =>
        Slots.TryGetValue(key, out Slot? slot)
            ? slot
            : throw new InvalidOperationException($"no install is open under key '{key}' -- open it first.");

    private static ColumnTable Table(DataRequest request)
    {
        TableBuilder table = new(InstallsId, "key", "root", "decoder", "options", "cabmap", "loaded#", "active#");
        table.Role(ColumnRole.Key, "key").Role(ColumnRole.Label, "key");
        foreach (string key in Order)
        {
            Slot slot = Slots[key];
            table.Row(slot.Key, slot.Root, slot.Decoder,
                string.Join(";", slot.Options.Select(pair => pair.Key + "=" + pair.Value)),
                slot.CabMapPath, slot.Map is null ? 0 : 1, key == _active ? 1 : 0);
        }
        return table.Build();
    }
}
