using XIVLauncher.Update;

namespace XIVLauncher.CatHost;

/// <summary>
///     <c>--cat-self-update</c> 的退出码: 外壳据此决定要不要等安装完成、要不要把在跑的守护进程换成新版（handoff → adopt）。
///     旧外壳只把退出码写日志, 所以 0 以外的取值不会被当成失败处理。
/// </summary>
public static class CatSelfUpdateExitCodes
{
    /// <summary>已是最新</summary>
    public const int UP_TO_DATE = 0;

    /// <summary>出错（检查、下载或安装失败）</summary>
    public const int FAILED = 1;

    /// <summary>新版本已下载, 本进程退出后由 Velopack 安装（外壳等安装目录里的版本变了再换守护进程）</summary>
    public const int APPLYING = 10;

    /// <summary>有从安装目录运行、正守着游戏的启动器（界面版或没走副本的无界面进程）, 没装</summary>
    public const int GUARDED = 11;

    /// <summary>安装目录下还有别的启动器开着, 没装</summary>
    public const int IN_USE = 12;

    /// <summary>本进程不在 Velopack 安装目录里（直接解压或本地编译）, 无从更新</summary>
    public const int NOT_INSTALLED = 13;

    /// <summary>
    ///     <see cref="UpdateOrchestrator.RunSilentAsync" /> 的结果代码 → 退出码; 不认识的按出错
    /// </summary>
    public static int From(string outcome) =>
        outcome switch
        {
            UpdateOrchestrator.SILENT_UP_TO_DATE    => UP_TO_DATE,
            UpdateOrchestrator.SILENT_APPLYING      => APPLYING,
            UpdateOrchestrator.SILENT_GUARDED       => GUARDED,
            UpdateOrchestrator.SILENT_IN_USE        => IN_USE,
            UpdateOrchestrator.SILENT_NOT_INSTALLED => NOT_INSTALLED,
            _                                       => FAILED
        };
}
