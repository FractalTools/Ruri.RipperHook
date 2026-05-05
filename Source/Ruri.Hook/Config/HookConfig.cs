using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ruri.Hook.Config;

// Unified host config: enabled-hook list + per-module settings bag in one
// JSON file per host (RuriFModelHook.json for the FModel host,
// RuriRipperHook.json for the RipperHook host). Modules opt into the
// settings bag with a string key; the underlying storage is JToken so
// unknown modules from a future build pass through unchanged on
// round-trip — adding a new module never invalidates an older config.
public class HookConfig
{
    /// <summary>
    /// List of enabled hooks by their unique identifier (Name_Version).
    /// </summary>
    public HashSet<string> EnabledHooks { get; set; } = new();

    /// <summary>
    /// Module key -> raw JSON node. Keys are case-insensitive on lookup.
    /// Each consumer (the shader decompiler, future modules, etc.)
    /// registers its own POCO under a unique module key via Get / Set.
    /// </summary>
    public Dictionary<string, JToken> ModuleSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a typed snapshot of `moduleKey` settings, or null when
    /// the module has never been written. Deserialisation failures
    /// (corrupt file, schema mismatch from a downgrade) also return
    /// null — callers should fall back to a fresh default instance.
    /// </summary>
    public T? GetModuleSettings<T>(string moduleKey) where T : class, new()
    {
        if (string.IsNullOrEmpty(moduleKey)) return null;
        if (!ModuleSettings.TryGetValue(moduleKey, out JToken? node) || node is null) return null;
        try
        {
            return node.ToObject<T>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Replace `moduleKey`'s slot with a serialised snapshot of `value`.
    /// Round-trip via JToken so future Save() emits the live shape.
    /// </summary>
    public void SetModuleSettings<T>(string moduleKey, T value) where T : class
    {
        if (string.IsNullOrEmpty(moduleKey)) throw new ArgumentException("Module key required.", nameof(moduleKey));
        ArgumentNullException.ThrowIfNull(value);
        ModuleSettings[moduleKey] = JToken.FromObject(value);
    }

    public static HookConfig Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<HookConfig>(json) ?? new HookConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HookConfig] Failed to load config from {path}: {ex.Message}");
            }
        }
        return new HookConfig();
    }

    public void Save(string path)
    {
        try
        {
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HookConfig] Failed to save config to {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Wipe every persisted choice (enabled hooks + every module's
    /// settings) by deleting the config file. Next Load() returns a
    /// fresh default. Hosts typically follow this with a restart hint
    /// because hook detours can't be undone in-process.
    /// </summary>
    public static void ResetToDefaults(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HookConfig] Failed to delete config at {path}: {ex.Message}");
        }
    }
}
