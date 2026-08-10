namespace XIVLauncher.Login.Models;

/// <summary>
///     本次启动实际会存在于游戏进程里的代理。跨大区必须有代理才能在游戏内完成：
///     Dalamud → DCTravelerX 插件做；只 Minion → 启动器注入的 native 模块做（F4）；都没有 → 只能走
///     启动器侧的超域传送（需要退出客户端）。
/// </summary>
[Flags]
public enum InGameAgents
{
    None = 0,

    /// <summary>Dalamud 真的会注入（开关开着、没被本次启动跳过、且更新/兼容检查通过）</summary>
    Dalamud = 1 << 0,

    /// <summary>游戏起来后会挂 MinionLauncher</summary>
    Minion = 1 << 1
}
