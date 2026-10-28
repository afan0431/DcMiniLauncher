using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using XIVLauncher.InGame;

namespace XIVLauncher.CatHost;

/// <summary>
///     远程截取模块（cat-remote-hook.dll）: 工作台随 launch 下发模块路径、管道名与令牌, 游戏进程一创建就注入并调用 <c>cat_remote_init</c>,
///     要赶在卫月装 Present 钩子之前（截取模块的钩子最先装才在最内层, 画面里才带插件界面）。崩溃重启的新进程同样注入。
///     结果发 <see cref="EVENT" />; 注入失败不影响游戏
/// </summary>
public static partial class CatRemoteHook
{
    /// <summary>注入结果事件</summary>
    public const string EVENT = "game.remoteHook";

    /// <summary>截取模块的初始化导出: <c>DWORD WINAPI cat_remote_init(LPCWSTR "管道名\t令牌")</c></summary>
    public const string INIT_EXPORT = "cat_remote_init";

    /// <summary>截取模块认的管道名前缀; 实际管道名为 前缀 + 游戏进程号 + "-" + 32 位十六进制</summary>
    public const string PIPE_PREFIX = @"\\.\pipe\cat-remote-";

    /// <summary>令牌最短长度（与截取模块一致）</summary>
    public const int MIN_TOKEN_LENGTH = 32;

    /// <summary>令牌最长长度; 截取模块读参数最多 1024 个 UTF-16 单元, 连同管道名要放得下</summary>
    public const int MAX_TOKEN_LENGTH = 512;

    /// <summary><c>cat_remote_init</c> 返回值: 钩子已装、管道在听</summary>
    public const uint INIT_OK = 0;

    /// <summary><c>cat_remote_init</c> 返回值: 参数无效, 什么都没启动</summary>
    public const uint INIT_BAD_ARGUMENT = 1;

    /// <summary><c>cat_remote_init</c> 返回值: 管道建不起来, 什么都没启动</summary>
    public const uint INIT_PIPE_FAILED = 2;

    /// <summary><c>cat_remote_init</c> 返回值: 管道在听但钩子没装上, 推流进程连上后能读到原因</summary>
    public const uint INIT_HOOK_FAILED = 3;

    /// <summary><c>cat_remote_init</c> 返回值: 模块内部出错, 什么都没启动</summary>
    public const uint INIT_PANICKED = 4;

    /// <summary>
    ///     校验 launch 带来的 remoteHook; 通过返回 null, 否则是给外壳的原因。
    ///     pipeName 不带进程号（<c>\\.\pipe\cat-remote-&lt;32 位十六进制&gt;</c>）, 每个游戏进程由 <see cref="PipeNameFor" /> 插入自己的进程号
    /// </summary>
    public static string? Validate(CatRemoteHookParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.DllPath) || !Path.IsPathFullyQualified(parameters.DllPath) ||
            !parameters.DllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return "remoteHook.dllPath 必须是 .dll 文件的完整路径";

        if (string.IsNullOrEmpty(parameters.PipeName) || !BasePipeName().IsMatch(parameters.PipeName))
            return $@"remoteHook.pipeName 必须是 {PIPE_PREFIX}<32 位十六进制>";

        if (string.IsNullOrEmpty(parameters.Token) || parameters.Token.Length is < MIN_TOKEN_LENGTH or > MAX_TOKEN_LENGTH ||
            parameters.Token.Any(char.IsWhiteSpace))
            return $"remoteHook.token 必须是 {MIN_TOKEN_LENGTH} 到 {MAX_TOKEN_LENGTH} 个不含空白的字符";

