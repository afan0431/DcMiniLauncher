using System.Diagnostics;
using System.IO;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Dalamud;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

public class GameRunner
(
    DalamudSession dalamudSession,
    bool           dalamudOk,
    DirectoryInfo  dotnetRuntimePath
) : IGameRunner
{
    /// <summary>
    ///     ⚠ 必须一直引用住这些 <see cref="GameArgumentInterop.Fixer" />（2026-08-14 排查游戏闪退定位）。
    ///     Fixer 在**游戏进程**里 VirtualAllocEx 了 arg-fix 函数和两个字符串常量, 并把游戏的 sdoLogin
    ///     改成 jmp 进去 —— 这个 hook 是游戏整个生命周期都在的。
    ///     一旦 Fixer 变成垃圾, <c>PrivateAllocation</c> 的**终结器**会去 VirtualFreeEx 掉那几块内存,
    ///     游戏下次调用 sdoLogin 就跳进已经解除映射（或被游戏自己拿去复用）的地址 → 闪退。
    ///     什么时候闪退取决于启动器什么时候 GC, 所以表现为「有时候好好的, 有时候起来十几秒就没了」。
    ///     启动器这边多干点活（比如挂 Minion 时的轮询与等待）就更容易触发。
    ///     游戏退出后这块内存本来就随进程消失, 根本不需要我们释放 —— 所以这里只管留着, 永不释放。
    /// </summary>
    private static readonly List<GameArgumentInterop.Fixer> ArgumentFixers = [];

    public Process Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment, DPIAwareness dpiAwareness)
    {
        Log.Information($"Game Exe:{path}");

        if (dalamudOk)
        {
            var compat = "RunAsInvoker ";
            compat += dpiAwareness switch
            {
                DPIAwareness.Aware   => "HighDPIAware",
                DPIAwareness.Unaware => "DPIUnaware",
                _                    => throw new ArgumentOutOfRangeException()
            };
            environment.Add("__COMPAT_LAYER", compat);

            var prevDalamudRuntime = Environment.GetEnvironmentVariable("DALAMUD_RUNTIME");
            if (string.IsNullOrWhiteSpace(prevDalamudRuntime))
                environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath.FullName);

            var prevDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (string.IsNullOrWhiteSpace(prevDotnetRoot))
                environment.Add("DOTNET_ROOT", dotnetRuntimePath.FullName);

            var prevDotnetLookup = Environment.GetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP");
            if (string.IsNullOrWhiteSpace(prevDotnetLookup))
                environment.Add("DOTNET_MULTILEVEL_LOOKUP", "0");

            return dalamudSession.LaunchGame(new FileInfo(path), arguments, environment);
        }

        return NativeAclFix.LaunchGame
        (
            workingDirectory,
            path,
            arguments,
            environment,
            dpiAwareness,
            process =>
            {
                var argFix = new GameArgumentInterop.Fixer(process);
                argFix.Fix();

                // 见 ArgumentFixers 上面的说明: 放走它 = 终结器会把游戏进程里的 hook 目标释放掉
                lock (ArgumentFixers)
                    ArgumentFixers.Add(argFix);
            }
        );
    }
}
