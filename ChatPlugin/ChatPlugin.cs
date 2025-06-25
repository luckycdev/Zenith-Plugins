using ServerShared.Plugins;
using ServerShared.Logging;
using ServerShared;
using System.Text.Json;
using System.Reflection;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System;

public class ChatConfig
{
    public bool? RankPrefixes { get; set; } = true;
    public string AdminPrefix { get; set; } = "[Admin]";
    public string ModPrefix { get; set; } = "[Mod]";
    public string PlayerPrefix { get; set; } = "";
    public bool? RankColors { get; set; } = true;
    public int? Player_Color_R { get; set; } = 255;
    public int? Player_Color_G { get; set; } = 255;
    public int? Player_Color_B { get; set; } = 255;
    public int? Mod_Color_R { get; set; } = 151;
    public int? Mod_Color_G { get; set; } = 255;
    public int? Mod_Color_B { get; set; } = 104;
    public int? Admin_Color_R { get; set; } = 255;
    public int? Admin_Color_G { get; set; } = 128;
    public int? Admin_Color_B { get; set; } = 128;
}

public class ChatPlugin : IPlugin
{
    public string Name => "ChatPlugin";

    public string Version => "0.1";

    public string Author => "luckycdev";

    private string configFilePath;
    private ChatConfig config;

    private float player_rgb_r;
    private float player_rgb_g;
    private float player_rgb_b;

    private float mod_rgb_r;
    private float mod_rgb_g;
    private float mod_rgb_b;

    private float admin_rgb_r;
    private float admin_rgb_g;
    private float admin_rgb_b;

    public void Initialize()
    {
        LoadOrCreateConfig();
        Logger.LogInfo("[ChatPlugin] Initialized!");
        ModifyMessages();

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        Logger.LogInfo("[ChatPlugin] Shutdown!");
    }

