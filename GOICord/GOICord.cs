﻿using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reflection;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using ServerShared;
using ServerShared.Player;
using ServerShared.Plugins;
using ServerShared.Logging;
using System.Net.Http;

public class BotConfig
{
    public string BotToken { get; set; } = "YOUR_BOT_TOKEN_HERE";
    public ulong? ChannelId { get; set; } = 123456789012345678;
    public bool JoinLeaveMessages { get; set; } = false;
    public bool StartStopMessages { get; set; } = true;
}

public class GOICord : IPlugin
{
    private string configFilePath;
    private BotConfig config;
    private DiscordSocketClient discordClient;
    private ISocketMessageChannel discordChannel;

    private static readonly Regex customEmojiRegex = new Regex(@"<a?:(\w+):\d+>", RegexOptions.Compiled);

    private static readonly Regex unicodeEmojiRegex = new Regex(
        @"([\uD800-\uDBFF][\uDC00-\uDFFF])",  // unicode characters like emojis
        RegexOptions.Compiled);

    private static readonly Regex mentionRegex = new Regex(@"<@!?(\d+)>|<@&(\d+)>|@everyone|@here", RegexOptions.Compiled); // make it so you cant ping users, roles, everyone, and here

    public string Name => "GOICord";

    public string Version => "0.8";

    public string Author => "luckycdev";

    public void Initialize()
    {
        LoadOrCreateConfig();

        try
        {
            Task.Run(async () =>
            {
                await InitializeDiscordBotAsync();
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError($"[GOICord] Failed to initialize Discord bot: {ex.Message}");
            return; // give up loading the plugin so it doesnt keep looping forever
        }

        if (discordClient == null || discordChannel == null) // dont let it continue trying if it isnt working
        {
            Logger.LogError("[GOICord] Initialization aborted due to being unable to connect!");
            return;
        }

        // chat to discord
        GameServer.Instance.OnChatMessageFinal += OnGameChatMessageFinal;

        if (config.JoinLeaveMessages == true)
        {
            GameServer.Instance.OnJoinMessageFinal += OnJoinMessageFinal;
            GameServer.Instance.OnLeaveMessageFinal += OnLeaveMessageFinal;
        }

        // discord to chat
        discordClient.MessageReceived += OnDiscordMessageReceived;

        Logger.LogInfo("[GOICord] Initialized!");

        if (config.StartStopMessages == true)
        {
            if (discordChannel != null)
                _ = discordChannel.SendMessageAsync($"🟢 Server **{GameServer.Instance.Name}** Started!");
        }

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        GameServer.Instance.OnChatMessageFinal -= OnGameChatMessageFinal;

        if (config.StartStopMessages == true)
        {
            if (discordChannel != null)
                _ = discordChannel.SendMessageAsync($"🔴 Server **{GameServer.Instance.Name}** Stopped!").GetAwaiter().GetResult();
        }

        if (config.JoinLeaveMessages == true)
        {
            GameServer.Instance.OnJoinMessageFinal -= OnJoinMessageFinal;
            GameServer.Instance.OnLeaveMessageFinal -= OnLeaveMessageFinal;
        }

        if (discordClient != null)
        {
            discordClient.LogoutAsync().GetAwaiter().GetResult();
            discordClient.Dispose();
        }

        Logger.LogInfo("[GOICord] Shutdown!");
    }

    private async Task OnDiscordMessageReceived(SocketMessage message)
    {
        // ignore bots
        if (message.Author.IsBot) return;

        if (message.Channel.Id != config.ChannelId) return;

        string username = message.Author.Username;

        string content = ConvertDiscordMessage(message.Content.Trim());

        if (string.IsNullOrWhiteSpace(content)) return;

        if (content == "!info")
        {
            var builder = new System.Text.StringBuilder();

            builder.AppendLine($"Name: {GameServer.Instance.Name}");
            builder.AppendLine($"Player Count: {GameServer.Instance.Players.Count}/{GameServer.Instance.MaxPlayers}");

            if (GameServer.Instance.Players.Count != 0)
            {
                builder.AppendLine("Player List:");

                foreach (var player in GameServer.Instance.Players.Values)
                {
                    var safePlayerName = mentionRegex.Replace(player.Name, "[mention]");
                    builder.AppendLine($"- {safePlayerName}");
                }
            }

            builder.AppendLine($"-# GOICord version {Version} on Zenith version {SharedConstants.ZenithVersion} (GOIMP version {SharedConstants.Version})");

            _ = discordChannel.SendMessageAsync(builder.ToString());
            return; // dont send to game chat
        }

        string gameChatMessage = $"[Discord] {username}: {content}";

        GameServer.Instance.BroadcastChatMessage(gameChatMessage, new UnityEngine.Color(0.345f, 0.396f, 0.949f));

        await Task.CompletedTask;
    }

    private void OnGameChatMessageFinal(string playerName, string message, UnityEngine.Color color)
    {
        if (discordChannel == null) return;

        if (string.IsNullOrWhiteSpace(playerName)) return; // prevent duplicate join and leave messages

        var safeMessage = mentionRegex.Replace(message, "[mention]");

        var discordMessage = $"**{playerName}:** {safeMessage}";

        // send msg
        _ = discordChannel.SendMessageAsync(discordMessage);
    }

    private void LoadOrCreateConfig()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(pluginFolder, "config.json");

        if (!File.Exists(configFilePath))
        {
            config = new BotConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configFilePath, json);

            Logger.LogDebug($"[GOICord] Config file created: {configFilePath}");

            Logger.LogError($"[GOICord] Please update {configFilePath} with your bot token and channel ID!");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<BotConfig>(json);

            if (string.IsNullOrWhiteSpace(config.BotToken) || config.BotToken == "YOUR_BOT_TOKEN_HERE")
                Logger.LogError($"[GOICord] BotToken in {configFilePath} is not set!");

            if (config.ChannelId == 123456789012345678)
                Logger.LogError($"[GOICord] ChannelId in {configFilePath} is not set!");

            if (config.ChannelId == null)
            {
                Logger.LogError($"[GOICord] ChannelId in {configFilePath} is invalid!");
                throw new Exception($"Discord channel ID '{config.ChannelId.Value}' not found! Please check {configFilePath}");
            }
        }
    }

