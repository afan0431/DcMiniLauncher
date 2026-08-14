using System.Diagnostics;
using System.IO;
using Serilog;

namespace XIVLauncher.Minion;

/// <summary>
///     一次挂载的结果。<see cref="Error" /> 非 null = 压根没挂上（配置缺失 / 拉不起 launcher / launcher 卡死）。
///     ⚠ <see cref="Ok" /> 只代表 MinionLauncher 正常跑完退出, <b>不代表 bot 真的在跑</b> ——
///     MinionLauncher 会在 bot 起不来时照样自报 "Attaching Successfull"（见 research/probe-P1）。
///     唯一判据是游戏内 overlay / 新的 bot 日志。
/// </summary>
public sealed record MinionAttachResult(bool Ok, string? Error)
{
    public static MinionAttachResult Succeeded() => new(true, null);

    public static MinionAttachResult Failed(string error) => new(false, error);
}

/// <summary>
///     起完游戏后把 MinionLauncher 挂到游戏进程上（F3）。
///     参数模板取自 MINIONAPP 自己的命令行（research/investigation.md）, 并经 P1 实测：
///     MINIONAPP 全程关闭也能让 bot 真运行, 但 <c>-minionpass</c> 必须是明文
///     （见 research/probe-P1-attach-standalone.md）。
/// </summary>
public static class MinionAttacher
{
    /// <summary>国服 —— 本启动器只做国服, 故写死</summary>
    private const string REGION_CN = "2";

    /// <summary>Accounts.json 里国服账号的 ProductID 都是 8, 缺失时兜底用它</summary>
    private const string PRODUCT_ID_CN = "8";

    private const string DAT_NAME_CN = "FFXIVMinionCN_64.dat";

    private const string BOT_DIR_NAME = @"Bots\FFXIVMinion64";

    /// <summary>MinionLauncher 要能找到游戏窗口才肯 attach, 先等窗口出来再拉它</summary>
    private static readonly TimeSpan GAME_WINDOW_TIMEOUT = TimeSpan.FromMinutes(2);

    /// <summary>MinionLauncher attach 完会自己退出; 超时视为卡住</summary>
    private static readonly TimeSpan LAUNCHER_TIMEOUT = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     按启动页选中的分组/账号, 把 MinionLauncher 挂到 <paramref name="gameProcess" /> 上。
    ///     不抛异常, 失败信息在返回值里（挂不上不该连累已经起来的游戏）。
    /// </summary>
    public static async Task<MinionAttachResult> AttachAsync(Process gameProcess, CancellationToken cancellationToken = default)
    {
        var installPath = MinionAccounts.InstallPath;
        var launcherExe = MinionAccounts.GetLauncherExePath(installPath);

        if (!File.Exists(launcherExe))
            return MinionAttachResult.Failed($"未找到 {launcherExe}（在「设置 → Minion」里指定 Minion 安装目录）");

        MinionAccount? account;

        try
        {
            account = MinionAccounts.FindAccount(App.Settings.MinionGroup, App.Settings.MinionAccountUid, installPath);
        }
        catch (Exception ex)
        {
            return MinionAttachResult.Failed($"读取 {MinionAccounts.GetAccountsJsonPath(installPath)} 失败: {ex.Message}");
        }

        if (account == null)
            return MinionAttachResult.Failed($"Minion 分组 {App.Settings.MinionGroup ?? "(未选择)"} 下没有账号, 请在启动页重新选择分组");

        if (BuildArguments(account, installPath, gameProcess.Id) is not { } arguments)
            return MinionAttachResult.Failed(DescribeMissingFields(account));

        await WaitForGameWindowAsync(gameProcess, cancellationToken).ConfigureAwait(false);

        if (gameProcess.HasExited)
            return MinionAttachResult.Failed("游戏进程已退出, 没有可挂载的目标");

        Log.Information
        (
            "[Minion] 挂载 bot: PID={GamePid}, 分组={Group}, 账号={Account}, 命令行={CommandLine}",
            gameProcess.Id,
            account.Group,
            account.Label,
            Redact(launcherExe, arguments)
        );

        return await RunLauncherAsync(launcherExe, installPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     照抄 MINIONAPP 的命令行（research/investigation.md 抓到的真实参数）。
    ///     其中 region/attachtype/datpath/botpath 是常量, 其余按账号取；
    ///     <c>-account</c>/<c>-accountpass</c>/<c>-charname</c>/<c>-server</c> 不传 ——
    ///     游戏由本启动器起并已登录, Minion 只负责 attach。
    ///     缺必需字段时返回 null。
    /// </summary>
    private static List<string>? BuildArguments(MinionAccount account, string installPath, int gamePid)
    {
        if (string.IsNullOrWhiteSpace(account.Uid)             ||
            string.IsNullOrWhiteSpace(account.Keycode)         ||
            string.IsNullOrWhiteSpace(account.PathToExe)       ||
            string.IsNullOrWhiteSpace(App.Settings.MinionId)   ||
            string.IsNullOrWhiteSpace(App.Settings.MinionPassword))
            return null;

        var botPath = Path.Combine(installPath, BOT_DIR_NAME);

        return
        [
            $"-region={REGION_CN}",
            "-attachtype=0",
            $"-productid={account.ProductId?.ToString() ?? PRODUCT_ID_CN}",
            $"-uid={account.Uid}",
            $"-minionid={App.Settings.MinionId}",
            $"-minionkey={account.Keycode}",

            // 明文 —— 传 Accounts.json 里加密的 KeyPassword 会 "attach 成功" 但 bot 不起（P1 实测）
            $"-minionpass={App.Settings.MinionPassword}",
            "-attach=true",
            $"-attachtopid={gamePid}",

            // -path 必填, 缺了报 Invalid Game exe path; 给了 attachtopid 就不会拿它重开游戏
            $"-path={account.PathToExe}",
            $"-datpath={Path.Combine(botPath, "MinionFiles", DAT_NAME_CN)}",
            $"-botpath={botPath}",
            $"-usebeta={(account.UseBetaFiles ? "1" : "0")}",
            $"-datacenter={account.Datacenter ?? 0}"
        ];
    }

    private static string DescribeMissingFields(MinionAccount account)
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(account.Uid))
            missing.Add("账号 UID");
        if (string.IsNullOrWhiteSpace(account.Keycode))
            missing.Add("Keycode");
        if (string.IsNullOrWhiteSpace(account.PathToExe))
            missing.Add("PathToExe（Accounts.json 里该账号的游戏路径）");
        if (string.IsNullOrWhiteSpace(App.Settings.MinionId))
            missing.Add("Minion 账号（设置 → Minion）");
        if (string.IsNullOrWhiteSpace(App.Settings.MinionPassword))
            missing.Add("Minion 密码（设置 → Minion, 只能填明文）");

        return $"Minion 挂载所需配置不全: {string.Join("、", missing)}";
    }

