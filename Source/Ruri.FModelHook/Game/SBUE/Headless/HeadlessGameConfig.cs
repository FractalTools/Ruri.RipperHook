using System;
using System.Collections.Generic;
using System.IO;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;

namespace Ruri.FModelHook.Game.SBUE.Headless;

public sealed class HeadlessGameConfig
{
    public string GameDirectory { get; private set; } = string.Empty;
    public EGame UeVersion { get; private set; } = EGame.GAME_UE4_LATEST;
    public ETexturePlatform TexturePlatform { get; private set; } = ETexturePlatform.DesktopMobile;

    public string MainAesKey { get; private set; } = string.Empty;
    public List<DynamicAesKey> DynamicKeys { get; } = new();

    public string RawDataDirectory { get; private set; } = string.Empty;
    public string OutputDirectory { get; private set; } = string.Empty;

    public string? MappingEndpointUrl { get; private set; }
    public string? MappingEndpointPath { get; private set; }
    public string? MappingLocalFile { get; private set; }

    public bool HasUnsupportedVersioning { get; private set; }

    public sealed class DynamicAesKey
    {
        public string Guid { get; init; } = string.Empty;
        public string Key { get; init; } = string.Empty;
    }

    public static HeadlessGameConfig Load(string appSettingsPath, string? gameDirectoryOverride = null)
    {
        if (!File.Exists(appSettingsPath))
            throw new FileNotFoundException("AppSettings JSON not found.", appSettingsPath);

        JObject root = JObject.Parse(File.ReadAllText(appSettingsPath));
        var cfg = new HeadlessGameConfig
        {
            RawDataDirectory = (string?)root["RawDataDirectory"] ?? string.Empty,
            OutputDirectory = (string?)root["OutputDirectory"] ?? string.Empty,
        };

        string gameDir = gameDirectoryOverride ?? (string?)root["GameDirectory"] ?? string.Empty;
        cfg.GameDirectory = gameDir;

        JObject? perDir = null;
        if (root["PerDirectory"] is JObject perDirMap)
        {
            foreach (KeyValuePair<string, JToken?> kv in perDirMap)
            {
                if (string.Equals(kv.Key, gameDir, StringComparison.OrdinalIgnoreCase))
                {
                    perDir = kv.Value as JObject;
                    break;
                }
            }
            if (perDir == null)
            {
                foreach (KeyValuePair<string, JToken?> kv in perDirMap)
                {
                    perDir = kv.Value as JObject;
                    if (perDir != null)
                    {
                        if (string.IsNullOrEmpty(cfg.GameDirectory))
                            cfg.GameDirectory = (string?)perDir["GameDirectory"] ?? kv.Key;
                        break;
                    }
                }
            }
        }

        if (perDir == null)
            throw new InvalidDataException($"AppSettings has no PerDirectory entry for '{gameDir}'.");

        if (string.IsNullOrEmpty(cfg.GameDirectory))
            cfg.GameDirectory = (string?)perDir["GameDirectory"] ?? gameDir;

        if (perDir.TryGetValue("UeVersion", out JToken? ueVer) && ueVer.Type == JTokenType.Integer)
            cfg.UeVersion = (EGame)(int)ueVer;
        if (perDir.TryGetValue("TexturePlatform", out JToken? texPlat) && texPlat.Type == JTokenType.Integer)
            cfg.TexturePlatform = (ETexturePlatform)(int)texPlat;

        if (perDir["Versioning"] is JObject versioning)
        {
            bool any = versioning["CustomVersions"]?.HasValues == true
                       || versioning["Options"]?.HasValues == true
                       || versioning["MapStructTypes"]?.HasValues == true;
            cfg.HasUnsupportedVersioning = any;
        }

        if (perDir["AesKeys"] is JObject aes)
        {
            cfg.MainAesKey = (string?)aes["mainKey"] ?? string.Empty;
            if (aes["dynamicKeys"] is JArray dyn)
            {
                foreach (JToken dk in dyn)
                {
                    string? guid = (string?)dk["guid"];
                    string? key = (string?)dk["key"];
                    if (!string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(key))
                        cfg.DynamicKeys.Add(new DynamicAesKey { Guid = guid!, Key = key! });
                }
            }
        }

        if (perDir["Endpoints"] is JArray endpoints && endpoints.Count > (int)EndpointSlot.Mapping
            && endpoints[(int)EndpointSlot.Mapping] is JObject mapEndpoint)
        {
            cfg.MappingEndpointUrl = (string?)mapEndpoint["Url"];
            cfg.MappingEndpointPath = (string?)mapEndpoint["Path"];
            bool overwrite = (bool?)mapEndpoint["Overwrite"] ?? false;
            string? filePath = (string?)mapEndpoint["FilePath"];
            if (overwrite && !string.IsNullOrWhiteSpace(filePath))
                cfg.MappingLocalFile = filePath;
        }

        return cfg;
    }

    private enum EndpointSlot { Aes = 0, Mapping = 1 }
}
