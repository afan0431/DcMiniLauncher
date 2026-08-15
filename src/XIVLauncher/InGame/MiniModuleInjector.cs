using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace XIVLauncher.InGame;

/// <summary>
///     把 <c>MiniLauncherModule.dll</c>（F4 游戏内模块）注入已经跑起来的游戏进程。
///     经典 <c>CreateRemoteThread</c> + <c>LoadLibraryW</c>：启动器本来就持有游戏进程句柄和 PID,
///     不需要任何调试器权限, 也不碰 Dalamud 那一套（硬约束: 不依赖 Dalamud）。
///     ⚠ 判据纪律: 远程线程返回值不算数, 一律以「目标进程的模块列表里真出现了这个 DLL」为准。
/// </summary>
public static class MiniModuleInjector
{
    public const string MODULE_FILE_NAME = "MiniLauncherModule.dll";

    /// <summary>LoadLibraryW 要跑完整个 DllMain, 给足时间; 超时说明目标进程卡住了</summary>
    private const int REMOTE_THREAD_TIMEOUT_MS = 30_000;

    /// <summary>
    ///     模块和启动器一起发布, 就在启动器自己的目录下。开发时由
    ///     <c>src\XIVLauncher.MiniModule\build.ps1</c> 直接产到同一个输出目录。
    /// </summary>
    public static FileInfo ModulePath => new(Path.Combine(AppContext.BaseDirectory, MODULE_FILE_NAME));

    /// <summary>目标进程里是否已经有这个模块（重复注入会让 DllMain 再跑一遍, 必须先挡掉）</summary>
    public static bool IsInjected(Process gameProcess)
    {
        try
        {
            gameProcess.Refresh();

            foreach (ProcessModule module in gameProcess.Modules)
            {
                if (string.Equals(module.ModuleName, MODULE_FILE_NAME, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            // 模块列表在进程刚起来/正在退出时会短暂取不到, 不是错误
            Log.Debug(ex, "[MiniModule] 读取目标进程模块列表失败");
        }

        return false;
    }

    /// <summary>
    ///     注入。返回 null 表示成功, 否则是失败原因（调用方只提示、不影响已经在跑的游戏）。
    /// </summary>
    public static string? Inject(Process gameProcess)
    {
        var modulePath = ModulePath;

        if (!modulePath.Exists)
            return $"找不到模块 {modulePath.FullName}（需要先跑 src\\XIVLauncher.MiniModule\\build.ps1）";

        if (gameProcess.HasExited)
            return "游戏进程已经退出";

        if (IsInjected(gameProcess))
        {
            Log.Information("[MiniModule] 目标进程 (PID={Pid}) 里模块已经在了, 跳过注入", gameProcess.Id);
            return null;
        }

        var access = ProcessAccess.CREATE_THREAD | ProcessAccess.QUERY_INFORMATION | ProcessAccess.VM_OPERATION |
                     ProcessAccess.VM_WRITE     | ProcessAccess.VM_READ;

        var process = NativeMethods.OpenProcess(access, false, gameProcess.Id);

        if (process == IntPtr.Zero)
            return $"OpenProcess 失败 (err={Marshal.GetLastWin32Error()})";

        var remotePath = IntPtr.Zero;

        try
        {
            // LoadLibraryW 要读的是目标进程里的宽字符串, 所以先把路径搬过去
            var pathBytes = Encoding.Unicode.GetBytes(modulePath.FullName + '\0');

            remotePath = NativeMethods.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)pathBytes.Length,
                                                      NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                                                      NativeMethods.PAGE_READWRITE);

            if (remotePath == IntPtr.Zero)
                return $"VirtualAllocEx 失败 (err={Marshal.GetLastWin32Error()})";

            if (!NativeMethods.WriteProcessMemory(process, remotePath, pathBytes, (UIntPtr)pathBytes.Length, out _))
                return $"WriteProcessMemory 失败 (err={Marshal.GetLastWin32Error()})";

            // kernel32 在所有进程里都映射在同一地址（同一次开机内), 所以本进程解析出来的地址可以直接用
            var kernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
            var loadLibrary = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");

            if (loadLibrary == IntPtr.Zero)
                return $"取 LoadLibraryW 地址失败 (err={Marshal.GetLastWin32Error()})";

            var thread = NativeMethods.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary,
                                                          remotePath, 0, out _);

            if (thread == IntPtr.Zero)
                return $"CreateRemoteThread 失败 (err={Marshal.GetLastWin32Error()})";

            try
            {
                var wait = NativeMethods.WaitForSingleObject(thread, REMOTE_THREAD_TIMEOUT_MS);

                if (wait != NativeMethods.WAIT_OBJECT_0)
                    return $"等待远程线程超时/失败 (wait=0x{wait:X})";
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }
        }
        finally
        {
            if (remotePath != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(process, remotePath, UIntPtr.Zero, NativeMethods.MEM_RELEASE);

            NativeMethods.CloseHandle(process);
        }

        // 远程线程的退出码是被截断的 HMODULE, 不足为凭 —— 只认模块列表
        if (!IsInjected(gameProcess))
            return "远程线程跑完了, 但目标进程模块列表里没有这个 DLL（LoadLibraryW 失败）";

        Log.Information("[MiniModule] 已注入 {Module} → PID={Pid}", MODULE_FILE_NAME, gameProcess.Id);
        return null;
    }

    [Flags]
    private enum ProcessAccess : uint
    {
        VM_OPERATION      = 0x0008,
        VM_READ           = 0x0010,
        VM_WRITE          = 0x0020,
        CREATE_THREAD     = 0x0002,
        QUERY_INFORMATION = 0x0400
    }

    private static class NativeMethods
    {
        public const uint MEM_COMMIT    = 0x1000;
        public const uint MEM_RESERVE   = 0x2000;
        public const uint MEM_RELEASE   = 0x8000;
        public const uint PAGE_READWRITE = 0x04;
        public const uint WAIT_OBJECT_0  = 0x0;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccess desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, UIntPtr stackSize,
                                                       IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
