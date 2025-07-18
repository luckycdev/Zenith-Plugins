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
    public string BannedWords { get; set; } = "badword1,bad word 2,badword 3";
    public bool? BannedWordsStrictMode { get; set; } = true;
    public string JoinMessage { get; set; } = "{player} joined the server";
    public int? JoinMessage_Color_R { get; set; } = 230;
    public int? JoinMessage_Color_G { get; set; } = 241;
    public int? JoinMessage_Color_B { get; set; } = 146;
    public string LeaveMessage { get; set; } = "{player} left the server";
    public int? LeaveMessage_Color_R { get; set; } = 230;
    public int? LeaveMessage_Color_G { get; set; } = 241;
    public int? LeaveMessage_Color_B { get; set; } = 146;
    public bool? RankPrefixes { get; set; } = true;
    public string AdminPrefix { get; set; } = "[Admin] ";
    public string ModPrefix { get; set; } = "[Mod] ";
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

public class ChatManager: IPlugin
{
    public string Name => "ChatManager";

    public string Version => "0.3.2";

    public string Author => "luckycdev";

    private string configFilePath;
    private ChatConfig config;

    private float joinmessage_rgb_r;
    private float joinmessage_rgb_g;
    private float joinmessage_rgb_b;

    private float leavemessage_rgb_r;
    private float leavemessage_rgb_g;
    private float leavemessage_rgb_b;

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
        Logger.LogInfo($"[{Name}] Initialized!");
        ModifyMessages();

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        Logger.LogInfo($"[{Name}] Shutdown!");
    }

    private string ContainsBannedWord(string message)
    {
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

    public void ModifyMessages()
    {
        GameServer.Instance.OnChatMessageModify += (sender, playerName, message, color) =>
        {
            string bannedWord = ContainsBannedWord(message);
            if (bannedWord != null)
            {
                Logger.LogCustom($"[{Name}] Blocked message from {playerName} for containing banned word '{bannedWord}'", ConsoleColor.DarkRed);
                sender.SendChatMessage("Message blocked due to it containing a banned word.", new UnityEngine.Color(1f, 0f, 0f));
                return default; // stop the message from sending
            }

            if (config.RankPrefixes == true && config.RankColors == true)
            {
                if (sender != null && (int)sender.AccessLevel == 0)
                    return ($"{config.PlayerPrefix}{playerName}".Trim(), message, new UnityEngine.Color(player_rgb_r, player_rgb_g, player_rgb_b));

                if (sender != null && (int)sender.AccessLevel == 1)
                    return ($"{config.ModPrefix}{playerName}".Trim(), message, new UnityEngine.Color(mod_rgb_r, mod_rgb_g, mod_rgb_b));

                if (sender != null && (int)sender.AccessLevel == 2)
                    return ($"{config.AdminPrefix}{playerName}".Trim(), message, new UnityEngine.Color(admin_rgb_r, admin_rgb_g, admin_rgb_b));

                return (playerName, message, color);
            }
            else if (config.RankPrefixes == true && config.RankColors == false)
            {
                if (sender != null && (int)sender.AccessLevel == 0)
                    return ($"{config.PlayerPrefix}{playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                if (sender != null && (int)sender.AccessLevel == 1)
                    return ($"{config.ModPrefix}{playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

                if (sender != null && (int)sender.AccessLevel == 2)
                    return ($"{config.AdminPrefix}{playerName}".Trim(), message, new UnityEngine.Color(1f, 1f, 1f));

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

        GameServer.Instance.OnJoinMessageModify += (player) =>
        {
            if (config.JoinMessage == null)
                return default; // dont modify and use default server message

            string msg = config.JoinMessage.Replace("{player}", player.Name);

            if (player != null && (int)player.AccessLevel == 0)
                return ($"{config.PlayerPrefix}{msg}", new UnityEngine.Color(joinmessage_rgb_r, joinmessage_rgb_g, joinmessage_rgb_b));

            if (player != null && (int)player.AccessLevel == 1)
                return ($"{config.ModPrefix}{msg}", new UnityEngine.Color(joinmessage_rgb_r, joinmessage_rgb_g, joinmessage_rgb_b));

            if (player != null && (int)player.AccessLevel == 2)
                return ($"{config.AdminPrefix}{msg}", new UnityEngine.Color(joinmessage_rgb_r, joinmessage_rgb_g, joinmessage_rgb_b));

            return (msg, new UnityEngine.Color(joinmessage_rgb_r, joinmessage_rgb_g, joinmessage_rgb_b));
        };

        GameServer.Instance.OnLeaveMessageModify += (player) =>
        {
            if (config.LeaveMessage == null)
                return default; // dont modify and use default server message

            string msg = config.LeaveMessage.Replace("{player}", player.Name);

            if (player != null && (int)player.AccessLevel == 0)
                return ($"{config.PlayerPrefix}{msg}", new UnityEngine.Color(leavemessage_rgb_r, leavemessage_rgb_g, leavemessage_rgb_b));

            if (player != null && (int)player.AccessLevel == 1)
                return ($"{config.ModPrefix}{msg}", new UnityEngine.Color(leavemessage_rgb_r, leavemessage_rgb_g, leavemessage_rgb_b));

            if (player != null && (int)player.AccessLevel == 2)
                return ($"{config.AdminPrefix}{msg}", new UnityEngine.Color(leavemessage_rgb_r, leavemessage_rgb_g, leavemessage_rgb_b));

            return (msg, new UnityEngine.Color(leavemessage_rgb_r, leavemessage_rgb_g, leavemessage_rgb_b));
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

            joinmessage_rgb_r = config.JoinMessage_Color_R.GetValueOrDefault() / 255f;
            joinmessage_rgb_g = config.JoinMessage_Color_G.GetValueOrDefault() / 255f;
            joinmessage_rgb_b = config.JoinMessage_Color_B.GetValueOrDefault() / 255f;

            leavemessage_rgb_r = config.LeaveMessage_Color_R.GetValueOrDefault() / 255f;
            leavemessage_rgb_g = config.LeaveMessage_Color_G.GetValueOrDefault() / 255f;
            leavemessage_rgb_b = config.LeaveMessage_Color_B.GetValueOrDefault() / 255f;

            player_rgb_r = config.Player_Color_R.GetValueOrDefault() / 255f;
            player_rgb_g = config.Player_Color_G.GetValueOrDefault() / 255f;
            player_rgb_b = config.Player_Color_B.GetValueOrDefault() / 255f;

            mod_rgb_r = config.Mod_Color_R.GetValueOrDefault() / 255f;
            mod_rgb_g = config.Mod_Color_G.GetValueOrDefault() / 255f;
            mod_rgb_b = config.Mod_Color_B.GetValueOrDefault() / 255f;

            admin_rgb_r = config.Admin_Color_R.GetValueOrDefault() / 255f;
            admin_rgb_g = config.Admin_Color_G.GetValueOrDefault() / 255f;
            admin_rgb_b = config.Admin_Color_B.GetValueOrDefault() / 255f;

            Logger.LogDebug($"[{Name}] Config file created: {configFilePath}");

            Logger.LogWarning($"[{Name}] Please see {configFilePath} to change settings!");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<ChatConfig>(json);

            joinmessage_rgb_r = config.JoinMessage_Color_R.GetValueOrDefault() / 255f;
            joinmessage_rgb_g = config.JoinMessage_Color_G.GetValueOrDefault() / 255f;
            joinmessage_rgb_b = config.JoinMessage_Color_B.GetValueOrDefault() / 255f;

            leavemessage_rgb_r = config.LeaveMessage_Color_R.GetValueOrDefault() / 255f;
            leavemessage_rgb_g = config.LeaveMessage_Color_G.GetValueOrDefault() / 255f;
            leavemessage_rgb_b = config.LeaveMessage_Color_B.GetValueOrDefault() / 255f;

            player_rgb_r = config.Player_Color_R.GetValueOrDefault() / 255f;
            player_rgb_g = config.Player_Color_G.GetValueOrDefault() / 255f;
            player_rgb_b = config.Player_Color_B.GetValueOrDefault() / 255f;

            mod_rgb_r = config.Mod_Color_R.GetValueOrDefault() / 255f;
            mod_rgb_g = config.Mod_Color_G.GetValueOrDefault() / 255f;
            mod_rgb_b = config.Mod_Color_B.GetValueOrDefault() / 255f;

            admin_rgb_r = config.Admin_Color_R.GetValueOrDefault() / 255f;
            admin_rgb_g = config.Admin_Color_G.GetValueOrDefault() / 255f;
            admin_rgb_b = config.Admin_Color_B.GetValueOrDefault() / 255f;


            if (config.BannedWords == "badword1,bad word 2,badword 3")
                Logger.LogError($"[{Name}] BannedWords in {configFilePath} is default! Please update it!");

            // check if null
            if (config.BannedWords == null)
                Logger.LogError($"[{Name}] BannedWords in {configFilePath} is invalid!");

            if (!config.BannedWordsStrictMode.HasValue)
                Logger.LogError($"[{Name}] BannedWordsStrictMode in {configFilePath} is invalid!");


            if (config.JoinMessage == null)
                Logger.LogError($"[{Name}] JoinMessage in {configFilePath} is invalid!");

            if (config.LeaveMessage == null)
                Logger.LogError($"[{Name}] LeaveMessage in {configFilePath} is invalid!");


            if (!config.RankPrefixes.HasValue)
                Logger.LogError($"[{Name}] RankPrefixes option in {configFilePath} is null");

            if (!config.RankColors.HasValue)
                Logger.LogError($"[{Name}] RankColors option in {configFilePath} is null");


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


            if (config.RankPrefixes == true && config.PlayerPrefix == null)
                Logger.LogError($"[{Name}] PlayerPrefix in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Player_Color_R == null || config.Player_Color_R > 255 || config.Player_Color_R < 0))
                Logger.LogError($"[{Name}] Player_Color_R in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Player_Color_G == null || config.Player_Color_G > 255 || config.Player_Color_G < 0))
                Logger.LogError($"[{Name}] Player_Color_G in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Player_Color_B == null || config.Player_Color_B > 255 || config.Player_Color_B < 0))
                Logger.LogError($"[{Name}] Player_Color_B in {configFilePath} is invalid!");


            if (config.RankPrefixes == true && config.ModPrefix == null)
                Logger.LogError($"[{Name}] ModPrefix in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Mod_Color_R == null || config.Mod_Color_R > 255 || config.Mod_Color_R < 0))
                Logger.LogError($"[{Name}] Mod_Color_R in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Mod_Color_G == null || config.Mod_Color_G > 255 || config.Mod_Color_G < 0))
                Logger.LogError($"[{Name}] Mod_Color_G in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Mod_Color_B == null || config.Mod_Color_B > 255 || config.Mod_Color_B < 0))
                Logger.LogError($"[{Name}] Mod_Color_B in {configFilePath} is invalid!");


            if (config.RankPrefixes == true && config.AdminPrefix == null)
                Logger.LogError($"[{Name}] AdminPrefix in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Admin_Color_R == null || config.Admin_Color_R > 255 || config.Admin_Color_R < 0))
                Logger.LogError($"[{Name}] Admin_Color_R in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Admin_Color_G == null || config.Admin_Color_G > 255 || config.Admin_Color_G < 0))
                Logger.LogError($"[{Name}] Admin_Color_G in {configFilePath} is invalid!");

            if (config.RankColors == true && (config.Admin_Color_B == null || config.Admin_Color_B > 255 || config.Admin_Color_B < 0))
                Logger.LogError($"[{Name}] Admin_Color_B in {configFilePath} is invalid!");
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