using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.CatHost;
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
///     起完游戏后把 MinionLauncher 挂到游戏进程上（F3）。卡号、编号、论坛账号都由工作台随 launch 下发, 挂载选项是固定值。
///     参数模板取自 MINIONAPP 自己的命令行（research/investigation.md）, 并经 P1 实测：
///     不开 MINIONAPP 也能让 bot 真运行, 但 <c>-minionpass</c> 必须是明文
///     （见 research/probe-P1-attach-standalone.md）。
/// </summary>
public static class MinionAttacher
{
    /// <summary>Minion 侧的区域设置, 国服与国际服都是 2</summary>
    private const string REGION = "2";

    /// <summary>产品编号, 国服与国际服都是 8</summary>
    private const string PRODUCT_ID = "8";

    /// <summary>大区, 固定 0</summary>
    private const string DATACENTER = "0";

    /// <summary>国服 bot 注入文件, 位于 bot 目录的 <c>MinionFiles\</c> 下</summary>
    private const string DAT_NAME_CN = "FFXIVMinionCN_64.dat";

    /// <summary>
    ///     国际服 bot 注入文件, 同在 <c>MinionFiles\</c> 下。
    ///     取证: PC3（Minion 装在 D:\MINI, 由 MINIONAPP 调 MinionLauncher_64）的 MinionLauncherInfo.txt, 2026-10-05、10-06 两次对国际服游戏
    ///     挂载成功（Attaching Successfull）时解析到的参数（2026-10-06 取证）。同一份记录里国际服与国服只有两处不同:
    ///     <c>datpath</c> 的文件名（这个常量）和 <c>path</c> 指向的游戏 exe（国际服是游戏目录下的 game\ffxiv_dx11.exe）;
    ///     region 两边都是 2（Minion 侧的全局设置, 不随客户端变）, attachtype 0、productid 8、usebeta 0、datacenter 0 也都相同。
    /// </summary>
    private const string DAT_NAME_GLOBAL = "FFXIVMinion_64.dat";

    private const string BOT_DIR_NAME = @"Bots\FFXIVMinion64";

    private const string MINION_FILES_DIR_NAME = "MinionFiles";

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

    /// <summary>
    ///     把 <paramref name="minion" /> 这张卡挂到 <paramref name="gameProcess" /> 上, 成功后写占用记录。
    ///     不抛异常, 失败信息在返回值里（挂不上不该连累已经起来的游戏）。
    /// </summary>
    /// <param name="minion">要挂的卡; variant 决定注入文件名和 <c>-path</c> 的候选, 其余参数两种相同</param>
    /// <param name="gameProcess">游戏进程</param>
    /// <param name="gamePath">这次用的游戏目录, 用来找 <c>-path</c></param>
    /// <param name="dalamudInjected">本次是否真的注了 Dalamud —— 是的话要等它先落地再挂 Minion</param>
    /// <param name="accountName">游戏账号名, 只写进占用记录</param>
    /// <param name="cancellationToken">取消</param>
    public static async Task<MinionAttachResult> AttachAsync
    (
        CatMinionLaunch   minion,
        Process           gameProcess,
        DirectoryInfo?    gamePath,
        bool              dalamudInjected,
        string?           accountName,
        CancellationToken cancellationToken = default
    )
    {
        var installPath = MinionInstall.InstallPath;

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

        var result = await SpawnLauncherAsync(minion, installPath, gamePath, gameProcess, cancellationToken).ConfigureAwait(false);

        if (result.Ok)
            WriteOccupancy(minion, gameProcess, accountName);

        return result;
    }

