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

public class BotConfig
{
    public string BotToken { get; set; } = "YOUR_BOT_TOKEN_HERE";
    public ulong ChannelId { get; set; } = 123456789012345678;
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

    public string Name => "GOICord";

    public string Version => "0.3.1";

    public string Author => "luckycdev";

    public void Initialize()
    {
        LoadOrCreateConfig();

        Task.Run(async () =>
        {
            await InitializeDiscordBotAsync();
        }).GetAwaiter().GetResult();

        // chat to discord
        GameServer.Instance.OnChatMessageReceived += OnGameChatMessage;

        if (config.JoinLeaveMessages == true)
        {
            GameServer.Instance.OnPlayerJoined += OnPlayerJoined;
            GameServer.Instance.OnPlayerLeft += OnPlayerLeft;
        }

        // discord to chat
        discordClient.MessageReceived += OnDiscordMessageReceived;

        Logger.LogInfo("[GOICord] Initialized!");

        if (config.StartStopMessages == true)
        {
            if (discordChannel != null)
                _ = discordChannel.SendMessageAsync($"🟢 Server **{GameServer.Instance.Name}** Started!");
        }
    }

    public void Shutdown()
    {
        GameServer.Instance.OnChatMessageReceived -= OnGameChatMessage;

        if (config.StartStopMessages == true)
        {
            if (discordChannel != null)
                _ = discordChannel.SendMessageAsync($"🔴 Server **{GameServer.Instance.Name}** Stopped!").GetAwaiter().GetResult();
        }

        if (config.JoinLeaveMessages == true)
        {
            GameServer.Instance.OnPlayerJoined -= OnPlayerJoined;
            GameServer.Instance.OnPlayerLeft -= OnPlayerLeft;
        }

        if (discordClient != null)
        {
            discordClient.MessageReceived -= OnDiscordMessageReceived;
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
        string rawcontent = message.Content.Trim();

        string content = ConvertDiscordMessage(rawcontent);

        if (string.IsNullOrWhiteSpace(content)) return;

        if (rawcontent == "!info")
        {
            var builder = new System.Text.StringBuilder();

            builder.AppendLine($"Name: {GameServer.Instance.Name}");
            builder.AppendLine($"Player Count: {GameServer.Instance.Players.Count}/{GameServer.Instance.MaxPlayers}");

            if (GameServer.Instance.Players.Count != 0)
            {
                builder.AppendLine("Player List:");

                foreach (var player in GameServer.Instance.Players.Values)
                {
                    builder.AppendLine($"- {player.Name}");
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

    private void OnGameChatMessage(NetPlayer sender, string message)
    {
        if (discordChannel == null) return;

        var username = sender?.Name ?? "Server";
        var discordMessage = $"**{username}:** {message}";

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

            Logger.LogError("[GOICord] Please update the config file with your bot token and channel ID.");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<BotConfig>(json);

            if (string.IsNullOrWhiteSpace(config.BotToken) || config.BotToken == "YOUR_BOT_TOKEN_HERE")
                Logger.LogError("[GOICord] BotToken in config file is not set. Please update it.");

            if (config.ChannelId == 123456789012345678)
                Logger.LogError("[GOICord] ChannelId in config file is not set. Please update it.");
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

        await discordClient.LoginAsync(TokenType.Bot, config.BotToken);
        await discordClient.StartAsync();

        // wait until the bot is on
        await Task.Delay(3000);

        discordChannel = discordClient.GetChannel(config.ChannelId) as ISocketMessageChannel;

        if (discordChannel == null)
        {
            Logger.LogError($"[GOICord] Could not find channel with ID {config.ChannelId}");
        }
        else
        {
            Logger.LogInfo($"[GOICord] Connected to Discord channel {discordChannel.Name}");
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                UpdateBotStatus();
                await Task.Delay(5000); // update status every 5 seconds
            }
        });

    }

    private void UpdateBotStatus()
    {
        _ = discordClient.SetActivityAsync(new Game($"{GameServer.Instance.Players.Count} players on {GameServer.Instance.Name}", ActivityType.Watching));
    }

    private Task LogDiscordMessage(LogMessage msg)
    {
        Logger.LogDebug($"[GOICord Backend] {msg.ToString()}");
        return Task.CompletedTask;
    }

    private void OnPlayerJoined(NetPlayer player)
    {
        if (discordChannel != null)
            _ = discordChannel.SendMessageAsync($"**{player.Name} joined the server.**");
    }

    private void OnPlayerLeft(NetPlayer player)
    {
        if (discordChannel != null)
            _ = discordChannel.SendMessageAsync($"**{player.Name} left the server.**");
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
}