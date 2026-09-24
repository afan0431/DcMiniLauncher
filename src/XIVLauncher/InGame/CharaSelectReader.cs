using System.Globalization;
using Serilog;

namespace XIVLauncher.InGame;

/// <summary>
///     读游戏选角界面的角色列表（原生模块的 <c>WHOLIST</c>）。
///     <para>
///         WHY: 标题/选角界面 Lua 的 <c>Player</c> 无效, 报不上角色名和世界 —— 游戏内 UI 在那两个界面
///         就查不了 /areas（2026-09-25 实测: 「正在读取角色… / 列表读取失败」）。选角列表在游戏内存里是现成的。
///     </para>
///     <para>
///         ⚠ 条目里的世界名是游戏<b>内部代号</b>（如 <c>HongChaChuan2</c>）, 不是中文名。它恰好等于 SDO 服务器列表的
///         <c>groupCode</c>（大小写不一, 如 <c>Longchaoshendian</c>）, 所以按 groupCode 忽略大小写对应。
///     </para>
/// </summary>
public static class CharaSelectReader
{
    private const int PIPE_CONNECT_TIMEOUT_MS = 3000;

    /// <summary>LoginFlags: DCTraveling / Unk32 —— DCTraveler 把这两个都当「超域中」</summary>
    private const byte LOGIN_FLAG_DC_TRAVELING = 16;
    private const byte LOGIN_FLAG_UNK32        = 32;

    public sealed record Entry
    (
        string ContentId,
        int    Index,
        byte   LoginFlags,
        int    CurrentWorldId,
        int    HomeWorldId,
        string Name,
        string CurrentWorldCode,
        string HomeWorldCode
    )
    {
        public bool DcTraveling => LoginFlags is LOGIN_FLAG_DC_TRAVELING or LOGIN_FLAG_UNK32;
    }

    public sealed record Snapshot(string Where, string SelectedContentId, IReadOnlyList<Entry> Entries)
    {
        /// <summary>
        ///     挑一个角色: 指定的 contentId &gt; 指定的名字 &gt; 当前选中的那个 &gt; 列表里只有一个。
        ///     选角界面右键的那个角色此时已是「选中」, 所以右键菜单即使没带 contentId 也能对上。
        /// </summary>
        public Entry? Pick(string? contentId, string? name)
        {
            if (!string.IsNullOrEmpty(contentId))
                return Entries.FirstOrDefault(x => x.ContentId == contentId);

            if (!string.IsNullOrEmpty(name))
                return Entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

            return Entries.FirstOrDefault(x => x.ContentId == SelectedContentId)
                   ?? (Entries.Count == 1 ? Entries[0] : null);
        }
    }

    /// <summary>
    ///     读一次。返回 (快照, 错误)。只要能连上模块就给快照 —— 在哪个界面由调用方判断,
    ///     因为「标题界面的列表是上一个大区的旧表」这种事只有调用方知道该怎么处理。
    /// </summary>
    public static async Task<(Snapshot? Snapshot, string? Error)> ReadAsync(int? pid, CancellationToken cancellationToken)
    {
        var game = RunningGameRegistry.Resolve(pid, out var resolveError);

        if (game == null)
            return (null, resolveError ?? "找不到目标客户端");

        // 模块的管道只允许一个连接, 超域旅行进行中那条连接会一直占着 —— 别去抢, 抢了会卡到超时
        if (InGameTravelJobs.IsRunning(game.Process.Id))
            return (null, "这个客户端正在跨区中");

        var injectError = MiniModuleInjector.Inject(game.Process);

        if (injectError != null)
            return (null, $"注入游戏内模块失败: {injectError}");

        try
        {
            using var module = new MiniModuleClient(game.Process.Id);
            await module.ConnectAsync(PIPE_CONNECT_TIMEOUT_MS, cancellationToken).ConfigureAwait(false);

            var response = await module.SendAsync("WHOLIST", cancellationToken).ConfigureAwait(false);
            var snapshot = Parse(response);

            if (snapshot == null)
            {
                // 旧版模块不认识这条命令（FAIL unknown-command）—— 游戏里注着的是升级前的那份 DLL
                Log.Warning("[CharaSelect] WHOLIST 回应无法解析: {Response}", FirstLine(response));
                return (null, response.Contains("unknown-command", StringComparison.Ordinal)
                                  ? "游戏里的模块是旧版, 重启游戏后再试"
                                  : "读不到选角列表");
            }

            return (snapshot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[CharaSelect] 读选角列表失败 PID={Pid}", game.Process.Id);
            return (null, "连不上游戏内模块");
        }
    }

    // 第一行: OK where=charaselect n=3 total=3 selected=<cid> selectedIndex=0 hovered=<cid> hoveredIndex=-1
    // 之后:   C \t cid \t index \t loginFlags \t curWorldId \t homeWorldId \t 名字 \t 当前世界代号 \t 原始世界代号
    internal static Snapshot? Parse(string response)
    {
        var lines = response.Split('\n');

        if (lines.Length == 0 || !lines[0].StartsWith("OK ", StringComparison.Ordinal))
            return null;

        var header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Select(x => x.Split('=', 2))
                             .Where(x => x.Length == 2)
                             .ToDictionary(x => x[0], x => x[1]);

        var entries = new List<Entry>();

        foreach (var line in lines.Skip(1))
        {
            var f = line.TrimEnd('\r').Split('\t');

            if (f.Length < 9 || f[0] != "C")
                continue;

            entries.Add(new Entry
            (
                f[1],
                ParseInt(f[2]),
                (byte)ParseInt(f[3]),
                ParseInt(f[4]),
                ParseInt(f[5]),
                f[6],
                f[7],
                f[8]
            ));
        }

        return new Snapshot(header.GetValueOrDefault("where", "unknown"),
                            header.GetValueOrDefault("selected", "0"),
                            entries);
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static string FirstLine(string value)
    {
        var newline = value.IndexOf('\n');
        return newline < 0 ? value : value[..newline];
    }
}
