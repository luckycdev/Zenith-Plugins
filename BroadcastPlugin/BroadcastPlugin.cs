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

public class BroadcastConfig
{
    public string BroadcastPrefix { get; set; } = "[BROADCAST]";
    public int? Color_R { get; set; } = 255;
    public int? Color_G { get; set; } = 0;
    public int? Color_B { get; set; } = 0;
}

public class BroadcastPlugin : IPlugin
{
    public string Name => "BroadcastPlugin";

    public string Version => "1.2";

    public string Author => "luckycdev";

    private string configFilePath;
    private BroadcastConfig config;

    public static string broadcastPrefix;
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
        }
        
        Logger.LogInfo("[BroadcastPlugin] Initialized!");

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        Logger.LogInfo("[BroadcastPlugin] Shutdown!");
    }

    private void LoadOrCreateConfig()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(pluginFolder, "config.json");

        if (!File.Exists(configFilePath))
        {
            config = new BroadcastConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configFilePath, json);

            Logger.LogDebug($"[BroadcastPlugin] Config file created: {configFilePath}");

            Logger.LogWarning($"[BroadcastPlugin] To customize the plugin, check out {configFilePath}");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<BroadcastConfig>(json);

            broadcastPrefix = config.BroadcastPrefix;
            rgb_r = config.Color_R.GetValueOrDefault() / 255f;
            rgb_g = config.Color_G.GetValueOrDefault() / 255f;
            rgb_b = config.Color_B.GetValueOrDefault() / 255f;

            // check if null or not rgb
            if (string.IsNullOrWhiteSpace(config.BroadcastPrefix))
                Logger.LogError($"[BroadcastPlugin] Message in {configFilePath} is invalid!");

            if (config.Color_R == null || config.Color_R > 255 || config.Color_R < 0)
                Logger.LogError($"[BroadcastPlugin] Color_R in {configFilePath} is invalid!");

            if (config.Color_G == null || config.Color_G > 255 || config.Color_G < 0)
                Logger.LogError($"[BroadcastPlugin] Color_G in {configFilePath} is invalid!");

            if (config.Color_B == null || config.Color_B > 255 || config.Color_B < 0)
                Logger.LogError($"[BroadcastPlugin] Color_B in {configFilePath} is invalid!");
        }
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/BroadcastPlugin/BroadcastPlugin.cs");

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
                        Logger.LogCustom($"[BroadcastPlugin] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}", ConsoleColor.Blue);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[BroadcastPlugin] Error checking for new version: {ex}");
        }
    }
}

[Command("Broadcast a message to all players in red.", "broadcast", "Broadcast a message.")]
[RequireAuth(AccessLevel.Moderator)]
public class BroadcastChatCommand : ChatCommand
{
    [CommandArgument("Message")]
    public string Message { get; set; }

    public override void Handle(string[] args)
    {
        GameServer.Instance.BroadcastChatMessage($"{BroadcastPlugin.broadcastPrefix} {Message}", new UnityEngine.Color(BroadcastPlugin.rgb_r, BroadcastPlugin.rgb_g, BroadcastPlugin.rgb_b));
    }
}