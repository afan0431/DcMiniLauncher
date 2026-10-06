namespace XIVLauncher.Dalamud;

/// <summary>
///     <see cref="DalamudSession" /> 用到的更新器成员。国服更新器 <see cref="DalamudUpdater" /> 原样实现它;
///     国际服（goatcorp 原版 Dalamud）另有一个更新器, 下载源和文件格式完全不同, 但准备好之后交给会话的东西一样。
/// </summary>
public interface IDalamudUpdater
{
    /// <summary>更新状态</summary>
    DalamudUpdater.DownloadState State { get; }

    /// <summary>更新失败时的最后一个异常</summary>
    Exception? EnsurementException { get; }

    /// <summary>注入器 Dalamud.Injector.exe; 还没准备好时为 null</summary>
    FileInfo? Runner { get; }

    /// <summary>.NET 运行时目录</summary>
    DirectoryInfo Runtime { get; }

    /// <summary>资源目录; 还没准备好时为 null</summary>
    DirectoryInfo? AssetDirectory { get; }

    /// <summary>等本次更新结束</summary>
    void WaitForCompletion();

    /// <summary>显示「正在更新」</summary>
    void ShowLoading();

    /// <summary>收起「正在更新」</summary>
    void HideLoading();
}