    private async Task InitializeDiscordBotAsync()
    {
        var socketConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                             | GatewayIntents.GuildMessages
                             | GatewayIntents.MessageContent
        };

        discordClient = new DiscordSocketClient(socketConfig);
        discordClient.Log += LogDiscordMessage;

        try
        {
            await discordClient.LoginAsync(TokenType.Bot, config.BotToken);
            await discordClient.StartAsync();

            // wait until the bot is on
            await Task.Delay(3000);

            discordChannel = discordClient.GetChannel(config.ChannelId.Value) as ISocketMessageChannel;

            if (discordChannel == null)
            {
                throw new Exception($"Discord channel not found! Please check {configFilePath}");
            }
        }
        catch (Exception ex) // give up to prevent looping and preventing other plugins from loading
        {
            Logger.LogError($"[GOICord] Discord bot connection failed: {ex.Message}");
            discordClient.Dispose();
            discordClient = null;
            discordChannel = null;
            throw;
        }

        Logger.LogInfo($"[GOICord] Connected to Discord channel {discordChannel.Name}");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await UpdateBotStatus();
                await Task.Delay(5000); // update status every 5 seconds
            }
        });
    }

    private async Task UpdateBotStatus()
    {
        try
        {
            if (GameServer.Instance.Players.Count == 1)
            {
                await discordClient.SetActivityAsync(
                    new Game($"{GameServer.Instance.Players.Count} player on {GameServer.Instance.Name}", ActivityType.Watching));
            }
            else
            {
                await discordClient.SetActivityAsync(
                    new Game($"{GameServer.Instance.Players.Count} players on {GameServer.Instance.Name}", ActivityType.Watching));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[GOICord] Failed to update bot status: {ex}");
        }
    }

    private Task LogDiscordMessage(LogMessage msg)
    {
        Logger.LogDebug($"[GOICord Backend] {msg.ToString()}");
        return Task.CompletedTask;
    }

    private void OnJoinMessageFinal(string message, UnityEngine.Color color)
    {
        if (discordChannel != null)
            _ = discordChannel.SendMessageAsync($"**{message}**");
    }

    private void OnLeaveMessageFinal(string message, UnityEngine.Color color)
    {
        if (discordChannel != null)
            _ = discordChannel.SendMessageAsync($"**{message}**");
    }

    private string ConvertDiscordMessage(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // replace discord <name:id> emojis with :name:
        string replacedCustom = customEmojiRegex.Replace(content, m => $":{m.Groups[1].Value}:");

        // replace unicode characters with "[emoji]"
        string final = unicodeEmojiRegex.Replace(replacedCustom, "[emoji]");

        return final;
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/GOICord/GOICord.cs");

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
                        Logger.LogCustom($"[GOICord] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}", ConsoleColor.Blue);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[GOICord] Error checking for new version: {ex}");
        }
    }
}