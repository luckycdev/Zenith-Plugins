using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reflection;
using ServerShared.Plugins;
using ServerShared.Logging;
using ServerShared.Player;
using ServerShared;
using Pyratron.Frameworks.Commands.Parser;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System;

public class MessageConfig
{
    public string Message { get; set; } = "Welcome to {server}!";
    public int? Color_R { get; set; } = 11;
    public int? Color_G { get; set; } = 218;
    public int? Color_B { get; set; } = 81;
}

public class WelcomeMessage : IPlugin
{
    public string Name => "WelcomeMessage";

    public string Version => "1.1.1";

    public string Author => "luckycdev";

    private string configFilePath;
    private MessageConfig config;

    private string toggledListFilePath;
    internal HashSet<string> toggledIPs = new HashSet<string>(); // todo also add steam id support (i cant even get it to run with steam auth rn lol)

    private static bool commandsRegistered = false;

    public static WelcomeMessage Instance { get; private set; }

    private string message;

    private float rgb_r;
    private float rgb_g;
    private float rgb_b;

    public void Initialize()
    {
        Instance = this;

        LoadOrCreateConfig();
        LoadOrCreateToggledList();

        if (!commandsRegistered)
        {
            GameServer.Instance.ChatCommands.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
            commandsRegistered = true;
        }

        GameServer.Instance.OnPlayerJoined += OnPlayerJoined;

        Logger.LogInfo("[WelcomeMessage] Initialized!");

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        GameServer.Instance.OnPlayerJoined -= OnPlayerJoined;
        Logger.LogInfo("[WelcomeMessage] Shutdown!");
    }

    private async void OnPlayerJoined(NetPlayer player)
    {
        string playerIp = player.Peer.RemoteEndPoint.Address.ToString();

        if (toggledIPs.Contains(playerIp))
            return;

        await Task.Delay(50); // wait 0.05 sec so the player can actually see it
        player.SendChatMessage(message, new UnityEngine.Color(rgb_r, rgb_g, rgb_b));
    }

    private void LoadOrCreateConfig()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(pluginFolder, "config.json");

        if (!File.Exists(configFilePath))
        {
            config = new MessageConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configFilePath, json);

            Logger.LogDebug($"[WelcomeMessage] Config file created: {configFilePath}");

            Logger.LogWarning($"[WelcomeMessage] Please update {configFilePath} with your welcome message and message color!");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<MessageConfig>(json);

            rgb_r = config.Color_R.GetValueOrDefault() / 255f;
            rgb_g = config.Color_G.GetValueOrDefault() / 255f;
            rgb_b = config.Color_B.GetValueOrDefault() / 255f;

            message = config.Message.Replace("{server}", GameServer.Instance.Name);

            // check if defaults
            if (config.Message == "Welcome to {server}!")
                Logger.LogWarning($"[WelcomeMessage] Message in {configFilePath} is default!");

            if (config.Color_R == 11)
                Logger.LogWarning($"[WelcomeMessage] Color_R in {configFilePath} is default!");

            if (config.Color_G == 218)
                Logger.LogWarning($"[WelcomeMessage] Color_G in {configFilePath} is default!");

            if (config.Color_B == 81)
                Logger.LogWarning($"[WelcomeMessage] Color_B in {configFilePath} is default!");

            // check if null or not rgb
            if (string.IsNullOrWhiteSpace(config.Message))
                Logger.LogError($"[WelcomeMessage] Message in {configFilePath} is invalid!");

            if (config.Color_R == null || config.Color_R > 255 || config.Color_R < 0)
                Logger.LogError($"[WelcomeMessage] Color_R in {configFilePath} is invalid!");

            if (config.Color_G == null || config.Color_G > 255 || config.Color_G < 0)
                Logger.LogError($"[WelcomeMessage] Color_G in {configFilePath} is invalid!");

            if (config.Color_B == null || config.Color_B > 255 || config.Color_B < 0)
                Logger.LogError($"[WelcomeMessage] Color_B in {configFilePath} is invalid!");
        }
    }

    private void LoadOrCreateToggledList()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        toggledListFilePath = Path.Combine(pluginFolder, "optedOut.json");

        if (!File.Exists(toggledListFilePath))
        {
            toggledIPs = new HashSet<string>();
            var json = JsonSerializer.Serialize(toggledIPs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(toggledListFilePath, json);

            Logger.LogDebug($"[WelcomeMessage] Toggled players list file created: {toggledListFilePath}");
            Logger.LogWarning($"[WelcomeMessage] Players can now toggle the welcome message using /togglewelcome");
        }
        else
        {
            var json = File.ReadAllText(toggledListFilePath);
            toggledIPs = JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
        }
    }

    public void SaveToggledList()
    {
        var json = JsonSerializer.Serialize(toggledIPs, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(toggledListFilePath, json);
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/WelcomeMessage/WelcomeMessage.cs");

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
                        Logger.LogCustom($"[WelcomeMessage] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}", ConsoleColor.Blue);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[WelcomeMessage] Error checking for new version: {ex}");
        }
    }
}

[Command("Toggle welcome message", "togglewelcome", "Toggles the welcome message on or off based on your IP.")]
public class ToggleWelcomeCommand : ChatCommand
{
    public override void Handle(string[] args)
    {
        if (Caller is not NetPlayer netPlayer)
        {
            SendMessage("[WelcomeMessage] This command can only be run by a player.", LogMessageType.Warning); // todo needed?
            return;
        }

        string ip = netPlayer.Peer.RemoteEndPoint.Address.ToString();

        var plugin = WelcomeMessage.Instance;

        if (plugin.toggledIPs.Contains(ip))
        {
            plugin.toggledIPs.Remove(ip);
            plugin.SaveToggledList();
            netPlayer.SendChatMessage("[WelcomeMessage] You will now see the welcome message again.", new UnityEngine.Color(0f, 1f, 0f));
        }
        else
        {
            plugin.toggledIPs.Add(ip);
            plugin.SaveToggledList();
            netPlayer.SendChatMessage("[WelcomeMessage] You will no longer receive the welcome message.", new UnityEngine.Color(1f, 0.5f, 0f));
        }
    }
}