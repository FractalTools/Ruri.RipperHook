using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ruri.Hook.Config;

public class HookConfig
{
    public HashSet<string> EnabledHooks { get; set; } = new();

    public Dictionary<string, JToken> ModuleSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
