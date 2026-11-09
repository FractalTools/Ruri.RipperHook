using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

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

    public static string ActiveKey => RipperBlenderBridge.WithReaderLifecycle(() => _active);

    public static CabTable? ActiveMap => RipperBlenderBridge.WithReaderLifecycle(() =>
        Slots.TryGetValue(_active, out Slot? slot) ? slot.Map : null);

    public static void Register()
    {
        Datasets.Publish(InstallsId, DataRole.Session, [],
            "Every install this session has open: its key, folder, decoder, read options, cabmap "
            + "path, whether that cabmap is loaded, and which one is active.", Table);
    }

    public static void Open(string key, string root, string decoder, string[] flatOptions)
        => RipperBlenderBridge.WithReaderTransition(() => OpenCore(key, root, decoder, flatOptions));

    private static void OpenCore(string key, string root, string decoder, string[] flatOptions)
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

    public static CabTable LoadCabMap(string key, string path)
        => RipperBlenderBridge.WithReaderTransition(() => LoadCabMapCore(key, path));

    private static CabTable LoadCabMapCore(string key, string path)
    {
        Slot slot = SlotOf(key);
        slot.CabMapPath = path;
        slot.Map = CabMap.LoadTable(path);
        Datasets.ClearCache();
        return slot.Map;
    }

    /// <summary>Retain an explicitly prepared map without disk IO or changing the active slot.</summary>
    public static void RegisterMap(string key, string root, string decoder, CabTable map, bool replaceExisting = false)
        => RipperBlenderBridge.WithReaderTransition(() => RegisterMapCore(key, root, decoder, map, replaceExisting));

    private static void RegisterMapCore(string key, string root, string decoder, CabTable map, bool replaceExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(map);
        if (Slots.TryGetValue(key, out Slot? existing))
        {
            if (!replaceExisting) throw new InvalidOperationException("An install already owns that key.");
            if (!string.Equals(existing.Root.TrimEnd('/', '\\'), root.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Decoder, decoder, StringComparison.Ordinal))
                throw new InvalidOperationException("Replacing a map must preserve its install root and decoder.");
            existing.Map = map;
            Datasets.ClearCache();
            return;
        }
        if (Slots.Count >= 64) throw new InvalidOperationException("Close an install before retaining another map.");
        Slot slot = new() { Key = key, Root = root, Decoder = decoder, Map = map };
        foreach ((string name, string value) in Session.Options) slot.Options[name] = value;
        Slots.Add(key, slot);
        Order.Add(key);
        Datasets.ClearCache();
    }

    public static CabTable MapOf(string key) => RipperBlenderBridge.WithReaderLifecycle(() => SlotOf(key).Map
        ?? throw new InvalidOperationException("The selected install has no loaded map."));

    public static string[] OptionsOf(string key) => RipperBlenderBridge.WithReaderLifecycle(() => SlotOf(key).Options
        .SelectMany(pair => new[] { pair.Key, pair.Value }).ToArray());

    /// <summary>Make one install the live one: its options and decoder become the
    /// session's, re-opened only when they differ from what is active.</summary>
    public static void Use(string key)
        => RipperBlenderBridge.WithReaderTransition(() => UseCore(key));

    private static void UseCore(string key)
    {
        Slot slot = SlotOf(key);
        Dictionary<string, string> previousOptions = new(Session.Options, StringComparer.Ordinal);
        try
        {
            Session.SetOptions(slot.Options);
            bool sameHooks = Session.HookIds.Contains(slot.Decoder) || (slot.Decoder.Length == 0 && Session.HookIds.Length <= 2);
            if (!sameHooks || !string.Equals(Session.GameRoot, slot.Root, StringComparison.OrdinalIgnoreCase))
            {
                (Opener ?? throw new InvalidOperationException("no session opener is registered."))(slot.Decoder, slot.Root);
            }
            _active = key;
        }
        catch (Exception error)
        {
            try { Session.SetOptions(previousOptions); }
            catch (Exception restoreError)
            {
                throw new AggregateException("Install selection and option restoration both failed.", error, restoreError);
            }
            throw;
        }
        Datasets.ClearCache();
    }

    public static void Rename(string oldKey, string newKey)
        => RipperBlenderBridge.WithReaderTransition(() => RenameCore(oldKey, newKey));

    private static void RenameCore(string oldKey, string newKey)
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
        => RipperBlenderBridge.WithReaderTransition(() => CloseCore(key));

    private static void CloseCore(string key)
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
        => RipperBlenderBridge.WithReaderLifecycle(() => TableCore(request));

    private static ColumnTable TableCore(DataRequest request)
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
