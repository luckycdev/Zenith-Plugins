using System;
using ServerShared.Plugins;

public class TestPlugin : IPlugin
{
    public string Name => "Test Plugin";

    public void Initialize()
    {
        Console.WriteLine("[TestPlugin] Initialized!");
    }

    public void Shutdown()
    {
        Console.WriteLine("[TestPlugin] Shutdown!");
    }
}
