using System.Collections.Generic;
using System.IO;
using System;
using Newtonsoft.Json;

namespace Ruri.Hook.Config;

public class HookConfig
{
    /// <summary>
    /// List of enabled hooks by their unique identifier (Name_Version)
    /// </summary>
    public HashSet<string> EnabledHooks { get; set; } = new HashSet<string>();

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
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HookConfig] Failed to save config to {path}: {ex.Message}");
        }
    }
}
