using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Dalamud;

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

    /// <summary>
    ///     beta 的 bot dat 在 <c>MinionFiles\Beta\</c> 下单独一份。
    ///     ⚠ 决定 bot 跑不跑 beta 的是 <c>-datpath</c> 指向哪个 dat, 不是 <c>-usebeta</c> ——
    ///     只给 usebeta=1 而 datpath 还是普通 dat, bot 会照常加载非 beta 的 LuaMods（2026-08-14 实测）。
    /// </summary>
    private const string BETA_DAT_NAME_CN = "FFXIVMinionCN_64_BETA.dat";

    private const string BOT_DIR_NAME = @"Bots\FFXIVMinion64";

    private const string MINION_FILES_DIR_NAME = "MinionFiles";

    private const string BETA_DIR_NAME = "Beta";

    /// <summary>MinionLauncher 要能找到游戏窗口才肯 attach, 先等窗口出来再拉它</summary>
    private static readonly TimeSpan GAME_WINDOW_TIMEOUT = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     两个都开时 Minion 至少比 Dalamud 的注入延迟晚这么多 —— Dalamud 随进程创建注入(entrypoint),
    ///     Minion 只能事后 attach, 撞在一起容易出事
    /// </summary>
    private const int MIN_DELAY_AFTER_DALAMUD_MS = 3000;

    /// <summary>等 Dalamud.dll 出现在游戏进程里的上限</summary>
    private static readonly TimeSpan DALAMUD_WAIT_TIMEOUT = TimeSpan.FromMinutes(1);

    /// <summary>MinionLauncher attach 完会自己退出; 超时视为卡住</summary>
    private static readonly TimeSpan LAUNCHER_TIMEOUT = TimeSpan.FromMinutes(3);

    /// <summary>等 MINIONAPP 把它那一行的 PID 刷成我们的游戏进程的上限</summary>
    private static readonly TimeSpan MINION_APP_INJECT_TIMEOUT = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     按启动页选中的分组/账号, 把 MinionLauncher 挂到 <paramref name="gameProcess" /> 上。
    ///     不抛异常, 失败信息在返回值里（挂不上不该连累已经起来的游戏）。
    /// </summary>
    /// <param name="gamePath">启动器本次用的游戏目录, 用来兜底 <c>-path</c>（Accounts.json 里的路径常常是旧机器的）</param>
    /// <param name="dalamudInjected">本次是否真的注了 Dalamud —— 是的话要等它先落地再挂 Minion</param>
    public static async Task<MinionAttachResult> AttachAsync
    (
        Process           gameProcess,
        DirectoryInfo?    gamePath,
        bool              dalamudInjected,
        CancellationToken cancellationToken = default
    )
    {
        var installPath = MinionAccounts.InstallPath;

        MinionAccount? account;
        int            rowIndex;

        try
        {
            (account, rowIndex) = MinionAccounts.FindAccountInGroup(App.Settings.MinionGroup, App.Settings.MinionAccountUid, installPath);
        }
        catch (Exception ex)
        {
            return MinionAttachResult.Failed($"读取 {MinionAccounts.GetAccountsJsonPath(installPath)} 失败: {ex.Message}");
        }

        if (account == null)
            return MinionAttachResult.Failed($"Minion 分组 {App.Settings.MinionGroup ?? "(未选择)"} 下没有账号, 请在启动页重新选择分组");

        await WaitForGameWindowAsync(gameProcess, cancellationToken).ConfigureAwait(false);

        if (dalamudInjected)
            await WaitForDalamudAsync(gameProcess, cancellationToken).ConfigureAwait(false);

        var delay = ResolveAttachDelay(dalamudInjected);

        if (delay > TimeSpan.Zero)
        {
            Log.Information("[Minion] 等 {Delay:0.0}s 再挂载（Dalamud 本次{DalamudState}）", delay.TotalSeconds, dalamudInjected ? "已注入" : "未注入");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return MinionAttachResult.Failed("挂载 Minion 已取消");
            }
        }

        if (gameProcess.HasExited)
            return MinionAttachResult.Failed("游戏进程已退出, 没有可挂载的目标");

        // MINIONAPP 开着时必须由它发起注入, 否则它认不下这个会话, 十几秒后会把客户端杀掉
        // （见 MinionAppAutomation 的说明）
        if (MinionAppAutomation.IsRunning())
            return InjectViaMinionApp(account, rowIndex, gameProcess.Id, cancellationToken);

        return await SpawnLauncherAsync(account, installPath, gamePath, gameProcess, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     MINIONAPP 在跑 —— 点它自己那一行的「注入」, 让会话归它所有
    /// </summary>
    private static MinionAttachResult InjectViaMinionApp(MinionAccount account, int rowIndex, int gamePid, CancellationToken cancellationToken)
    {
        Log.Information
        (
            "[Minion] 检测到 MINIONAPP 正在运行, 改由它发起注入: 分组={Group}, 组内第 {Row} 行, 账号={Account}, beta={UseBeta}",
            account.Group,
            rowIndex + 1,
            account.Label,
            account.UseBetaFiles
        );

        MinionAppInjectResult result;

        try
        {
            result = MinionAppAutomation.Inject
            (
                account.Group,
                rowIndex,
                account.UseBetaFiles,
                account.Keycode,
                gamePid,
                MINION_APP_INJECT_TIMEOUT,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Minion] 驱动 MINIONAPP 注入时出错");
            return MinionAttachResult.Failed($"驱动 MINIONAPP 注入失败: {ex.Message}");
        }

        if (!result.Ok)
            return MinionAttachResult.Failed
            (
                $"""
                 {result.Error}

                 MINIONAPP 正在运行时, 必须由它发起注入 —— 我们自己挂的会话它认不下来, 会在十几秒后杀掉游戏。
                 可以在 MINIONAPP 里手动点该账号的「注入」, 或退出 MINIONAPP 后由启动器自己挂。
                 """
            );

        Log.Information("[Minion] MINIONAPP 已接管本次注入; bot 是否真的在跑以游戏内 overlay / 新 bot 日志为准");
        return MinionAttachResult.Succeeded();
    }

    /// <summary>
    ///     MINIONAPP 没开 —— 自己拉 MinionLauncher（probe-P1 验证过的路径）
    /// </summary>
    private static async Task<MinionAttachResult> SpawnLauncherAsync
    (
        MinionAccount     account,
        string            installPath,
        DirectoryInfo?    gamePath,
        Process           gameProcess,
        CancellationToken cancellationToken
    )
    {
        var launcherExe = MinionAccounts.GetLauncherExePath(installPath);

        if (!File.Exists(launcherExe))
            return MinionAttachResult.Failed($"未找到 {launcherExe}（在「设置 → Minion」里指定 Minion 安装目录）");

        if (ResolveGameExePath(account, gamePath, gameProcess) is not { } gameExePath)
            return MinionAttachResult.Failed
            (
                $"找不到可用的游戏 exe 给 -path 用（Accounts.json 里写的是 {account.PathToExe ?? "(空)"}, 本机不存在; " +
                $"启动器的游戏目录 {gamePath?.FullName ?? "(未配置)"} 下也没找到）"
            );

        var botPath = Path.Combine(installPath, BOT_DIR_NAME);
        var datPath = GetDatPath(botPath, account.UseBetaFiles);

        if (!File.Exists(datPath))
            return MinionAttachResult.Failed
            (
                account.UseBetaFiles
                    ? $"该账号勾了 beta, 但找不到 beta 的 bot 文件 {datPath}（用 MINIONAPP 起一次 beta 账号让它下载）"
                    : $"找不到 bot 文件 {datPath}"
            );

        if (BuildArguments(account, botPath, datPath, gameExePath, gameProcess.Id) is not { } arguments)
            return MinionAttachResult.Failed(DescribeMissingFields(account));

        Log.Information
        (
            "[Minion] 挂载 bot: PID={GamePid}, 分组={Group}, 账号={Account}, beta={UseBeta}, 命令行={CommandLine}",
            gameProcess.Id,
            account.Group,
            account.Label,
            account.UseBetaFiles,
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
    private static string GetDatPath(string botPath, bool useBeta) =>
        useBeta
            ? Path.Combine(botPath, MINION_FILES_DIR_NAME, BETA_DIR_NAME, BETA_DAT_NAME_CN)
            : Path.Combine(botPath, MINION_FILES_DIR_NAME, DAT_NAME_CN);

    private static List<string>? BuildArguments(MinionAccount account, string botPath, string datPath, string gameExePath, int gamePid)
    {
        if (string.IsNullOrWhiteSpace(account.Uid)             ||
            string.IsNullOrWhiteSpace(account.Keycode)         ||
            string.IsNullOrWhiteSpace(App.Settings.MinionId)   ||
            string.IsNullOrWhiteSpace(App.Settings.MinionPassword))
            return null;

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

            // -path 必填且**文件必须真的存在**, 否则 launcher 报 Invalid Game exe path 直接退出;
            // 给了 attachtopid 就不会拿它重开游戏, 只是校验
            $"-path={gameExePath}",
            // beta 账号要指到 MinionFiles\Beta\ 下那份 dat, 只给 usebeta=1 是不够的
            $"-datpath={datPath}",
            $"-botpath={botPath}",
            $"-usebeta={(account.UseBetaFiles ? "1" : "0")}",
            $"-datacenter={account.Datacenter ?? 0}",
            $"-streamermode={(account.StreamerMode ? "1" : "0")}",
            $"-setwindowtitle={(account.SetWindowTitle ? "1" : "0")}"
        ];
    }

    /// <summary>
    ///     挑一个**真实存在**的 exe 给 <c>-path</c>。
    ///     不能只信 Accounts.json 的 <c>PathToExe</c>: 那是 MINIONAPP 当初配的, 换机器/换盘后就成了死路径,
    ///     MinionLauncher 校验不过会直接 "ERROR: Invalid Game exe path!" 退出, 根本不会 attach（2026-08-14 实测）。
    ///     优先本次启动用的游戏目录下的官方登录器（与 MINIONAPP 的配法一致）, 再退回账号里的路径, 最后用游戏进程自己的 exe。
    /// </summary>
    private static string? ResolveGameExePath(MinionAccount account, DirectoryInfo? gamePath, Process gameProcess)
    {
        List<string?> candidates =
        [
            gamePath == null ? null : Path.Combine(gamePath.FullName, "sdo", "sdologin", "Launcher.exe"),
            account.PathToExe,
            gamePath == null ? null : Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe"),
            TryGetProcessExePath(gameProcess)
        ];

        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

    private static string? TryGetProcessExePath(Process gameProcess)
    {
        try
        {
            return gameProcess.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 读取游戏进程的 exe 路径失败");
            return null;
        }
    }

    private static string DescribeMissingFields(MinionAccount account)
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(account.Uid))
            missing.Add("账号 UID");
        if (string.IsNullOrWhiteSpace(account.Keycode))
            missing.Add("Keycode");
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

        // MinionLauncher 出错时是打一行 "ERROR: …" 然后正常退出, 不认这行就会把失败当成功
        var errorLines = new ConcurrentQueue<string>();

        launcher.OutputDataReceived += (_, args) =>
        {
            if (args.Data == null)
                return;

            Log.Information("[Minion] launcher: {Line}", args.Data);

            if (args.Data.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                errorLines.Enqueue(args.Data.Trim());
        };

        launcher.ErrorDataReceived += (_, args) =>
        {
            if (args.Data == null)
                return;

            Log.Warning("[Minion] launcher(stderr): {Line}", args.Data);
            errorLines.Enqueue(args.Data.Trim());
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

        var exitCode = launcher.ExitCode;
        Log.Information("[Minion] MinionLauncher 已退出 (ExitCode={ExitCode})", exitCode);

        if (!errorLines.IsEmpty)
            return MinionAttachResult.Failed($"MinionLauncher 报错: {string.Join(" / ", errorLines)}");

        // 正常收尾打的是 "Exiting Launcher, returning PID = <游戏PID>" —— 退出码可能就是那个 PID,
        // 所以只有负数才判失败（实测参数错误时是 -106）, 正数不当失败看。
        if (exitCode < 0)
            return MinionAttachResult.Failed($"MinionLauncher 异常退出 (ExitCode={exitCode}), 详见日志");

        Log.Information("[Minion] launcher 侧没有报错; bot 是否真的在跑以游戏内 overlay / 新 bot 日志为准");

        return MinionAttachResult.Succeeded();
    }

    /// <summary>
    ///     挂载等多久 —— 与 Dalamud 用同一套「毫秒延迟」的配法。
    ///     两个都开时强制排在 Dalamud 之后: 取「设置里的挂载延迟」和「Dalamud 注入延迟 + 间隔」的较大者。
    /// </summary>
    private static TimeSpan ResolveAttachDelay(bool dalamudInjected)
    {
        var configured = (int)Math.Clamp(App.Settings.MinionAttachDelayMS, 0, 120_000);

        if (!dalamudInjected)
            return TimeSpan.FromMilliseconds(configured);

        var dalamudDelay = (int)Math.Clamp(App.Settings.DalamudInjectionDelayMS, 0, DalamudLaunchOptions.MAX_DELAY_INITIALIZE_MS);
        var afterDalamud = dalamudDelay + MIN_DELAY_AFTER_DALAMUD_MS;

        if (afterDalamud > configured)
            Log.Information("[Minion] 挂载延迟按 Dalamud 顺延: {Configured}ms → {Effective}ms", configured, afterDalamud);

        return TimeSpan.FromMilliseconds(Math.Max(configured, afterDalamud));
    }

    /// <summary>
    ///     等 Dalamud.dll 真的出现在游戏进程里 —— 「Minion 要比 Dalamud 晚」靠这个信号保证, 而不是靠掐表。
    ///     等不到也照常往下走（Dalamud 自己会报错, 不该因此不挂 Minion）。
    /// </summary>
    private static async Task WaitForDalamudAsync(Process gameProcess, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < DALAMUD_WAIT_TIMEOUT)
        {
            if (gameProcess.HasExited)
                return;

            if (IsDalamudLoaded(gameProcess))
            {
                Log.Information("[Minion] 已检测到 Dalamud 注入完成, 用时 {Elapsed:0.0}s", stopwatch.Elapsed.TotalSeconds);
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

        Log.Warning("[Minion] 等了 {Timeout:0} 分钟没见到 Dalamud.dll, 仍然按计划挂载", DALAMUD_WAIT_TIMEOUT.TotalMinutes);
    }

    private static bool IsDalamudLoaded(Process gameProcess)
    {
        try
        {
            return FFXIVProcess.IsDalamudInjected(gameProcess);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Minion] 检查 Dalamud 是否注入失败");
            return false;
        }
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
