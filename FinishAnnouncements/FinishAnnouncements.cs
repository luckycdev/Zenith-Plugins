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

    public string Version => "1.2";

    public string Author => "luckycdev";

    private string configFilePath;
    private FinishAnnouncementsConfig config;

    private Dictionary<int, DateTime> playerTimers = new();
    private Dictionary<int, DateTime> announcementTimers = new();
    private Dictionary<int, bool> movementChecked = new();
    private Dictionary<int, double> fastStartAdjustments = new(); // stores skipped time from faststart

    private CancellationTokenSource cancellation;

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
        playerTimers[player.Id] = DateTime.UtcNow;
        announcementTimers[player.Id] = DateTime.UtcNow;
        movementChecked[player.Id] = false;
        fastStartAdjustments[player.Id] = 0.0;
    }

    private void OnPlayerLeft(NetPlayer player)
    {
        playerTimers.Remove(player.Id);
        announcementTimers.Remove(player.Id);
        movementChecked.Remove(player.Id);
        fastStartAdjustments.Remove(player.Id);
    }

    private async Task CheckHeightLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var player in GameServer.Instance.Players.Values)
                {
                    float height = player.Movement.Position.y;

                    if (!movementChecked.GetValueOrDefault(player.Id) && playerTimers.TryGetValue(player.Id, out var joinTime))
                    {
                        if (height > -2.3f) // player moved from spawn
                        {
                            movementChecked[player.Id] = true;

                            double timeToMove = (DateTime.UtcNow - joinTime).TotalSeconds;

                            if (timeToMove < 6.0)
                            {
                                double skippedAnimationTime = 6.0 - timeToMove;
                                fastStartAdjustments[player.Id] = skippedAnimationTime;
                            }
                            else
                            {
                                fastStartAdjustments[player.Id] = 0.0;
                            }

                            announcementTimers[player.Id] = joinTime;
                        }
                    }

                    if (announcementTimers.TryGetValue(player.Id, out var startTime))
                    {
                        if (height >= 473f)
                        {
                            TimeSpan duration = DateTime.UtcNow - startTime;
                            double adjustment = fastStartAdjustments.GetValueOrDefault(player.Id);
                            duration += TimeSpan.FromSeconds(adjustment);

                            string formattedTime = FormatTimeSpan(duration);
                            string message = config.Message.Replace("{player}", player.Name).Replace("{time}", formattedTime);
                            GameServer.Instance.BroadcastChatMessage($"{message}", new UnityEngine.Color(rgb_r, rgb_g, rgb_b));

                            // prevent spamming
                            announcementTimers.Remove(player.Id);
                            fastStartAdjustments.Remove(player.Id);
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

            Logger.LogDebug($"[{Name}] Config file created: {configFilePath}");
            Logger.LogWarning($"[{Name}] Please update {configFilePath} with your finish announcement message and message color!");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<FinishAnnouncementsConfig>(json);
        }

        rgb_r = config.Color_R.GetValueOrDefault() / 255f;
        rgb_g = config.Color_G.GetValueOrDefault() / 255f;
        rgb_b = config.Color_B.GetValueOrDefault() / 255f;

        if (string.IsNullOrWhiteSpace(config.Message))
            Logger.LogError($"[{Name}] Message in {configFilePath} is invalid!");
        if (config.Color_R is < 0 or > 255)
            Logger.LogError($"[{Name}] Color_R in {configFilePath} is invalid!");
        if (config.Color_G is < 0 or > 255)
            Logger.LogError($"[{Name}] Color_G in {configFilePath} is invalid!");
        if (config.Color_B is < 0 or > 255)
            Logger.LogError($"[{Name}] Color_B in {configFilePath} is invalid!");
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
                    System.Version.TryParse(localVersionStr, out var localVersion) &&
                    remoteVersion > localVersion)
                {
                    Logger.LogCustom($"[{Name}] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}", ConsoleColor.Blue);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[{Name}] Error checking for new version: {ex}");
        }
    }
}