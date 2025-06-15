using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reflection;
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
}

public class GOICord : IPlugin
{
    private string configFilePath;
    private BotConfig config;
    private DiscordSocketClient discordClient;
    private ISocketMessageChannel discordChannel;

    public string Name => "GOICord";

    public void Initialize()
    {
        LoadOrCreateConfig();

        Task.Run(async () =>
        {
            await InitializeDiscordBotAsync();
        }).GetAwaiter().GetResult();

        // chat to discord
        GameServer.Instance.OnChatMessageReceived += OnGameChatMessage;

        // discord to chat
        discordClient.MessageReceived += OnDiscordMessageReceived;

        Console.WriteLine("[GOICord] Initialized!");
    }

    public void Shutdown()
    {
        GameServer.Instance.OnChatMessageReceived -= OnGameChatMessage;

        if (discordClient != null)
        {
            discordClient.MessageReceived -= OnDiscordMessageReceived;
            discordClient.LogoutAsync().GetAwaiter().GetResult();
            discordClient.Dispose();
        }

        Console.WriteLine("[GOICord] Shutdown!");
    }

    private async Task OnDiscordMessageReceived(SocketMessage message)
    {
        // ignore bots
        if (message.Author.IsBot) return;

        if (message.Channel.Id != config.ChannelId) return;

        string username = message.Author.Username;
        string content = message.Content.Trim();

        if (string.IsNullOrWhiteSpace(content)) return;

        string gameChatMessage = $"[Discord] {username}: {content}";

        GameServer.Instance.BroadcastChatMessage(gameChatMessage, new UnityEngine.Color(0.345f, 0.396f, 0.949f));

        await Task.CompletedTask;
    }

    private void LoadOrCreateConfig()
    {
        string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(pluginFolder, "GOICord.conf");

        if (!File.Exists(configFilePath))
        {
            config = new BotConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configFilePath, json);
            Console.WriteLine($"[GOICord] Config file created: {configFilePath}");

            Logger.LogWarning("[GOICord] Please update the config file with your bot token and channel ID.");
        }
        else
        {
            var json = File.ReadAllText(configFilePath);
            config = JsonSerializer.Deserialize<BotConfig>(json);

            if (string.IsNullOrWhiteSpace(config.BotToken) || config.BotToken == "YOUR_BOT_TOKEN_HERE")
                Logger.LogWarning("[GOICord] BotToken in config file is not set. Please update it.");

            if (config.ChannelId == 123456789012345678)
                Logger.LogWarning("[GOICord] ChannelId in config file is not set. Please update it.");
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
            Logger.LogWarning($"[GOICord] Could not find channel with ID {config.ChannelId}");
        }
        else
        {
            Console.WriteLine($"[GOICord] Connected to Discord channel {discordChannel.Name}");
        }
    }

    private Task LogDiscordMessage(LogMessage msg)
    {
        Console.WriteLine($"[GOICord Backend] {msg.ToString()}");
        return Task.CompletedTask;
    }

    private void OnGameChatMessage(NetPlayer sender, string message)
    {
        if (discordChannel == null) return;

        var username = sender?.Name ?? "Server";
        var discordMessage = $"**{username}:** {message}";

        // send msg
        _ = discordChannel.SendMessageAsync(discordMessage);
    }
}
