using ServerShared.Plugins;
using ServerShared.Logging;
using ServerShared.Player;
using ServerShared;
using System.Text.Json;
using System.Reflection;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

public class RankStyle
{
    public string Prefix { get; set; } = "";
    public int? Color_R { get; set; } = 255;
    public int? Color_G { get; set; } = 255;
    public int? Color_B { get; set; } = 255;
}

public class ChatConfig
{
    public bool? BanWords { get; set; } = true;
    public string BannedWords { get; set; } = "badword1,bad word 2,badword 3";
    public bool? BannedWordsStrictMode { get; set; } = true;
    public bool? ChangeJoinMessages { get; set; } = true;
    public string JoinMessage { get; set; } = "{player} joined the server";
    public int? JoinMessage_Color_R { get; set; } = 230;
    public int? JoinMessage_Color_G { get; set; } = 241;
    public int? JoinMessage_Color_B { get; set; } = 146;
    public bool? ChangeLeaveMessages { get; set; } = true;
    public string LeaveMessage { get; set; } = "{player} left the server";
    public int? LeaveMessage_Color_R { get; set; } = 230;
    public int? LeaveMessage_Color_G { get; set; } = 241;
    public int? LeaveMessage_Color_B { get; set; } = 146;
    public bool? RankPrefixes { get; set; } = true;
    public bool? RankColors { get; set; } = true;
    // Chat prefix and color per rank name (the rank names from Zenith's permissions.json).
    // Ranks that aren't listed here get no prefix and white text.
    public Dictionary<string, RankStyle> Ranks { get; set; } = DefaultRankStyles();

    public static Dictionary<string, RankStyle> DefaultRankStyles() => new Dictionary<string, RankStyle>(StringComparer.OrdinalIgnoreCase)
    {
        [DefaultRanks.Member] = new RankStyle { Prefix = "", Color_R = 255, Color_G = 255, Color_B = 255 },
        [DefaultRanks.Moderator] = new RankStyle { Prefix = "[Mod] ", Color_R = 151, Color_G = 255, Color_B = 104 },
        [DefaultRanks.Admin] = new RankStyle { Prefix = "[Admin] ", Color_R = 255, Color_G = 128, Color_B = 128 },
    };
}

public class ChatManager: IPlugin
{
    public string Name => "ChatManager";

    public string Version => "0.4.3";

    public string Author => "luckycdev";

    private string configFilePath;
    private ChatConfig config;

    private float joinmessage_rgb_r;
    private float joinmessage_rgb_g;
    private float joinmessage_rgb_b;

    private float leavemessage_rgb_r;
    private float leavemessage_rgb_g;
    private float leavemessage_rgb_b;

    public void Initialize()
    {
        LoadOrCreateConfig();
        Logger.LogInfo($"[{Name}] Initialized!");
        ModifyMessages();

        if (config.BanWords == true)
        {
            GameServer.Instance.OnPlayerJoined += BadNameKick;
        }

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        Logger.LogInfo($"[{Name}] Shutdown!");

        if (config.BanWords == true)
        {
            GameServer.Instance.OnPlayerJoined -= BadNameKick;
        }
    }

    // Gets the chat prefix and color for a player's rank. Ranks not listed in the config get no prefix and white.
    private (string Prefix, UnityEngine.Color Color) GetRankStyle(NetPlayer player)
    {
        if (config.Ranks != null
            && !string.IsNullOrWhiteSpace(player.RankName)
            && config.Ranks.TryGetValue(player.RankName, out var style)
            && style != null)
        {
            return (style.Prefix ?? "", new UnityEngine.Color(
                style.Color_R.GetValueOrDefault(255) / 255f,
                style.Color_G.GetValueOrDefault(255) / 255f,
                style.Color_B.GetValueOrDefault(255) / 255f));
        }

        return ("", new UnityEngine.Color(1f, 1f, 1f));
    }

    private string ContainsBannedWord(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        if (string.IsNullOrWhiteSpace(config.BannedWords))
            return null;

        string[] bannedWords = config.BannedWords.Split(',', StringSplitOptions.RemoveEmptyEntries);
        string lowerMessage = message.ToLowerInvariant();

        foreach (string phrase in bannedWords)
        {
            string bannedWord = phrase.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bannedWord))
                continue;