    /// <summary>
    ///     自己拉 MinionLauncher（probe-P1 验证过的路径）
    /// </summary>
    private static async Task<MinionAttachResult> SpawnLauncherAsync
    (
        CatMinionLaunch   minion,
        string            installPath,
        DirectoryInfo?    gamePath,
        Process           gameProcess,
        CancellationToken cancellationToken
    )
    {
        var launcherExe = MinionInstall.GetLauncherExePath(installPath);

        if (!File.Exists(launcherExe))
            return MinionAttachResult.Failed($"未找到 {launcherExe}（在「设置 → Minion」里指定 Minion 安装目录）");

        if (ResolveGameExePath(gamePath, gameProcess, minion.Variant) is not { } gameExePath)
            return MinionAttachResult.Failed($"找不到可用的游戏 exe 给 -path 用（游戏目录 {gamePath?.FullName ?? "(未配置)"} 下没找到, 也读不到游戏进程的 exe）");

        var botPath = Path.Combine(installPath, BOT_DIR_NAME);
        var datPath = Path.Combine(botPath, MINION_FILES_DIR_NAME, DatNameOf(minion.Variant));

        if (!File.Exists(datPath))
            return MinionAttachResult.Failed($"找不到 bot 文件 {datPath}");

        if (BuildArguments(minion, botPath, datPath, gameExePath, gameProcess.Id) is not { } arguments)
            return MinionAttachResult.Failed("Minion 挂载所需配置不全: 卡号、编号、论坛账号或论坛密码为空");

        Log.Information
        (
            "[Minion] 挂载 bot: PID={GamePid}, 卡={Fingerprint}, 客户端={Variant}, 命令行={CommandLine}",
            gameProcess.Id,
            minion.Fingerprint,
            minion.Variant,
            Redact(launcherExe, arguments)
        );

        return await RunLauncherAsync(launcherExe, installPath, arguments, RedactorFor(minion), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     照抄 MINIONAPP 的命令行（research/investigation.md 抓到的真实参数）。
    ///     卡号、编号、论坛账号与密码取自 <paramref name="minion" />, 其余是固定值:
    ///     productid 8、datacenter 0、主播模式开、命名窗口标题关、Beta 关。
    ///     <c>-account</c>/<c>-accountpass</c>/<c>-charname</c>/<c>-server</c> 不传 —— 游戏由本启动器起并已登录, Minion 只负责 attach。
    ///     国服与国际服拼法相同, 区别只在调用方传进来的 <paramref name="datPath" /> 与 <paramref name="gameExePath" />。
    ///     卡号、编号、论坛账号或密码为空时返回 null。
    /// </summary>
    internal static List<string>? BuildArguments(CatMinionLaunch minion, string botPath, string datPath, string gameExePath, int gamePid)
    {
        var keycode       = minion.Keycode.Reveal();
        var forumPassword = minion.ForumPassword.Reveal();

        if (string.IsNullOrWhiteSpace(minion.Uid)     ||
            string.IsNullOrWhiteSpace(keycode)        ||
            string.IsNullOrWhiteSpace(minion.ForumId) ||
            string.IsNullOrEmpty(forumPassword))
            return null;

        return
        [
            $"-region={REGION}",
            "-attachtype=0",
            $"-productid={PRODUCT_ID}",
            $"-uid={minion.Uid}",
            $"-minionid={minion.ForumId}",
            $"-minionkey={keycode}",

            // 明文 —— 传加密后的密码会 "attach 成功" 但 bot 不起（P1 实测）
            $"-minionpass={forumPassword}",
            "-attach=true",
            $"-attachtopid={gamePid}",

            // -path 必填且**文件必须真的存在**, 否则 launcher 报 Invalid Game exe path 直接退出;
            // 给了 attachtopid 就不会拿它重开游戏, 只是校验
            $"-path={gameExePath}",
            $"-datpath={datPath}",
            $"-botpath={botPath}",
            "-usebeta=0",
            $"-datacenter={DATACENTER}",
            "-streamermode=1",
            "-setwindowtitle=0"
        ];
    }

    /// <summary>
    ///     挑一个**真实存在**的 exe 给 <c>-path</c>: MinionLauncher 校验不过会直接 "ERROR: Invalid Game exe path!" 退出, 根本不会 attach（2026-08-14 实测）
    /// </summary>
    private static string? ResolveGameExePath(DirectoryInfo? gamePath, Process gameProcess, string variant) =>
        GameExeCandidates(gamePath, TryGetProcessExePath(gameProcess), variant)
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));

