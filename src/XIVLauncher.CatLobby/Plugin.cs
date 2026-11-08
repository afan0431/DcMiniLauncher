using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XIVLauncher.CatLobby.Game;

namespace XIVLauncher.CatLobby;

/// <summary>CatLobby 插件入口: 加载即开命名管道, 卸载时关</summary>
public sealed class Plugin : IDalamudPlugin
{
    private readonly CatLobbyPipe pipe;

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log, IDataManager data)
    {
        // Dalamud 的日志文件与 pluginConfigs 目录同级
        var logPath = Path.Combine(pluginInterface.ConfigDirectory.Parent?.Parent?.FullName ?? string.Empty, "dalamud.log");

        pipe = new CatLobbyPipe(framework, new LobbyGame(data, log), log, logPath);
        pipe.Start();

        log.Information("CatLobby 已加载, 管道 {Name}", pipe.PipeName);
    }

    public void Dispose() => pipe.Dispose();
}
