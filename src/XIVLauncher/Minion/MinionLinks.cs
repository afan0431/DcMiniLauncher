using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XIVLauncher.Minion;

/// <summary>
///     NTFS 硬链接、目录联接（junction）与链接安全删除。.NET 只有符号链接的托管接口, 其余经 Win32 调用。
/// </summary>
internal static class MinionLinks
{
    /// <summary>跨盘建硬链接时的错误码</summary>
    private const int ERROR_NOT_SAME_DEVICE = 17;

    private const uint GENERIC_WRITE         = 0x40000000;
    private const uint FILE_READ_ATTRIBUTES  = 0x00000080;
    private const uint DELETE                = 0x00010000;
    private const uint FILE_SHARE_ALL        = 0x00000007;
    private const uint OPEN_EXISTING         = 3;
    private const uint FILE_FLAG_BACKUP      = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE = 0x00200000;

    private const uint FSCTL_SET_REPARSE_POINT  = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    private const int  FILE_DISPOSITION_INFO_EX = 21;
    private const uint FILE_DISPOSITION_FLAG_DELETE                   = 0x1;
    private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS          = 0x2;
    private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    /// <summary>
    ///     建硬链接; 跨盘建不了时返回 false（调用方改为复制）, 其余失败抛异常
    /// </summary>
    public static bool TryCreateHardLink(string link, string existing)
    {
        if (CreateHardLinkW(link, existing, IntPtr.Zero))
            return true;

        var error = Marshal.GetLastWin32Error();

        if (error == ERROR_NOT_SAME_DEVICE)
            return false;

        throw new IOException($"建硬链接失败 {link} -> {existing}", new Win32Exception(error));
    }

    /// <summary>
    ///     建目录联接 <paramref name="link" /> 指向 <paramref name="target" />。
    ///     用 DeviceIoControl(FSCTL_SET_REPARSE_POINT) 而不是 <c>mklink /J</c>: 一个客户端目录要建几百个联接,
    ///     每个都起一次 cmd 太慢, 而且插件目录名里有引号、中文等, 经命令行转义容易出错; 联接不需要提权
    /// </summary>
    public static void CreateJunction(string link, string target)
    {
        var fullTarget = Path.GetFullPath(target);

        if (fullTarget.Length > 3)
            fullTarget = fullTarget.TrimEnd('\\');

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + fullTarget);
        var print      = Encoding.Unicode.GetBytes(fullTarget);

        // REPARSE_DATA_BUFFER（MountPointReparseBuffer）: 头 8 字节 + 4 个 ushort + 两个以 \0 结尾的名字
        var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        var buffer     = new byte[8 + dataLength];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), IO_REPARSE_TAG_MOUNT_POINT);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)dataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        Directory.CreateDirectory(link);

        try
        {
            using var handle = CreateFileW(link, GENERIC_WRITE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP | FILE_FLAG_OPEN_REPARSE, IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"打开 {link} 失败", new Win32Exception(Marshal.GetLastWin32Error()));

            if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException($"建目录联接失败 {link} -> {fullTarget}", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        catch
        {
            try
            {
                Directory.Delete(link, false);
            }
            catch
            {
                // 空目录删不掉时留给整个客户端目录的清理
            }

            throw;
        }
    }

    /// <summary>
    ///     文件的硬链接数; 读不到时返回 0
    /// </summary>
    public static int HardLinkCount(string path)
    {
        using var handle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP | FILE_FLAG_OPEN_REPARSE, IntPtr.Zero);

        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            return 0;

        return (int)info.NumberOfLinks;
    }

    /// <summary>
    ///     这一项本身是重解析点（联接、符号链接等）
    /// </summary>
    public static bool IsReparsePoint(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    ///     删掉一个链接本身（目录联接、目录符号链接或文件符号链接）, 不碰它指向的内容
    /// </summary>
    public static void DeleteLinkOnly(FileSystemInfo link)
    {
        if (link is DirectoryInfo directory)
            directory.Delete(false);
        else
            DeleteFile(link.FullName);
    }

    /// <summary>
    ///     删一个文件名（硬链接只删这一个名字）。只读文件不改属性直接删: 硬链接共用属性, 清只读会连主目录那份一起改掉
    /// </summary>
    public static void DeleteFile(string path)
    {
        var attributes = File.GetAttributes(path);

        if (!attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.Delete(path);
            return;
        }

        using var handle = CreateFileW(path, DELETE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP | FILE_FLAG_OPEN_REPARSE, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException($"打开 {path} 失败", new Win32Exception(Marshal.GetLastWin32Error()));

        var flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;

        if (!SetFileInformationByHandle(handle, FILE_DISPOSITION_INFO_EX, ref flags, sizeof(uint)))
            throw new IOException($"删除 {path} 失败", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, ref uint information, int bufferSize);

    // FILETIME 是两个 DWORD, 按 4 字节对齐
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
