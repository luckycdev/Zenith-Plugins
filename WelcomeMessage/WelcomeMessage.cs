using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reflection;
using ServerShared.Plugins;
using ServerShared.Logging;
using ServerShared.Player;
using ServerShared;

public class MessageConfig
{
    public string Message { get; set; } = "Welcome to {server}!";
    public int? Color_R { get; set; } = 11;
    public int? Color_G { get; set; } = 218;
    public int? Color_B { get; set; } = 81;
}

public class TestPlugin : IPlugin
{
    public string Name => "WelcomeMessage";

    public string Version => "1.0";

    public string Author => "luckycdev";

    private string configFilePath;
    private MessageConfig config;

    private string message;

    private float rgb_r;
    private float rgb_b;
    private float rgb_g;

    public void Initialize()
    {
        LoadOrCreateConfig();

        GameServer.Instance.OnPlayerJoined += OnPlayerJoined;

        Logger.LogInfo("[WelcomeMessage] Initialized!");
    }

    public void Shutdown()
    {
        GameServer.Instance.OnPlayerJoined -= OnPlayerJoined;
        Logger.LogInfo("[WelcomeMessage] Shutdown!");
    }

    private async void OnPlayerJoined(NetPlayer player)
    {
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
}
