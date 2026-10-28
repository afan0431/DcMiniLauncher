using System.IO;
using System.Runtime.InteropServices;
using XIVLauncher.InGame;
using Xunit;

namespace XIVLauncher.Test.InGame;

/// <summary>
///     从 DLL 文件导出表读出的 RVA 与系统加载后 GetProcAddress 给的地址一致
/// </summary>
public sealed class PeExportsTests
{
    private static string Kernel32 => Path.Combine(Environment.SystemDirectory, "kernel32.dll");

    [Theory]
    [InlineData("LoadLibraryW")]
    [InlineData("CreateRemoteThread")]
    [InlineData("Beep")]
    [InlineData("lstrlenW")]
    public void FindRva_MatchesGetProcAddress(string export)
    {
        var module  = GetModuleHandle("kernel32.dll");
        var address = GetProcAddress(module, export);

        Assert.NotEqual(IntPtr.Zero, address);

        uint? rva;

        try
        {
            rva = PeExports.FindRva(Kernel32, export);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("转发"))
        {
            // 转发到别的模块的导出, GetProcAddress 给的是那个模块里的地址, 没法对比
            return;
        }

        Assert.Equal((uint)(address - module), rva);
    }

    [Fact]
    public void FindRva_UnknownExport_ReturnsNull() =>
        Assert.Null(PeExports.FindRva(Kernel32, "cat_remote_init"));

    [Fact]
    public void FindRva_NotAPeFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-a-dll-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "not a dll");

        try
        {
            Assert.Throws<InvalidDataException>(() => PeExports.FindRva(path, "cat_remote_init"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}