    /// <summary>
    ///     <c>-path</c> 的候选, 按先后取第一个真实存在的。
    ///     国服: 游戏目录下的盛趣登录器（与 MINIONAPP 的配法一致）→ 游戏目录下的 game\ffxiv_dx11.exe → 游戏进程自己的 exe。
    ///     国际服（取证见 <see cref="DAT_NAME_GLOBAL" />）: 国际服游戏目录下的 game\ffxiv_dx11.exe → 游戏进程自己的 exe; 国际服没有盛趣登录器, 不找它。
    /// </summary>
    internal static List<string?> GameExeCandidates(DirectoryInfo? gamePath, string? processExePath, string variant)
    {
        var gameExe = gamePath == null ? null : Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");

        if (variant == MinionCards.VARIANT_GLOBAL)
            return [gameExe, processExePath];

        return
        [
            gamePath == null ? null : Path.Combine(gamePath.FullName, "sdo", "sdologin", "Launcher.exe"),
            gameExe,
            processExePath
        ];
    }

    /// <summary>
    ///     variant 对应的 bot 注入文件名: 国际服 <see cref="DAT_NAME_GLOBAL" />, 其余（国服）<see cref="DAT_NAME_CN" />
    /// </summary>
    internal static string DatNameOf(string variant) =>
        variant == MinionCards.VARIANT_GLOBAL ? DAT_NAME_GLOBAL : DAT_NAME_CN;

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

    private static void WriteOccupancy(CatMinionLaunch minion, Process gameProcess, string? accountName)
    {
        try
        {
            MinionOccupancy.WriteAndDeleteOnExit
            (
                new MinionOccupancyRecord
                {
                    Pid              = gameProcess.Id,
                    ProcessStartedAt = MinionOccupancy.GetProcessStartedAt(gameProcess),
                    CardFingerprint  = minion.Fingerprint,
                    Variant          = minion.Variant,
                    AccountName      = accountName,
                    MinionUid        = minion.Uid,
                    AttachedAt       = DateTimeOffset.UtcNow
                },
                gameProcess
            );
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Minion] 生成占用记录失败 PID={Pid}", gameProcess.Id);
        }
    }

    private static async Task<MinionAttachResult> RunLauncherAsync
    (
        string            launcherExe,
        string            workingDirectory,
        IEnumerable<string> arguments,
        CatLogRedactor    redactor,
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
            if (args.Data != null)
                OnLauncherOutput(args.Data, redactor, errorLines);
        };

        launcher.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
                OnLauncherError(args.Data, redactor, errorLines);
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
    ///     遮 MinionLauncher 输出用的: 登记本次的卡号与论坛密码（launcher 可能把命令行原样打出来）
    /// </summary>
    internal static CatLogRedactor RedactorFor(CatMinionLaunch minion)
    {
        var redactor = new CatLogRedactor();
        redactor.RegisterSecret(minion.Keycode.Reveal());
        redactor.RegisterSecret(minion.ForumPassword.Reveal());

        return redactor;
    }

    /// <summary>
    ///     MinionLauncher 标准输出的一行: 遮住卡号与论坛密码后写日志; 含 ERROR 的记为报错
    /// </summary>
    internal static void OnLauncherOutput(string line, CatLogRedactor redactor, ConcurrentQueue<string> errorLines)
    {
        var safe = redactor.Redact(line);
        Log.Information("[Minion] launcher: {Line}", safe);

        if (safe.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            errorLines.Enqueue(safe.Trim());
    }

    /// <summary>
    ///     MinionLauncher 标准错误的一行: 遮住卡号与论坛密码后写日志, 并记为报错
    /// </summary>
    internal static void OnLauncherError(string line, CatLogRedactor redactor, ConcurrentQueue<string> errorLines)
    {
        var safe = redactor.Redact(line);
        Log.Warning("[Minion] launcher(stderr): {Line}", safe);
        errorLines.Enqueue(safe.Trim());
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
    ///     日志里不留 Minion 的论坛密码与卡号
    /// </summary>
    internal static string Redact(string launcherExe, IEnumerable<string> arguments) =>
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
