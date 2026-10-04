namespace XIVLauncher.CatHost;

/// <summary>
///     命令行是否要求无界面启动（<c>--cat-launch</c>, 可加 <c>--cat-simulate</c>）
/// </summary>
public static class CatHostMode
{
    /// <summary>无界面启动开关</summary>
    public const string LAUNCH_SWITCH = "--cat-launch";

    /// <summary>模拟模式开关</summary>
    public const string SIMULATE_SWITCH = "--cat-simulate";

    private static readonly Lazy<string[]> Args = new(Environment.GetCommandLineArgs);

    /// <summary>本进程是否为无界面启动</summary>
    public static bool IsActive => HasSwitch(Args.Value, LAUNCH_SWITCH);

    /// <summary>本进程是否为模拟模式（仅在无界面启动下有效）</summary>
    public static bool IsSimulate => IsActive && HasSwitch(Args.Value, SIMULATE_SWITCH);

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
