using XIVLauncher.Common.Game;
using XIVLauncher.Login.Client;

namespace XIVLauncher.Login.Models;

public sealed class GameLaunchContext
(
    LoginResult    loginResult,
    LoginArea      area,
    LoginArea[]    areas,
    XIVAccountType accountType
)
{
    public LoginResult    LoginResult  { get; set; } = loginResult;
    public LoginArea      Area         { get; set; } = area;
    public LoginArea[]    Areas        { get; }      = areas;
    public XIVAccountType AccountType  { get; }      = accountType;
    public int            DcTravelPort { get; set; }

    /// <summary>
    ///     本次启动实际会有哪些游戏内代理, 在起游戏时确定（Dalamud 那一位取的是真实注入结果, 不是设置值）。
    ///     决定游戏内跨大区由谁执行: Dalamud → DCTravelerX 插件; 只 Minion → 注入的 native 模块。
    /// </summary>
    public InGameAgents InGameAgents { get; set; }
}
