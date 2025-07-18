using System;
using System.Collections.Generic;
using System.Threading;
using ServerShared;
using ServerShared.Player;
using ServerShared.Plugins;
using ServerShared.Logging;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Reflection;
using System.IO;
using System.Text.Json;

public class FinishAnnouncementsConfig
{
    public string Message { get; set; } = "{player} beat the game in ~{time}!";
    public int? Color_R { get; set; } = 0;
    public int? Color_G { get; set; } = 255;
    public int? Color_B { get; set; } = 255;
}

public class FinishAnnouncements : IPlugin
{
    public string Name => "FinishAnnouncements";

    public string Version => "1.1.1";

    public string Author => "luckycdev";

    private string configFilePath;
    private FinishAnnouncementsConfig config;

    private Dictionary<int, DateTime> playerTimers = new();
    private CancellationTokenSource cancellation;

    private string message;

    private float rgb_r;
    private float rgb_g;
    private float rgb_b;

    public void Initialize()
    {
        LoadOrCreateConfig();

        GameServer.Instance.OnPlayerJoined += OnPlayerJoined;
        GameServer.Instance.OnPlayerLeft += OnPlayerLeft;

        cancellation = new CancellationTokenSource();

        Logger.LogInfo($"[{Name}] Initialized!");

        _ = CheckForNewerVersionAsync();

        _ = CheckHeightLoopAsync(cancellation.Token);
    }

    public void Shutdown()
    {
        cancellation.Cancel();

        GameServer.Instance.OnPlayerJoined -= OnPlayerJoined;
        GameServer.Instance.OnPlayerLeft -= OnPlayerLeft;

        Logger.LogInfo($"[{Name}] Shutdown!");
    }

    private void OnPlayerJoined(NetPlayer player)
    {
        playerTimers[player.Id] = DateTime.UtcNow; // start or restart timer
    }

    private void OnPlayerLeft(NetPlayer player)
    {
        playerTimers.Remove(player.Id);
    }

    private async Task CheckHeightLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var player in GameServer.Instance.Players.Values)
                {
                    if (playerTimers.TryGetValue(player.Id, out var startTime))
                    {
                        float height = player.Movement.Position.y;
                        if (height >= 472f)
                        {
                            TimeSpan duration = DateTime.UtcNow - startTime;
                            string formattedTime = FormatTimeSpan(duration);
                            message = config.Message.Replace("{player}", player.Name).Replace("{time}", formattedTime);
                            GameServer.Instance.BroadcastChatMessage($"{message}", new UnityEngine.Color(rgb_r, rgb_g, rgb_b));
                            playerTimers.Remove(player.Id); // prevent spamming
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[{Name}] Error during height check: {ex}");
            }

            await Task.Delay(50, token); // check height every 50 ms
        }
    }

    private string FormatTimeSpan(TimeSpan timespan)
    {
        if (timespan.TotalHours >= 1)
            return $"{(int)timespan.TotalHours:D2}:{timespan.Minutes:D2}:{timespan.Seconds:D2}";

        return $"{timespan.Minutes:D2}:{timespan.Seconds:D2}";
    }

    private void LoadOrCreateConfig()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(pluginFolder, "config.json");

        if (!File.Exists(configFilePath))
        {
            config = new FinishAnnouncementsConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configFilePath, json);

            rgb_r = config.Color_R.GetValueOrDefault() / 255f;
            rgb_g = config.Color_G.GetValueOrDefault() / 255f;
            rgb_b = config.Color_B.GetValueOrDefault() / 255f;

            Logger.LogDebug($"[{Name}] Config file created: {configFilePath}");

            Logger.LogWarning($"[{Name}] Please update {configFilePath} with your finish announcement message and message color!");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<FinishAnnouncementsConfig>(json);

            rgb_r = config.Color_R.GetValueOrDefault() / 255f;
            rgb_g = config.Color_G.GetValueOrDefault() / 255f;
            rgb_b = config.Color_B.GetValueOrDefault() / 255f;

            // check if null or not rgb
            if (string.IsNullOrWhiteSpace(config.Message))
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
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/FinishAnnouncements/FinishAnnouncements.cs");

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
