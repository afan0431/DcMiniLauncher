using System.Runtime.InteropServices;
using System.Text;

namespace XIVLauncher.CatHost;

/// <summary>
///     把角色名归一成「人手打时容易写岔的地方都抹平」的样子, 只用于一字不差找不到时的第二轮比较:
///     繁体转简体、全角转半角、转小写、去掉首尾的标点与符号（如「呜啦啦·」末尾的间隔号）。
///     中间的字一个不删、不做前缀匹配: 「户野」与「户野一」、「福福」与「伤心福福」仍是两个人。
/// </summary>
internal static class CharacterNameFold
{
    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    private const uint LCMAP_HALFWIDTH          = 0x00400000;

    public static string Fold(string? name)
    {
        var s = (name ?? "").Trim();
        s = Map(s, LCMAP_SIMPLIFIED_CHINESE);
        s = Map(s, LCMAP_HALFWIDTH);
        s = s.ToLowerInvariant();

        var start = 0;
        var end   = s.Length;

        while (start < end && !char.IsLetterOrDigit(s, start))
            start++;

        while (end > start && !char.IsLetterOrDigit(s, end - 1))
            end--;

        // 全是符号的名字（不大可能）就不去首尾, 免得归一成空串和别的空串相等
        return end > start ? s[start..end].Normalize(NormalizationForm.FormKC) : s;
    }

    /// <summary>Windows 自带的简繁、全半角映射（LCMapStringEx）; 映射失败时原样返回</summary>
    private static string Map(string s, uint flags)
    {
        if (s.Length == 0)
            return s;

        var buffer = new char[s.Length * 2 + 16];
        var n      = LCMapStringEx("zh-CN", flags, s, s.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return n > 0 ? new string(buffer, 0, n) : s;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string lpLocaleName, uint dwMapFlags, string lpSrcStr, int cchSrc, [Out] char[] lpDestStr, int cchDest,
                                            IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);
}