            if (config.BannedWordsStrictMode == true)
            {
                if (lowerMessage.Contains(bannedWord))
                    return bannedWord;
            }
            else
            {
                string pattern = Regex.Replace(Regex.Escape(bannedWord), @"\\\s+", @"\s+");
                string fullPattern = $@"(?<=^|\s|[.,!?;:]){pattern}(?=$|\s|[.,!?;:])";

                if (Regex.IsMatch(message, fullPattern, RegexOptions.IgnoreCase))
                    return bannedWord;
            }
        }

        return null;
    }

    private string NameCheck(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";//TODO console shows a : before it (only in console) - to replicate disable rankprefixes and then join or leave

        string bannedWord = ContainsBannedWord(name);
        if (bannedWord != null)
        {
            Logger.LogCustom($"[{Name}] Hid player name '{name}' for containing banned word '{bannedWord}'", ConsoleColor.DarkRed);
            return "Player";
        }

        return name;
    }

    private void BadNameKick(NetPlayer player)
    {
        if (config.BanWords == true)
        {
            string bannedWord = ContainsBannedWord(player.Name);
            if (bannedWord != null)
            {
                player.Peer.Disconnect("You were kicked from the server for having an inappropriate name.");
            }
        }
    }

    public void ModifyMessages()
    {
        GameServer.Instance.OnChatMessageModify += (sender, playerName, message, color) =>
        {
            string safeName;

            if (config.BanWords == true)
            {
                safeName = NameCheck(playerName);
            }
            else
            {
                safeName = playerName;
            }

            string bannedWord = ContainsBannedWord(message);

            if (config.BanWords == true && bannedWord != null)
            {
                Logger.LogCustom($"[{Name}] Blocked message from {playerName} for containing banned word '{bannedWord}'", ConsoleColor.DarkRed);
                sender.SendChatMessage("Message blocked due to it containing a banned word.", new UnityEngine.Color(1f, 0f, 0f));
                return default; // stop the message from sending
            }

            if (sender == null)
                return (config.RankPrefixes == true ? playerName : safeName, message, color);

            var style = GetRankStyle(sender);

            string displayName = config.RankPrefixes == true
                ? $"{style.Prefix}{safeName}".Trim()
                : safeName.Trim();

            UnityEngine.Color displayColor = config.RankColors == true
                ? style.Color
                : new UnityEngine.Color(1f, 1f, 1f);

            return (displayName, message, displayColor);
        };

        if (config.ChangeJoinMessages == true)
        {
            GameServer.Instance.OnJoinMessageModify += (player) =>
            {
                string safeName;

                if (config.BanWords == true)
                {
                    safeName = NameCheck(player.Name);
                }
                else
                {
                    safeName = player.Name;
                }

                if (string.IsNullOrWhiteSpace(config.JoinMessage)) // empty message = don't send anything
                    return ("", new UnityEngine.Color(1f, 1f, 1f));

                string msg = config.JoinMessage.Replace("{player}", safeName);

                if (config.RankPrefixes == true)
                    return ($"{GetRankStyle(player).Prefix}{msg}", new UnityEngine.Color(joinmessage_rgb_r, joinmessage_rgb_g, joinmessage_rgb_b));

                return (msg, new UnityEngine.Color(joinmessage_rgb_r, joinmessage_rgb_g, joinmessage_rgb_b));
            };
        }

        if (config.ChangeLeaveMessages == true)
        {
            GameServer.Instance.OnLeaveMessageModify += (player) =>
            {
                string safeName;

                if (config.BanWords == true)
                {
                    safeName = NameCheck(player.Name);
                }
                else
                {
                    safeName = player.Name;
                }

                if (string.IsNullOrWhiteSpace(config.LeaveMessage)) // empty message = don't send anything
                    return ("", new UnityEngine.Color(1f, 1f, 1f));

                string msg = config.LeaveMessage.Replace("{player}", safeName);

                if (config.RankPrefixes == true)
                    return ($"{GetRankStyle(player).Prefix}{msg}", new UnityEngine.Color(leavemessage_rgb_r, leavemessage_rgb_g, leavemessage_rgb_b));

                return (msg, new UnityEngine.Color(leavemessage_rgb_r, leavemessage_rgb_g, leavemessage_rgb_b));
            };
        }
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

            joinmessage_rgb_r = config.JoinMessage_Color_R.GetValueOrDefault() / 255f;
            joinmessage_rgb_g = config.JoinMessage_Color_G.GetValueOrDefault() / 255f;
            joinmessage_rgb_b = config.JoinMessage_Color_B.GetValueOrDefault() / 255f;

            leavemessage_rgb_r = config.LeaveMessage_Color_R.GetValueOrDefault() / 255f;
            leavemessage_rgb_g = config.LeaveMessage_Color_G.GetValueOrDefault() / 255f;
            leavemessage_rgb_b = config.LeaveMessage_Color_B.GetValueOrDefault() / 255f;

            Logger.LogDebug($"[{Name}] Config file created: {configFilePath}");

            Logger.LogWarning($"[{Name}] Please see {configFilePath} to change settings!");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<ChatConfig>(json);

            // Rank names are case-insensitive.
            if (config.Ranks != null)
                config.Ranks = new Dictionary<string, RankStyle>(config.Ranks, StringComparer.OrdinalIgnoreCase);

            joinmessage_rgb_r = config.JoinMessage_Color_R.GetValueOrDefault() / 255f;
            joinmessage_rgb_g = config.JoinMessage_Color_G.GetValueOrDefault() / 255f;
            joinmessage_rgb_b = config.JoinMessage_Color_B.GetValueOrDefault() / 255f;

            leavemessage_rgb_r = config.LeaveMessage_Color_R.GetValueOrDefault() / 255f;
            leavemessage_rgb_g = config.LeaveMessage_Color_G.GetValueOrDefault() / 255f;
            leavemessage_rgb_b = config.LeaveMessage_Color_B.GetValueOrDefault() / 255f;


            if (!config.RankPrefixes.HasValue)
                Logger.LogError($"[{Name}] RankPrefixes option in {configFilePath} is null");

            if (!config.RankColors.HasValue)
                Logger.LogError($"[{Name}] RankColors option in {configFilePath} is null");

            if (!config.BanWords.HasValue)
                Logger.LogError($"[{Name}] BanWords option in {configFilePath} is null");

            if (!config.ChangeJoinMessages.HasValue)
                Logger.LogError($"[{Name}] ChangeJoinMessages option in {configFilePath} is null");

            if (!config.ChangeLeaveMessages.HasValue)
                Logger.LogError($"[{Name}] ChangeLeaveMessages option in {configFilePath} is null");


            if (config.BanWords == true && config.BannedWords == "badword1,bad word 2,badword 3")
                Logger.LogError($"[{Name}] BannedWords in {configFilePath} is default! Please update it!");

            if (config.BanWords == true && config.BannedWords == null)
                Logger.LogError($"[{Name}] BannedWords in {configFilePath} is invalid!");

            if (config.BanWords == true && !config.BannedWordsStrictMode.HasValue)
                Logger.LogError($"[{Name}] BannedWordsStrictMode in {configFilePath} is invalid!");




            if (config.JoinMessage_Color_R == null || config.JoinMessage_Color_R > 255 || config.JoinMessage_Color_R < 0)
                Logger.LogError($"[{Name}] JoinMessage_Color_R in {configFilePath} is invalid!");

            if (config.JoinMessage_Color_G == null || config.JoinMessage_Color_G > 255 || config.JoinMessage_Color_G < 0)
                Logger.LogError($"[{Name}] JoinMessage_Color_G in {configFilePath} is invalid!");

            if (config.JoinMessage_Color_B == null || config.JoinMessage_Color_B > 255 || config.JoinMessage_Color_B < 0)
                Logger.LogError($"[{Name}] JoinMessage_Color_B in {configFilePath} is invalid!");


            if (config.LeaveMessage_Color_R == null || config.LeaveMessage_Color_R > 255 || config.LeaveMessage_Color_R < 0)
                Logger.LogError($"[{Name}] LeaveMessage_Color_R in {configFilePath} is invalid!");

            if (config.LeaveMessage_Color_G == null || config.LeaveMessage_Color_G > 255 || config.LeaveMessage_Color_G < 0)
                Logger.LogError($"[{Name}] LeaveMessage_Color_G in {configFilePath} is invalid!");

            if (config.LeaveMessage_Color_B == null || config.LeaveMessage_Color_B > 255 || config.LeaveMessage_Color_B < 0)
                Logger.LogError($"[{Name}] LeaveMessage_Color_B in {configFilePath} is invalid!");


            if (config.Ranks != null)
            {
                foreach (var entry in config.Ranks)
                {
                    if (entry.Value == null)
                    {
                        Logger.LogError($"[{Name}] Ranks entry '{entry.Key}' in {configFilePath} is invalid!");
                        continue;
                    }

                    if (config.RankPrefixes == true && entry.Value.Prefix == null)
                        Logger.LogError($"[{Name}] Prefix for rank '{entry.Key}' in {configFilePath} is invalid!");

                    if (config.RankColors == true && (entry.Value.Color_R == null || entry.Value.Color_R > 255 || entry.Value.Color_R < 0
                        || entry.Value.Color_G == null || entry.Value.Color_G > 255 || entry.Value.Color_G < 0
                        || entry.Value.Color_B == null || entry.Value.Color_B > 255 || entry.Value.Color_B < 0))
                        Logger.LogError($"[{Name}] Color for rank '{entry.Key}' in {configFilePath} is invalid!");
                }
            }
        }
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/ChatManager/ChatManager.cs");

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
                            $"[{Name}] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}"
                        );
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