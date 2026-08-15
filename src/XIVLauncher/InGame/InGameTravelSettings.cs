namespace XIVLauncher.InGame;

/// <summary>
///     游戏内换大区的行为设置。四项与 DcTraveler 的设置页一一对应（含默认值），
///     好让游戏内 UI 能原样抄它那套界面：
///     <list type="bullet">
///       <item>跨区失败时自动重试</item>
///       <item>目标繁忙时自动切换到同大区其他畅通服务器（若存在）</item>
///       <item>最大重试次数</item>
///       <item>重试间隔（秒）</item>
///     </list>
///     ⚠ 60 秒冷却不在此列 —— 那是服务侧的规矩, 不是偏好, 不给关。
/// </summary>
public sealed class InGameTravelSettings
{
    public bool EnableAutoRetry             { get; set; } = true;
    public bool AllowSwitchToAvailableWorld { get; set; } = true;
    public int  MaxRetryCount               { get; set; } = 20;
    public int  RetryDelaySeconds           { get; set; } = 60;

    /// <summary>全启动器共用一份 —— 冷却与重试都是账号侧的行为, 不按客户端分。</summary>
    public static InGameTravelSettings Current { get; } = new();

    public void Apply(InGameTravelSettings other)
    {
        EnableAutoRetry             = other.EnableAutoRetry;
        AllowSwitchToAvailableWorld = other.AllowSwitchToAvailableWorld;
        MaxRetryCount               = Math.Clamp(other.MaxRetryCount, 0, 100);
        RetryDelaySeconds           = Math.Clamp(other.RetryDelaySeconds, 5, 3600);
    }
}