    private static async Task<MinionAttachResult> RunLauncherAsync
    (
        string            launcherExe,
        string            workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launcherExe,

            // MinionLauncher 靠相对路径找 MinionLauncher_64.dat, 工作目录必须是安装目录（P1 实测）
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var launcher = new Process { StartInfo = startInfo };

        launcher.OutputDataReceived += (_, args) =>
        {
            if (args.Data != null)
                Log.Information("[Minion] launcher: {Line}", args.Data);
        };

        launcher.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
                Log.Warning("[Minion] launcher(stderr): {Line}", args.Data);
        };

        try
        {
            if (!launcher.Start())
                return MinionAttachResult.Failed($"无法启动 {launcherExe}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Minion] 启动 MinionLauncher 失败");
            return MinionAttachResult.Failed($"启动 {launcherExe} 失败: {ex.Message}");
        }

        launcher.BeginOutputReadLine();
        launcher.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LAUNCHER_TIMEOUT);

        try
        {
            await launcher.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(launcher);

            return cancellationToken.IsCancellationRequested
                       ? MinionAttachResult.Failed("挂载 Minion 已取消")
                       : MinionAttachResult.Failed($"MinionLauncher 超过 {LAUNCHER_TIMEOUT.TotalMinutes:0} 分钟没有退出, 已结束该进程");
        }

        // ⚠ 退出码不当判据: MinionLauncher 正常收尾时打的是 "Exiting Launcher, returning PID = <游戏PID>",
        //    退出码到底是 0 还是那个 PID 没有实测过, 拿它判成败会误报。
        Log.Information("[Minion] MinionLauncher 已退出 (ExitCode={ExitCode}); bot 是否真的在跑以游戏内 overlay / 新 bot 日志为准", launcher.ExitCode);

        return MinionAttachResult.Succeeded();
    }

    /// <summary>
    ///     MinionLauncher 找不到游戏窗口就不会 attach, 而游戏刚起来时窗口还没出来。
    ///     等不到也照样往下走（让 launcher 自己去找并报错), 只是先给它一个好时机。
    /// </summary>
    private static async Task WaitForGameWindowAsync(Process gameProcess, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < GAME_WINDOW_TIMEOUT)
        {
            gameProcess.Refresh();

            if (gameProcess.HasExited)
                return;

            if (gameProcess.MainWindowHandle != IntPtr.Zero)
            {
                Log.Information("[Minion] 游戏窗口已出现, 用时 {Elapsed:0.0}s", stopwatch.Elapsed.TotalSeconds);
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        Log.Warning("[Minion] 等了 {Timeout:0} 分钟仍未见游戏窗口, 仍然尝试挂载", GAME_WINDOW_TIMEOUT.TotalMinutes);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 结束 MinionLauncher 进程失败");
        }
    }

    /// <summary>
    ///     日志里不留 Minion 的密码与 Keycode
    /// </summary>
    private static string Redact(string launcherExe, IEnumerable<string> arguments) =>
        string.Join
        (
            ' ',
            arguments
                .Select
                (argument => argument.StartsWith("-minionpass=", StringComparison.Ordinal) || argument.StartsWith("-minionkey=", StringComparison.Ordinal)
                                 ? $"{argument[..argument.IndexOf('=')]}=***"
                                 : argument
                )
                .Prepend(launcherExe)
        );
}
