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

    /// <summary>本进程内的注入一个一个做: 同一个游戏被几处同时注入时, 后到的看到模块已在就跳过, 不会写两次模式文件、注两次</summary>
    private static readonly object InjectSync = new();

    /// <summary>远程线程跑完后等模块出现在模块列表里的最长时间</summary>
    private static readonly TimeSpan ModuleListWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ModuleListPoll = TimeSpan.FromMilliseconds(200);

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
    ///     模式文件 <c>%ProgramData%\DcMiniLauncher\module-&lt;pid&gt;.mode</c>: 注入前写, 模块启动时读。内容 <c>readonly</c> = 只读模式
    ///     （不装标题守卫和选角右键菜单）; 没有这个文件 = 完整模式。游戏退出时随端口文件一起删
    /// </summary>
    public static string ModeFilePath(int processId) =>
        Path.Combine(Path.GetDirectoryName(RunningGameRegistry.PortFilePath(processId))!, $"module-{processId}.mode");

    /// <summary>
    ///     注入。返回 null 表示成功, 否则是失败原因（调用方只提示、不影响已经在跑的游戏）。
    ///     readOnly = 只为读当前角色和职业等级而注入: 模块不装标题守卫和选角右键菜单, 游戏的行为与没注入时一样。
    ///     模块已经在了就不再注入, 它的模式也不变
    /// </summary>
    public static string? Inject(Process gameProcess, bool readOnly = false)
    {
        lock (InjectSync)
            return InjectCore(gameProcess, readOnly);
    }

    private static string? InjectCore(Process gameProcess, bool readOnly)
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

        // 每次注入前都按这次要的模式重写（进程号会复用: 不能让上一个游戏留下的文件决定这一次）; 只读模式写不成就不注, 免得装成完整模式
        if (WriteModeFile(gameProcess.Id, readOnly) is { } modeError)
            return modeError;

        if (LoadLibrary(gameProcess, modulePath.FullName) is { } failure)
            return failure.Message;

        Log.Information("[MiniModule] 已注入 {Module} → PID={Pid}", MODULE_FILE_NAME, gameProcess.Id);
        return null;
    }

    /// <summary>目标进程里按完整路径找已加载的模块, 返回基址; 没有或读不到模块列表返回 null</summary>
    public static IntPtr? FindModuleBase(Process gameProcess, string dllPath)
    {
        try
        {
            gameProcess.Refresh();

            foreach (ProcessModule module in gameProcess.Modules)
            {
                if (string.Equals(module.FileName, dllPath, StringComparison.OrdinalIgnoreCase))
                    return module.BaseAddress;
            }
        }
        catch (Exception ex)
        {
            Log.Information("[MiniModule] 读取目标进程模块列表失败: {Message}", ex.Message);
        }

        return null;
    }

    /// <summary>反复查找模块直到找到或超时; 游戏刚起来时模块列表可能短暂取不到或还没更新</summary>
    public static IntPtr? WaitForModuleBase(Func<IntPtr?> find, TimeSpan timeout, TimeSpan interval)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (find() is { } moduleBase)
                return moduleBase;

            if (DateTime.UtcNow >= deadline)
                return null;

            Thread.Sleep(interval);
        }
    }

    private static IntPtr? WaitForModuleBase(Process gameProcess, string dllPath) =>
        WaitForModuleBase(() => FindModuleBase(gameProcess, dllPath), ModuleListWait, ModuleListPoll);

    /// <summary>
    ///     在目标进程里 <c>LoadLibraryW(dllPath)</c>（dllPath 须是完整路径）。返回 null 表示模块已在目标进程的模块列表里, 否则是失败原因。
    ///     模块已经在了也会再调一次 LoadLibraryW, 只加引用计数, DllMain 不再跑
    /// </summary>
    public static InjectFailure? LoadLibrary(Process gameProcess, string dllPath)
    {
        // kernel32 在所有进程里都映射在同一地址（同一次开机内), 所以本进程解析出来的地址可以直接用
        var kernel32    = NativeMethods.GetModuleHandle("kernel32.dll");
        var loadLibrary = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");

        if (loadLibrary == IntPtr.Zero)
            return new InjectFailure($"取 LoadLibraryW 地址失败 (err={Marshal.GetLastWin32Error()})");

        // LoadLibraryW 要读的是目标进程里的宽字符串, 所以先把路径搬过去; 远程线程的退出码是被截断的 HMODULE, 不足为凭 —— 只认模块列表
        if (RunRemoteThread(gameProcess, loadLibrary, dllPath, out _) is { } failure)
            return failure;

        return WaitForModuleBase(gameProcess, dllPath) == null
                   ? new InjectFailure("远程线程跑完了, 但目标进程模块列表里没有这个 DLL（LoadLibraryW 失败）")
                   : null;
    }

    /// <summary>
    ///     在目标进程里调用已加载模块 dllPath 的导出函数 <c>DWORD WINAPI exportName(LPCWSTR argument)</c>, 等它返回并取返回值。
    ///     导出地址按 DLL 文件的导出表算（不在本进程加载模块）。返回 null 表示调用完成, exitCode 是函数的返回值
    /// </summary>
    public static InjectFailure? CallExport(Process gameProcess, string dllPath, string exportName, string argument, out uint exitCode)
    {
        exitCode = 0;

        if (WaitForModuleBase(gameProcess, dllPath) is not { } moduleBase)
            return new InjectFailure($"目标进程里没有加载 {Path.GetFileName(dllPath)}");

        uint? rva;

        try
        {
            rva = PeExports.FindRva(dllPath, exportName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new InjectFailure($"读 {Path.GetFileName(dllPath)} 的导出表失败: {ex.Message}");
        }

        if (rva == null)
            return new InjectFailure($"{Path.GetFileName(dllPath)} 没有导出 {exportName}");

        return RunRemoteThread(gameProcess, moduleBase + (nint)rva.Value, argument, out exitCode);
    }

    /// <summary>把 argument 按 UTF-16（带结尾 0）写进目标进程, 在那里以它为参数从 startAddress 起一个线程, 等它结束并取退出码</summary>
    private static InjectFailure? RunRemoteThread(Process gameProcess, IntPtr startAddress, string argument, out uint exitCode)
    {
        exitCode = 0;

        if (gameProcess.HasExited)
            return new InjectFailure("游戏进程已经退出");

        var access = ProcessAccess.CREATE_THREAD | ProcessAccess.QUERY_INFORMATION | ProcessAccess.VM_OPERATION |
                     ProcessAccess.VM_WRITE     | ProcessAccess.VM_READ;

        var process = NativeMethods.OpenProcess(access, false, gameProcess.Id);

        if (process == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return new InjectFailure($"OpenProcess 失败 (err={error})", error == NativeMethods.ERROR_ACCESS_DENIED);
        }

        var remoteArgument = IntPtr.Zero;

        try
        {
            var argumentBytes = Encoding.Unicode.GetBytes(argument + '\0');

            remoteArgument = NativeMethods.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)argumentBytes.Length,
                                                          NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                                                          NativeMethods.PAGE_READWRITE);

            if (remoteArgument == IntPtr.Zero)
                return new InjectFailure($"VirtualAllocEx 失败 (err={Marshal.GetLastWin32Error()})");

            if (!NativeMethods.WriteProcessMemory(process, remoteArgument, argumentBytes, (UIntPtr)argumentBytes.Length, out _))
                return new InjectFailure($"WriteProcessMemory 失败 (err={Marshal.GetLastWin32Error()})");

            var thread = NativeMethods.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, startAddress,
                                                          remoteArgument, 0, out _);

            if (thread == IntPtr.Zero)
                return new InjectFailure($"CreateRemoteThread 失败 (err={Marshal.GetLastWin32Error()})");

            try
            {
                var wait = NativeMethods.WaitForSingleObject(thread, REMOTE_THREAD_TIMEOUT_MS);

                // 超时时远程线程可能还在读参数, 不能释放那块内存
                if (wait != NativeMethods.WAIT_OBJECT_0)
                {
                    remoteArgument = IntPtr.Zero;
                    return new InjectFailure($"等待远程线程超时/失败 (wait=0x{wait:X})");
                }

                if (!NativeMethods.GetExitCodeThread(thread, out exitCode))
                    return new InjectFailure($"GetExitCodeThread 失败 (err={Marshal.GetLastWin32Error()})");
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }
        }
        finally
        {
            if (remoteArgument != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(process, remoteArgument, UIntPtr.Zero, NativeMethods.MEM_RELEASE);

            NativeMethods.CloseHandle(process);
        }

        return null;
    }

    /// <summary>写（只读）或删（完整）模式文件; 失败返回原因</summary>
    private static string? WriteModeFile(int processId, bool readOnly)
    {
        var path = ModeFilePath(processId);

        try
        {
            if (readOnly)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "readonly");
            }
            else if (File.Exists(path))
                File.Delete(path);

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MiniModule] 写模式文件失败 {Path}", path);
            return readOnly ? $"写只读模式文件失败, 没有注入: {ex.Message}" : $"删旧的模式文件失败, 没有注入: {ex.Message}";
        }
    }

    /// <summary>游戏退出后删掉它的模式文件</summary>
    public static void DeleteModeFile(int processId)
    {
        try
        {
            var path = ModeFilePath(processId);

            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[MiniModule] 删模式文件失败 PID={Pid}", processId);
        }
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
        public const int  ERROR_ACCESS_DENIED = 5;

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
        public static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>
///     注入失败的原因; AccessDenied = 打不开游戏进程（权限不足, 游戏多半以更高权限运行）
/// </summary>
public sealed record InjectFailure(string Message, bool AccessDenied = false);
