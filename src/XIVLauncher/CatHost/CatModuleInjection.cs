using System.Diagnostics;
using XIVLauncher.Common.Util;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     游戏内模块的注入策略。
///     国服真实游戏一律注入模块（共用游戏配置在模块里, 是否生效只看本机 machine.json）; 模式按启动方式定:
///     完整模式装标题守卫和选角右键菜单, 只读模式不装, 游戏的行为与没注入时一样。模块已经在了就不再注入, 模式也不变
/// </summary>
public static class CatModuleInjection
{
    /// <summary>
    ///     环境变量: 设为 0 / false / off 时, 接管来的游戏里没有模块就不补注入（读不到等级和当前角色, 其它照常）。不设 = 补注入
    /// </summary>
    public const string ADOPT_INJECT_VARIABLE = "CAT_DML_ADOPT_INJECT";

    /// <summary>接管时补注入用的跨进程锁: 多个游戏同时被接管时一个一个注</summary>
    public const string ADOPT_INJECT_MUTEX = @"Local\DcMiniLauncher-AdoptInject";

    /// <summary>接管时补注入完一个游戏后, 至少隔这么久才注下一个</summary>
    public static readonly TimeSpan AdoptInjectSpacing = TimeSpan.FromSeconds(10);

    /// <summary>等别的进程补注入完的上限</summary>
    public static readonly TimeSpan AdoptInjectWait = TimeSpan.FromMinutes(10);

    /// <summary>本进程内的注入一个一个做: 查「已注入」与注入之间不让别的调用插进来</summary>
    private static readonly object InjectSync = new();

    /// <summary>接管来的游戏要不要补注入（每次现读环境变量）</summary>
    public static bool InjectOnAdopt =>
        Environment.GetEnvironmentVariable(ADOPT_INJECT_VARIABLE)?.Trim().ToLowerInvariant() is not ("0" or "false" or "off");

    /// <summary>
    ///     起游戏（含崩溃重开、界面版启动、对在跑的游戏补注入）时用不用只读模式: 自动进入角色或挂 Minion 的按完整模式, 其它按只读模式
    /// </summary>
    public static bool ReadOnlyForLaunch(bool autoEnter, bool minion) => !autoEnter && !minion;

    /// <summary>
    ///     接管来的游戏里没有模块、要补注入时用不用只读模式: 自动进入角色的按完整模式（与原来那次上号一致）, 其它按只读模式
    /// </summary>
    public static bool ReadOnlyForAdopt(bool autoEnter) => !autoEnter;

    /// <summary>
    ///     等游戏窗口出现（模块要挂在窗口线程上, 窗口出现前注入会装不上主线程通道）后注入; 已经在了就不再注入。
    ///     adopted = 接管来的游戏: 补注入受环境变量 <see cref="ADOPT_INJECT_VARIABLE" /> 控制, 并经跨进程锁与别的接管进程一个一个注、注完隔一会儿再放下一个。
    ///     返回 null 表示模块已在游戏里, 否则是没注入的原因
    /// </summary>
    public static async Task<string?> InjectWhenReadyAsync
    (
        ICatModuleTarget          target,
        bool                      readOnly,
        bool                      adopted,
        CatModuleInjectOptions?   options,
        CancellationToken         cancellationToken
    )
    {
        options ??= new CatModuleInjectOptions();

        var clock = Stopwatch.StartNew();

        while (true)
        {
            if (target.HasExited)
                return "游戏进程已经退出";

            if (target.HasWindow)
                break;

            if (clock.Elapsed > options.WindowTimeout)
                return $"等了 {options.WindowTimeout.TotalSeconds:F0} 秒游戏窗口没有出现";

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (target.IsInjected)
            return null;

        if (!adopted)
            return await Task.Run(() => InjectOnce(target, readOnly), cancellationToken).ConfigureAwait(false);

        if (!options.InjectOnAdopt())
            return $"接管来的游戏里没有游戏内模块, 按环境变量 {ADOPT_INJECT_VARIABLE} 的设置不补注入";

        // 拿不到锁（超时、打不开）时不加锁照常注
        using var injectGate = await CrossProcessMutex.TryAcquireAsync(options.AdoptMutex, options.AdoptWait).ConfigureAwait(false);

        // 等锁期间别的调用已经注好了: 不再注, 也不占着锁等间隔
        if (target.IsInjected)
            return null;

        var error = await Task.Run(() => InjectOnce(target, readOnly), cancellationToken).ConfigureAwait(false);

        if (injectGate != null)
            await Task.Delay(options.AdoptSpacing, cancellationToken).ConfigureAwait(false);

        return error;
    }

    private static string? InjectOnce(ICatModuleTarget target, bool readOnly)
    {
        lock (InjectSync)
            return target.IsInjected ? null : target.Inject(readOnly);
    }
}

/// <summary>
///     <see cref="CatModuleInjection.InjectWhenReadyAsync" /> 的时限与开关
/// </summary>
public sealed record CatModuleInjectOptions
{
    /// <summary>等游戏窗口出现的上限</summary>
    public TimeSpan WindowTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>看一次窗口出没出现的间隔</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>接管时补注入用的跨进程锁名</summary>
    public string AdoptMutex { get; init; } = CatModuleInjection.ADOPT_INJECT_MUTEX;

    /// <summary>等别的进程补注入完的上限</summary>
    public TimeSpan AdoptWait { get; init; } = CatModuleInjection.AdoptInjectWait;

    /// <summary>接管时补注入完后至少隔这么久才放下一个</summary>
    public TimeSpan AdoptSpacing { get; init; } = CatModuleInjection.AdoptInjectSpacing;

    /// <summary>接管来的游戏要不要补注入</summary>
    public Func<bool> InjectOnAdopt { get; init; } = () => CatModuleInjection.InjectOnAdopt;
}

/// <summary>
///     要注入游戏内模块的游戏
/// </summary>
public interface ICatModuleTarget
{
    /// <summary>进程已经退出</summary>
    bool HasExited { get; }

    /// <summary>游戏窗口已经出现</summary>
    bool HasWindow { get; }

    /// <summary>进程里已经有模块</summary>
    bool IsInjected { get; }

    /// <summary>
    ///     注入; 返回 null 表示成功, 否则是失败原因
    /// </summary>
    /// <param name="readOnly">按只读模式装</param>
    string? Inject(bool readOnly);
}

/// <summary>
///     真实游戏进程
/// </summary>
public sealed class CatProcessModuleTarget(Process process) : ICatModuleTarget
{
    /// <inheritdoc />
    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <inheritdoc />
    public bool HasWindow
    {
        get
        {
            try
            {
                process.Refresh();
                return process.MainWindowHandle != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool IsInjected => MiniModuleInjector.IsInjected(process);

    /// <inheritdoc />
    public string? Inject(bool readOnly) => MiniModuleInjector.Inject(process, readOnly);
}