    public void ModifyMessages()
    {
        GameServer.Instance.OnChatMessageModify += (sender, playerName, message, color) =>
        {
            if (config.RankPrefixes == true && config.RankColors == true)
            {
                if (sender != null && (int)sender.AccessLevel == 0)
                    return ($"{config.PlayerPrefix} {playerName}".Trim(), message, new UnityEngine.Color(player_rgb_r, player_rgb_g, player_rgb_b));

                if (sender != null && (int)sender.AccessLevel == 1)
                    return ($"{config.ModPrefix} {playerName}".Trim(), message, new UnityEngine.Color(mod_rgb_r, mod_rgb_g, mod_rgb_b));

                if (sender != null && (int)sender.AccessLevel == 2)
                    return ($"{config.AdminPrefix} {playerName}".Trim(), message, new UnityEngine.Color(admin_rgb_r, admin_rgb_g, admin_rgb_b));

                return (playerName, message, color);
            }
            else if (config.RankPrefixes == true && config.RankColors == false)
            {
                if (sender != null && (int)sender.AccessLevel == 0)
                    return ($"{config.PlayerPrefix} {playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                if (sender != null && (int)sender.AccessLevel == 1)
                    return ($"{config.ModPrefix} {playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                if (sender != null && (int)sender.AccessLevel == 2)
                    return ($"{config.AdminPrefix} {playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                return (playerName, message, color);
            }
            else if (config.RankPrefixes == false && config.RankColors == true)
            {
                if (sender != null && (int)sender.AccessLevel == 0)
                    return ($"{playerName}".Trim(), message, new UnityEngine.Color(player_rgb_r, player_rgb_g, player_rgb_b));

                if (sender != null && (int)sender.AccessLevel == 1)
                    return ($"{playerName}".Trim(), message, new UnityEngine.Color(mod_rgb_r, mod_rgb_g, mod_rgb_b));

                if (sender != null && (int)sender.AccessLevel == 2)
                    return ($"{playerName}".Trim(), message, new UnityEngine.Color(admin_rgb_r, admin_rgb_g, admin_rgb_b));

                return (playerName, message, color);
            }
            else // no prefix and no color
            {
                if (sender != null && (int)sender.AccessLevel == 0)
                    return ($"{playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                if (sender != null && (int)sender.AccessLevel == 1)
                    return ($"{playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                if (sender != null && (int)sender.AccessLevel == 2)
                    return ($"{playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                return (playerName, message, color);
            }
        };
    }

    private void LoadOrCreateConfig()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(pluginFolder, "config.json");

        if (!File.Exists(configFilePath))
        {
            config = new ChatConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configFilePath, json);

            Logger.LogDebug($"[ChatPlugin] Config file created: {configFilePath}");

            Logger.LogWarning($"[ChatPlugin] Please see {configFilePath} to change settings!");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<ChatConfig>(json);

            player_rgb_r = config.Player_Color_R.GetValueOrDefault() / 255f;
            player_rgb_g = config.Player_Color_G.GetValueOrDefault() / 255f;
            player_rgb_b = config.Player_Color_B.GetValueOrDefault() / 255f;

            mod_rgb_r = config.Mod_Color_R.GetValueOrDefault() / 255f;
            mod_rgb_g = config.Mod_Color_G.GetValueOrDefault() / 255f;
            mod_rgb_b = config.Mod_Color_B.GetValueOrDefault() / 255f;

            admin_rgb_r = config.Admin_Color_R.GetValueOrDefault() / 255f;
            admin_rgb_g = config.Admin_Color_G.GetValueOrDefault() / 255f;
            admin_rgb_b = config.Admin_Color_B.GetValueOrDefault() / 255f;


            // check if null
            if (!config.RankPrefixes.HasValue)
                Logger.LogError($"[ChatPlugin] RankPrefixes option in {configFilePath} is null");

            if (!config.RankColors.HasValue)
                Logger.LogError($"[ChatPlugin] RankColors option in {configFilePath} is null");


            if (config.RankPrefixes == true && config.PlayerPrefix == null)
                Logger.LogError($"[ChatPlugin] Player Prefix in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Player_Color_R == null || config.Player_Color_R > 255 || config.Player_Color_R < 0))
                Logger.LogError($"[ChatPlugin] Player_Color_R in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Player_Color_G == null || config.Player_Color_G > 255 || config.Player_Color_G < 0))
                Logger.LogError($"[ChatPlugin] Player_Color_G in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Player_Color_B == null || config.Player_Color_B > 255 || config.Player_Color_B < 0))
                Logger.LogError($"[ChatPlugin] Player_Color_B in {configFilePath} is invalid!");


            if (config.RankPrefixes == true && config.ModPrefix == null)
                Logger.LogError($"[ChatPlugin] Mod Prefix in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Mod_Color_R == null || config.Mod_Color_R > 255 || config.Mod_Color_R < 0))
                Logger.LogError($"[ChatPlugin] Mod_Color_R in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Mod_Color_G == null || config.Mod_Color_G > 255 || config.Mod_Color_G < 0))
                Logger.LogError($"[ChatPlugin] Mod_Color_G in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Mod_Color_B == null || config.Mod_Color_B > 255 || config.Mod_Color_B < 0))
                Logger.LogError($"[ChatPlugin] Mod_Color_B in {configFilePath} is invalid!");


            if (config.RankPrefixes == true && config.AdminPrefix == null)
                Logger.LogError($"[ChatPlugin] Admin Prefix in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Admin_Color_R == null || config.Admin_Color_R > 255 || config.Admin_Color_R < 0))
                Logger.LogError($"[ChatPlugin] Admin_Color_R in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Admin_Color_G == null || config.Admin_Color_G > 255 || config.Admin_Color_G < 0))
                Logger.LogError($"[ChatPlugin] Admin_Color_G in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Admin_Color_B == null || config.Admin_Color_B > 255 || config.Admin_Color_B < 0))
                Logger.LogError($"[ChatPlugin] Admin_Color_B in {configFilePath} is invalid!");
        }
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/ChatPlugin/ChatPlugin.cs");

            var versionMatch = Regex.Match(
                fileContent,
                @"public\s+string\s+Version\s*=>\s*""([^""]+)"""
            );

            if (versionMatch.Success)
            {
                var remoteVersionStr = versionMatch.Groups[1].Value;
                var localVersionStr = Version;

                if (System.Version.TryParse(remoteVersionStr, out var remoteVersion) &&
                    System.Version.TryParse(localVersionStr, out var localVersion))
                {
                    if (remoteVersion > localVersion)
                    {
                        Logger.LogWarning(
                            $"[ChatPlugin] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}"
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[ChatPlugin] Error checking for new version: {ex}");
        }
    }
}
