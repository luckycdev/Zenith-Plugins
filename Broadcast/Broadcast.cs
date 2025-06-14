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

    public void Initialize()
    {
        GameServer.Instance.ChatCommands.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
        GameServer.Instance.ConsoleCommands.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
        Console.WriteLine("[BroadcastPlugin] Initialized!");
    }

    public void Shutdown()
    {
        Console.WriteLine("[BroadcastPlugin] Shutdown!");
    }

    [Command("Broadcast a message to all players from chat.", "broadcast", "Broadcast a message.")]
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

    [Command("Broadcast a message to all players from console.", "broadcast", "Broadcast a message.")]
    public class BroadcastConsoleCommand : BaseCommand
    {
        [CommandArgument("Message")]
        public string Message { get; set; }

        public override void Handle(string[] args)
        {
            GameServer.Instance.BroadcastChatMessage($"[BROADCAST] {Message}", new UnityEngine.Color(1f, 0f, 0f));
        }
    }
}
