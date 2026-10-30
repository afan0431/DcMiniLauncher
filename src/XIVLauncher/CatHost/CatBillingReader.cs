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

    private bool unsupported;

    /// <summary>模块不认识 <c>BILLING</c>, 不再读</summary>
    public bool Unsupported => unsupported;

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
            Log.Debug("[CatHost] 这一次没读到计费文字: {Response}", CatModuleReplies.FirstLine(response));
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
}
