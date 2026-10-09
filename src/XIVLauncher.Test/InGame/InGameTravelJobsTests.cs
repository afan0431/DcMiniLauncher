using XIVLauncher.InGame;
using Xunit;

namespace XIVLauncher.Test.InGame;

/// <summary>
///     换大区与交接停止互斥: 交接定下后不再开始新的换大区; 已有一次在进行时交接等它结束
/// </summary>
public sealed class InGameTravelJobsTests
{
    // 状态是进程内静态的, 每个用例用自己的进程号
    private static int NextPid() => Random.Shared.Next(2_000_000, 3_000_000);

    [Fact]
    public void Held_RefusesNewTravel_UntilReleased()
    {
        var pid = NextPid();

        Assert.True(InGameTravelJobs.TryHold(pid));
        Assert.Null(InGameTravelJobs.TryBegin(pid, "陆行鸟"));
        Assert.False(InGameTravelJobs.IsRunning(pid));

        InGameTravelJobs.Release(pid);
        Assert.NotNull(InGameTravelJobs.TryBegin(pid, "陆行鸟"));
        Assert.True(InGameTravelJobs.IsRunning(pid));
        InGameTravelJobs.End(pid, true, "完成");
    }

    [Fact]
    public void RunningTravel_BlocksHold_AndASecondTravel()
    {
        var pid = NextPid();

        Assert.NotNull(InGameTravelJobs.TryBegin(pid, "陆行鸟"));
        Assert.Null(InGameTravelJobs.TryBegin(pid, "猫小胖"));
        Assert.False(InGameTravelJobs.TryHold(pid));

        InGameTravelJobs.End(pid, false, "失败");
        Assert.True(InGameTravelJobs.TryHold(pid));
        InGameTravelJobs.Release(pid);
    }
}
