using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace XIVLauncher.Common.Game;

/// <summary>
///     解除游戏自带的同机多开限制。游戏启动时占用两个命名互斥体之一
///     （<c>6AA83AB5-BAC4-4a36-9F66-A309770760CB_ffxiv_game00</c> / <c>_ffxiv_game01</c>）, 两个都被占时新开的客户端会自行退出。
///     这里从外部关掉已开游戏进程里的这两个句柄, 互斥体随最后一个句柄关闭而消失, 名额空出, 已开的游戏照常运行
/// </summary>
public static class GameInstanceLimit
{
    /// <summary>
    ///     游戏名额互斥体名的公共部分, 后接两位序号
    /// </summary>
    public const string MUTEX_NAME_MARKER = "6AA83AB5-BAC4-4a36-9F66-A309770760CB_ffxiv_game";

    private const string GAME_PROCESS_NAME = "ffxiv_dx11";

    private const int  PROCESS_HANDLE_INFORMATION   = 51;
    private const int  OBJECT_NAME_INFORMATION      = 1;
    private const uint STATUS_INFO_LENGTH_MISMATCH  = 0xC0000004;
    private const uint PROCESS_DUP_HANDLE           = 0x0040;
    private const uint PROCESS_QUERY_INFORMATION    = 0x0400;
    private const uint DUPLICATE_CLOSE_SOURCE       = 0x0001;
    private const uint DUPLICATE_SAME_ACCESS        = 0x0002;

    /// <summary>
    ///     关掉本机所有游戏进程占用的名额互斥体。从不抛异常, 失败只记日志
    /// </summary>
    /// <returns>关掉的句柄数</returns>
    public static int Release()
    {
        var processes = Process.GetProcessesByName(GAME_PROCESS_NAME);

        try
        {
            if (processes.Length == 0)
                return 0;

            var closed = ReleaseIn(processes.Select(process => process.Id), IsGameSlotName);
            Log.Information("[多开] 本机已开游戏 {Count} 个, 关掉名额互斥体 {Closed} 个", processes.Length, closed);
            return closed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[多开] 解除多开限制失败");
            return 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    ///     名字是否是游戏名额互斥体（内核对象名带会话前缀, 如 <c>\Sessions\1\BaseNamedObjects\…</c>）
    /// </summary>
    public static bool IsGameSlotName(string objectName) =>
        objectName.Contains(MUTEX_NAME_MARKER, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     关掉指定进程里名字满足 <paramref name="isTarget" /> 的互斥体句柄。单个进程失败只记日志, 不影响其它进程
    /// </summary>
    /// <param name="processIds">要处理的进程</param>
    /// <param name="isTarget">按内核对象名判断是否关掉</param>
    /// <returns>关掉的句柄数</returns>
    public static int ReleaseIn(IEnumerable<int> processIds, Func<string, bool> isTarget)
    {
        var mutantType = MutantTypeIndex();
        var closed     = 0;

        foreach (var pid in processIds)
        {
            try
            {
                closed += ReleaseInProcess(pid, mutantType, isTarget);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[多开] 处理进程 {Pid} 失败", pid);
            }
        }

        return closed;
    }

    private static int ReleaseInProcess(int pid, uint mutantType, Func<string, bool> isTarget)
    {
        var process = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, pid);

        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"打不开进程 {pid}");

        try
        {
            var closed = 0;

            foreach (var entry in Handles(process))
            {
                if (entry.ObjectTypeIndex != mutantType)
                    continue;

                if (!DuplicateHandle(process, entry.HandleValue, GetCurrentProcess(), out var copy, 0, false, DUPLICATE_SAME_ACCESS))
                    continue;

                string? name;

                try
                {
                    name = ObjectName(copy);
                }
                finally
                {
                    CloseHandle(copy);
                }

                if (string.IsNullOrEmpty(name) || !isTarget(name))
                    continue;

                if (DuplicateHandle(process, entry.HandleValue, IntPtr.Zero, out _, 0, false, DUPLICATE_CLOSE_SOURCE))
                {
                    closed++;
                    Log.Information("[多开] 已关掉进程 {Pid} 的 {Name}", pid, name);
                }
                else
                {
                    Log.Warning("[多开] 关不掉进程 {Pid} 的 {Name}: {Error}", pid, name, Marshal.GetLastWin32Error());
                }
            }

            return closed;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    ///     互斥体在这台机器上的对象类型编号: 自己建一个互斥体, 在本进程的句柄表里找到它
    /// </summary>
    private static uint MutantTypeIndex()
    {
        using var probe  = new Mutex();
        var       handle = probe.SafeWaitHandle.DangerousGetHandle();

        foreach (var entry in Handles(GetCurrentProcess()))
        {
            if (entry.HandleValue == handle)
                return entry.ObjectTypeIndex;
        }

        throw new InvalidOperationException("找不到互斥体的对象类型编号");
    }

    private static List<PROCESS_HANDLE_TABLE_ENTRY_INFO> Handles(IntPtr process)
    {
        var length = 0x10000;

        while (true)
        {
            var buffer = Marshal.AllocHGlobal(length);

            try
            {
                var status = NtQueryInformationProcess(process, PROCESS_HANDLE_INFORMATION, buffer, length, out var needed);

                if ((uint)status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    length = Math.Max(needed, length * 2);
                    continue;
                }

                if (status < 0)
                    throw new Win32Exception($"读取句柄表失败: 0x{status:X8}");

                var count  = (long)Marshal.ReadIntPtr(buffer);
                var size   = Marshal.SizeOf<PROCESS_HANDLE_TABLE_ENTRY_INFO>();
                var first  = buffer + Marshal.SizeOf<PROCESS_HANDLE_SNAPSHOT_INFORMATION>();
                var result = new List<PROCESS_HANDLE_TABLE_ENTRY_INFO>((int)count);

                for (var i = 0L; i < count; i++)
                    result.Add(Marshal.PtrToStructure<PROCESS_HANDLE_TABLE_ENTRY_INFO>(first + (int)(i * size)));

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string? ObjectName(IntPtr handle)
    {
        var length = 0x400;
        var buffer = Marshal.AllocHGlobal(length);

        try
        {
            var status = NtQueryObject(handle, OBJECT_NAME_INFORMATION, buffer, length, out _);

            if (status < 0)
                return null;

            var name = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
            return name.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(name.Buffer, name.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_HANDLE_SNAPSHOT_INFORMATION
    {
        public IntPtr NumberOfHandles;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_HANDLE_TABLE_ENTRY_INFO
    {
        public IntPtr HandleValue;
        public IntPtr HandleCount;
        public IntPtr PointerCount;
        public uint   GrantedAccess;
        public uint   ObjectTypeIndex;
        public uint   HandleAttributes;
        public uint   Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int objectInformationClass, IntPtr objectInformation, int objectInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
