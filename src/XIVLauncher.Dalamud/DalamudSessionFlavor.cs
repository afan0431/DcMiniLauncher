namespace XIVLauncher.Dalamud;

/// <summary>
///     会话与注入器里随 Dalamud 版本不同的地方。缺省值 = 国服 Dalamud 的现有行为, 不传时注入器命令行与原来逐字相同;
///     国际服（goatcorp 原版 Dalamud）用 <see cref="Upstream" />。
/// </summary>
public sealed record DalamudSessionFlavor
{
    /// <summary>国服客户端语言在 Dalamud 里的编号</summary>
    public const int CLIENT_LANGUAGE_CN = 4;

    /// <summary>国服 Dalamud（缺省）</summary>
    public static DalamudSessionFlavor Default { get; } = new();

    /// <summary><c>--dalamud-client-language</c> 的值; 国服固定 4, 原版 Dalamud 是 0 日 / 1 英 / 2 德 / 3 法</summary>
    public int ClientLanguage { get; init; } = CLIENT_LANGUAGE_CN;

    /// <summary>是否传 <c>--launcher-directory</c>（国服 Dalamud 专有, 原版注入器的参数表里没有）</summary>
    public bool PassLauncherDirectory { get; init; } = true;

    /// <summary>是否传 <c>--managed-restart</c>（国服 Dalamud 专有: 崩溃处理器把重启决定编码进退出码, 由 RestartMonitor 接管）</summary>
    public bool ManagedRestart { get; init; } = true;

    /// <summary>
    ///     更新器就绪后再核对「这版 Dalamud 支持的游戏版本」与本地游戏版本: 返回 false 时 <see cref="DalamudSession.EnsureReady" />
    ///     给出 <see cref="DalamudSession.DalamudInstallState.OutOfDate" />; 返回 null（核对不了）抛异常; 为 null = 不核对（国服）
    /// </summary>
    public Func<DirectoryInfo, bool?>? IsGameVersionSupported { get; init; }

    /// <summary>
    ///     goatcorp 原版 Dalamud: 语言照客户端语言传, 不传两个国服专有参数, 并核对支持的游戏版本
    /// </summary>
    /// <param name="clientLanguage">0 日 / 1 英 / 2 德 / 3 法</param>
    /// <param name="isGameVersionSupported">核对支持的游戏版本</param>
    public static DalamudSessionFlavor Upstream(int clientLanguage, Func<DirectoryInfo, bool?>? isGameVersionSupported) =>
        new()
        {
            ClientLanguage         = clientLanguage,
            PassLauncherDirectory  = false,
            ManagedRestart         = false,
            IsGameVersionSupported = isGameVersionSupported
        };
}
