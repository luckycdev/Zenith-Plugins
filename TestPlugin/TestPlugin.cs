using ServerShared.Plugins;
using ServerShared.Logging;

public class TestPlugin : IPlugin
{
    public string Name => "Test Plugin";

    public string Version => "1.0";

    public string Author => "luckycdev";

    public void Initialize()
    {
        Logger.LogInfo("[TestPlugin] Initialized!");
    }

    public void Shutdown()
    {
        Logger.LogInfo("[TestPlugin] Shutdown!");
    }
}