        return null;
    }

    /// <summary>这个游戏进程的管道名: 在下发的管道名前缀后插入进程号（截取模块只认带自己进程号的管道名）</summary>
    public static string PipeNameFor(string basePipeName, int pid) =>
        $"{PIPE_PREFIX}{pid}-{basePipeName[PIPE_PREFIX.Length..]}";

    /// <summary>
    ///     注入截取模块并初始化。不抛异常: 失败都折成 <see cref="CatRemoteHookResult" />（游戏照常运行）
    /// </summary>
    public static CatRemoteHookResult Inject(ICatRemoteHookTarget target, CatRemoteHookParams parameters)
    {
        try
        {
            return InjectCore(target, parameters);
        }
        catch (Exception ex)
        {
            return CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, $"注入截取模块时出错: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static CatRemoteHookResult InjectCore(ICatRemoteHookTarget target, CatRemoteHookParams parameters)
    {
        // 目标进程模块列表里的是规范化的完整路径, 按同一形式加载和查找
        var dllPath = Path.GetFullPath(parameters.DllPath);

        if (!File.Exists(dllPath))
            return CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, $"找不到截取模块 {dllPath}");

        if (target.HasExited)
            return CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, "游戏进程已经退出");

        if (target.LoadLibrary(dllPath) is { } loadFailure)
            return FromFailure("加载截取模块失败", loadFailure);

        var pipeName = PipeNameFor(parameters.PipeName, target.Pid);

        if (target.CallInit(dllPath, $"{pipeName}\t{parameters.Token}", out var result) is { } initFailure)
            return FromFailure("调用 cat_remote_init 失败", initFailure);

        return result switch
        {
            INIT_OK           => CatRemoteHookResult.Ready(pipeName),
            INIT_HOOK_FAILED  => new CatRemoteHookResult(false, pipeName, CatCodes.REMOTE_HOOK_FAILED, "截取模块已启动但未能安装钩子, 连接管道后可读到具体原因"),
            INIT_BAD_ARGUMENT => CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, "截取模块初始化失败: 参数无效"),
            INIT_PIPE_FAILED  => CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, "截取模块初始化失败: 无法创建管道"),
            INIT_PANICKED     => CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, "截取模块初始化时发生内部错误"),
            _                 => CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, $"截取模块初始化返回未知结果 {result}")
        };
    }

    private static CatRemoteHookResult FromFailure(string step, InjectFailure failure) =>
        failure.AccessDenied
            ? CatRemoteHookResult.Failed(CatCodes.REMOTE_TARGET_ELEVATED, $"{step}: 无法打开游戏进程, 游戏可能以更高权限运行（{failure.Message}）")
            : CatRemoteHookResult.Failed(CatCodes.REMOTE_HOOK_FAILED, $"{step}: {failure.Message}");

    [GeneratedRegex(@"^\\\\\.\\pipe\\cat-remote-[0-9a-fA-F]{32}$")]
    private static partial Regex BasePipeName();
}

/// <summary>
///     截取模块注入结果。Ok = 钩子已装、管道在听; PipeName = 截取模块在听的管道（含进程号）, 管道没建起来时为 null（钩子没装上时管道仍在听, 连上能读到原因）;
///     Code / Message = 失败时的错误码与说明
/// </summary>
public sealed record CatRemoteHookResult(bool Ok, string? PipeName, string? Code, string? Message)
{
    /// <summary>注入并初始化成功</summary>
    public static CatRemoteHookResult Ready(string pipeName) => new(true, pipeName, null, null);

    /// <summary>失败, 管道没有建起来</summary>
    public static CatRemoteHookResult Failed(string code, string message) => new(false, null, code, message);
}

/// <summary>
///     要注入截取模块的游戏
/// </summary>
public interface ICatRemoteHookTarget
{
    /// <summary>进程号</summary>
    int Pid { get; }

    /// <summary>进程已经退出</summary>
    bool HasExited { get; }

    /// <summary>在游戏进程里加载模块; 返回 null 表示模块已加载</summary>
    InjectFailure? LoadLibrary(string dllPath);

    /// <summary>在游戏进程里调用模块的 <see cref="CatRemoteHook.INIT_EXPORT" />; 返回 null 表示调用完成, result 是它的返回值</summary>
    InjectFailure? CallInit(string dllPath, string argument, out uint result);
}

/// <summary>
///     真实游戏进程
/// </summary>
public sealed class CatProcessRemoteHookTarget(Process process) : ICatRemoteHookTarget
{
    /// <inheritdoc />
    public int Pid => process.Id;

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
    public InjectFailure? LoadLibrary(string dllPath) => MiniModuleInjector.LoadLibrary(process, dllPath);

    /// <inheritdoc />
    public InjectFailure? CallInit(string dllPath, string argument, out uint result) =>
        MiniModuleInjector.CallExport(process, dllPath, CatRemoteHook.INIT_EXPORT, argument, out result);
}
