using System;
using System.Reflection;
using ServerShared.Plugins;
using Pyratron.Frameworks.Commands.Parser;
using ServerShared;
using ServerShared.Player;
using ServerShared.Logging;

public class BroadcastPlugin : IPlugin
{
    public string Name => "Broadcast Plugin";

    public string Version => "1.1";

    public string Author => "luckycdev";

    private static bool commandsRegistered = false;
    public void Initialize()
    {
        if (!commandsRegistered)
        {
            GameServer.Instance.ChatCommands.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
            GameServer.Instance.ConsoleCommands.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
            commandsRegistered = true; // to stop commands duplicating on plugin reload    
        }
        
        Logger.LogInfo("[BroadcastPlugin] Initialized!");
    }

    public void Shutdown()
    {
        Logger.LogInfo("[BroadcastPlugin] Shutdown!");
    }

    [Command("Broadcast a message to all players in red.", "broadcast", "Broadcast a message.")]
    [RequireAuth(AccessLevel.Moderator)]
    public class BroadcastChatCommand : ChatCommand
    {
        [CommandArgument("Message")]
        public string Message { get; set; }

        public override void Handle(string[] args)
        {
            GameServer.Instance.BroadcastChatMessage($"[BROADCAST] {Message}", new UnityEngine.Color(1f, 0f, 0f));
        }
    }
}
