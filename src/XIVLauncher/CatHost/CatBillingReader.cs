using Serilog;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     向游戏内模块读选角界面的计费文字（<c>BILLING</c>）并经 <see cref="ICatLaunchReporter.Billing" /> 报告原文。
///     文字是界面 <c>_CharaSelectRemain</c> 里含「月卡」或「点卡」的那条, 例「月卡：24天18小时12分 点卡：2小时2分［122分钟］」;
///     模块不解析, 只交原文, 解析在 Cat 后端。界面文字可能比选角列表晚一点才填上, 所以读不到时由调用方隔一会儿再读几次。
///     模块太旧（不认识命令）时记一次日志, 之后不再读
/// </summary>
internal sealed class CatBillingReader(ICatLaunchReporter reporter, int processId, TimeSpan timeout)
{
    /// <summary>模块命令</summary>
    public const string COMMAND = "BILLING";

    private readonly List<string> misses = [];

    private bool unsupported;

    /// <summary>模块不认识 <c>BILLING</c>, 不再读</summary>
    public bool Unsupported => unsupported;

    /// <summary>这一轮没读到时模块答的界面文字（相邻相同的只留一条, 用「→」连起来）; 排查界面写法用</summary>
    public string Misses => string.Join(" → ", misses);

    /// <summary>开始新一轮读数（进一次选角界面算一轮）, 清掉上一轮记下的界面文字</summary>
    public void BeginRound() => misses.Clear();

    /// <summary>
    ///     读一次; 读到原文并报出去返回 <see cref="CatModuleRead.Answered" /> 且 Found 为真。
    ///     通话失败照常抛异常, 由调用方决定跳过; 调用方事后要放开管道
    /// </summary>
    public async Task<(CatModuleRead Read, bool Found)> ReadAsync(IMiniModuleChannel module, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (unsupported)
            return (CatModuleRead.Unsupported, false);

        if (await CatLevelReader.SendWithTimeoutAsync(module, COMMAND, timeout, cancellationToken).ConfigureAwait(false) is not { } response)
        {
            Log.Debug("[CatHost] 读计费文字 {Seconds} 秒没有回应, 跳过这一次", timeout.TotalSeconds);
            return (CatModuleRead.TimedOut, false);
        }

        if (response.Contains("unknown-command", StringComparison.Ordinal))
        {
            unsupported = true;
            Log.Information("[CatHost] 游戏 {Pid} 里的游戏内模块是旧版, 读不了计费文字（下次启动游戏才会换上新模块）", processId);
            return (CatModuleRead.Unsupported, false);
        }

        if (CatModuleReplies.ParseBilling(response) is not { } raw)
        {
            var seen = CatModuleReplies.DescribeBillingMiss(response);
            Log.Debug("[CatHost] 这一次没读到计费文字: {Response}", seen);

            if (misses.Count == 0 || misses[^1] != seen)
                misses.Add(seen);

            return (CatModuleRead.Answered, false);
        }

        reporter.Billing(processId, raw, at);
        return (CatModuleRead.Answered, true);
    }
}

internal static partial class CatModuleReplies
{
    /// <summary>
    ///     BILLING 的回应（<c>OK found=1 nodes=9</c> 后面每个含月卡/点卡的文字节点一行 <c>T \t 节点id \t 原文</c>）: 读到时返回各节点原文用空格连起来、去掉首尾空白的结果;
    ///     界面不在、没有这样的文字、回应不是 OK、或格式对不上返回 null
    /// </summary>
    public static string? ParseBilling(string response)
    {
        if (!IsOk(response))
            return null;

        var lines = response.Split('\n');

        if (lines.Length == 0 || !Flag(ParseFields(lines[0].TrimEnd('\r')), "found"))
            return null;

        var texts = new List<string>();

        foreach (var line in lines.Skip(1))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);

            if (parts.Length == 3 && parts[0] == "T" && parts[2].Trim() is { Length: > 0 } text)
                texts.Add(text);
        }

        return texts.Count == 0 ? null : string.Join(' ', texts);
    }

    /// <summary>
    ///     没读到计费时模块答了什么, 写成一行: 首行摘要, 后面是 found=0 时模块附上的各文字节点
    ///     （<c>D \t 节点id \t 字节数 \t 开头字节的十六进制 \t 原文</c>）, 例「OK found=0 nodes=5 | #3 0B | #4 6B E69C88E58DA1 「月卡」」
    /// </summary>
    public static string DescribeBillingMiss(string response)
    {
        var lines = response.Split('\n');
        var parts = new List<string> { lines[0].TrimEnd('\r') };

        foreach (var line in lines.Skip(1))
        {
            var fields = line.TrimEnd('\r').Split('\t', 5);

            if (fields.Length < 3 || fields[0] != "D")
                continue;

            var hex  = fields.Length > 3 && fields[3].Length > 0 ? $" {fields[3]}" : string.Empty;
            var text = fields.Length > 4 && fields[4].Trim() is { Length: > 0 } shown ? $" 「{shown}」" : string.Empty;
            parts.Add($"#{fields[1]} {fields[2]}B{hex}{text}");
        }

        return string.Join(" | ", parts);
    }
}
