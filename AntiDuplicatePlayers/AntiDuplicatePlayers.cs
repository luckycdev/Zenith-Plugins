using System;
using ServerShared;
using ServerShared.Player;
using ServerShared.Plugins;
using ServerShared.Logging;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

public class AntiDuplicatePlayers : IPlugin
{
    public string Name => "AntiDuplicatePlayers";

    public string Version => "1.0.1";

    public string Author => "luckycdev";

    public void Initialize()
    {
        GameServer.Instance.OnPlayerJoined += OnPlayerJoined;

        Logger.LogInfo($"[{Name}] Initialized!");

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        GameServer.Instance.OnPlayerJoined -= OnPlayerJoined;

        Logger.LogInfo($"[{Name}] Shutdown!");
    }

    private async void OnPlayerJoined(NetPlayer player)
    {
        await Task.Delay(250); // wait a little so it can kick if the ppl join super close together (like when people control r tab spam)

        string playerName = player.Name;
        string playerIP = player.Peer.RemoteEndPoint.Address.ToString();

        foreach (var entry in GameServer.Instance.Players) // check all players
        {
            var otherConnection = entry.Key;
            var otherPlayer = entry.Value;

            if (otherPlayer == player) continue; // dont compare to self

            string otherIP = otherPlayer.Peer.RemoteEndPoint.Address.ToString();

            if (otherPlayer.Name == playerName && otherIP == playerIP)
            {
                if (otherPlayer.Id < player.Id) // kick the older player
                {
                    Logger.LogCustom($"[{Name}] Kicking old duplicate '{otherPlayer.Name}' ({otherIP}), keeping newer player.", ConsoleColor.DarkRed);
                    otherConnection.Disconnect("Duplicate session detected, newer session connected.");
                }
                else
                {
                    Logger.LogCustom($"[{Name}] Kicking new duplicate '{player.Name}' ({playerIP}), keeping older player.", ConsoleColor.DarkRed);
                    player.Peer.Disconnect("Duplicate session detected, please wait before reconnecting.");
                }
                return;
            }
        }
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/AntiDuplicatePlayers/AntiDuplicatePlayers.cs");

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