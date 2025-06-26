using ServerShared.Plugins;
using ServerShared.Logging;

using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
public class TestPlugin : IPlugin
{
    public string Name => "TestPlugin";

    public string Version => "1.1";

    public string Author => "luckycdev";

    public void Initialize()
    {
        Logger.LogInfo("[TestPlugin] Initialized!");

        _ = CheckForNewerVersionAsync();
    }

    public void Shutdown()
    {
        Logger.LogInfo("[TestPlugin] Shutdown!");
    }

    private async Task CheckForNewerVersionAsync()
    {
        try
        {
            using var http = new HttpClient();
            var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/TestPlugin/TestPlugin.cs");

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
                        Logger.LogCustom($"[TestPlugin] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}", ConsoleColor.Blue);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[TestPlugin] Error checking for new version: {ex}");
        }
    }
}
