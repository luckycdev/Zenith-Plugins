using System;
using System.Reflection;
using ServerShared.Plugins;
using Pyratron.Frameworks.Commands.Parser;
using ServerShared;
using ServerShared.Player;
using ServerShared.Logging;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;

public class ChatAlertsConfig
{
    public string AlertPrefix { get; set; } = "[ALERT]";
    public int? Color_R { get; set; } = 255;
    public int? Color_G { get; set; } = 0;
    public int? Color_B { get; set; } = 0;
}

public class ChatAlerts : IPlugin
{
    public string Name => "ChatAlerts";

    public string Version => "1.3";

    public string Author => "luckycdev";

    private string configFilePath;
    private ChatAlertsConfig config;

    public static string alertPrefix;
    public static float rgb_r;
    public static float rgb_g;
    public static float rgb_b;

    private static bool commandsRegistered = false;
    public void Initialize()
    {
        LoadOrCreateConfig();

        if (!commandsRegistered)
        {
            GameServer.Instance.ChatCommands.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
            GameServer.Instance.ConsoleCommands.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
            commandsRegistered = true; // to stop commands duplicating on plugin reload
            // TODO: try and make it remove command instead so if reload and the command code changed it will update the command code
        }
        
        Logger.LogInfo($"[{Name}] Initialized!");

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        Logger.LogInfo($"[{Name}] Shutdown!");
    }

    private void LoadOrCreateConfig()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(pluginFolder, "config.json");

        if (!File.Exists(configFilePath))
        {
            config = new ChatAlertsConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configFilePath, json);

            Logger.LogDebug($"[{Name}] Config file created: {configFilePath}");

            Logger.LogWarning($"[{Name}] To customize the plugin, check out {configFilePath}");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<ChatAlertsConfig>(json);

            alertPrefix = config.AlertPrefix;
            rgb_r = config.Color_R.GetValueOrDefault() / 255f;
            rgb_g = config.Color_G.GetValueOrDefault() / 255f;
            rgb_b = config.Color_B.GetValueOrDefault() / 255f;

            // check if null or not rgb
            if (string.IsNullOrWhiteSpace(config.AlertPrefix))
                Logger.LogError($"[{Name}] Message in {configFilePath} is invalid!");

            if (config.Color_R == null || config.Color_R > 255 || config.Color_R < 0)
                Logger.LogError($"[{Name}] Color_R in {configFilePath} is invalid!");

            if (config.Color_G == null || config.Color_G > 255 || config.Color_G < 0)
                Logger.LogError($"[{Name}] Color_G in {configFilePath} is invalid!");

            if (config.Color_B == null || config.Color_B > 255 || config.Color_B < 0)
                Logger.LogError($"[{Name}] Color_B in {configFilePath} is invalid!");
        }
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/Alert/Alert.cs");

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
                        Logger.LogCustom($"[{Name}] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}", ConsoleColor.Blue);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[{Name}] Error checking for new version: {ex}");
        }
    }
}

[Command("Alerts a message to all players.", "alert", "Alerts a message.")]
[RequireAuth(AccessLevel.Moderator)]
public class AlertCommand : ChatCommand
{
    [CommandArgument("Message")]
    public string Message { get; set; }

    public override void Handle(string[] args)
    {
        GameServer.Instance.BroadcastChatMessage($"{ChatAlerts.alertPrefix} {Message}", new UnityEngine.Color(ChatAlerts.rgb_r, ChatAlerts.rgb_g, ChatAlerts.rgb_b));
    }
}