1. You need to have the [server](https://github.com/luckycdev/Zenith) built (see Zenith README for instructions, or just download the build release)

2. In a folder, `run dotnet new classlib -n PluginName` (change PluginName to your plugins name)

3. Open the csproj in [Visual Studio 2019](https://download.visualstudio.microsoft.com/download/pr/e84651e1-d13a-4bd2-a658-f47a1011ffd1/e17f0d85d70dc9f1e437a78a90dcfc527befe3dc11644e02435bdfe8fd51da27/vs_Community.exe)

4. Right click on Dependencies and click Add Project Reference, then at the bottom right hit browse, then select ServerShared.dll from the build from step 1

5. Create your plugin (see TestPlugin.cs for reference)

6. Build it (I have only tested it with [dotnet 5.0.408](https://builds.dotnet.microsoft.com/dotnet/Sdk/5.0.408/dotnet-sdk-5.0.408-win-x86.exe)

7. Simply add your plugins .dll file from bin/Debug/net5.0/ into the servers plugins/ folder!


For support join https://discord.gg/GVKxbXtbqH
