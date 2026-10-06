namespace XIVLauncher.CatHost;

/// <summary>
///     命令行是否要求无界面启动（<c>--cat-launch</c>, 可加 <c>--cat-simulate</c>、<c>--cat-detached</c>）
///     或无界面更新启动器（<c>--cat-self-update</c>）
/// </summary>
public static class CatHostMode
{
    /// <summary>无界面启动开关</summary>
    public const string LAUNCH_SWITCH = "--cat-launch";

    /// <summary>模拟模式开关</summary>
    public const string SIMULATE_SWITCH = "--cat-simulate";

    /// <summary>
    ///     本进程从安装目录之外的一份副本运行（Cat 工作台按版本另存的）: 启动器更新不会结束它, 所以不拦更新
    /// </summary>
    public const string DETACHED_SWITCH = "--cat-detached";

    /// <summary>无界面检查并安装启动器更新后退出, 不显示窗口、不重启启动器</summary>
    public const string SELF_UPDATE_SWITCH = "--cat-self-update";

    private static readonly Lazy<string[]> Args = new(Environment.GetCommandLineArgs);

    /// <summary>本进程是否为无界面启动</summary>
    public static bool IsActive => HasSwitch(Args.Value, LAUNCH_SWITCH);

    /// <summary>本进程是否为模拟模式（仅在无界面启动下有效）</summary>
    public static bool IsSimulate => IsActive && HasSwitch(Args.Value, SIMULATE_SWITCH);

    /// <summary>本进程是否从安装目录之外的副本运行（仅在无界面启动下有效）</summary>
    public static bool IsDetached => IsActive && HasSwitch(Args.Value, DETACHED_SWITCH);

    /// <summary>本进程是否只做无界面的启动器更新（与无界面启动同时给时以无界面启动为准）</summary>
    public static bool IsSelfUpdate => !IsActive && HasSwitch(Args.Value, SELF_UPDATE_SWITCH);

    /// <summary>
    ///     参数列表里是否有某个开关（忽略大小写）
    /// </summary>
    public static bool HasSwitch(IEnumerable<string> args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     取 <c>--name=value</c> 或 <c>--name value</c> 形式的参数值
    /// </summary>
    public static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..];

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1];
        }

        return null;
    }

    /// <summary>当前进程的命令行参数</summary>
    public static IReadOnlyList<string> CommandLineArgs => Args.Value;
}
