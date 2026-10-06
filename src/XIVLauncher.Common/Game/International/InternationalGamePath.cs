namespace XIVLauncher.Common.Game.International;

/// <summary>国际服游戏目录的检查结果</summary>
public enum InternationalGamePathState
{
    /// <summary>看起来是完整的国际服客户端</summary>
    Ok,

    /// <summary>没填</summary>
    NotSet,

    /// <summary>目录不存在</summary>
    NotFound,

    /// <summary>目录下没有 game\ffxiv_dx11.exe 或 boot 文件夹</summary>
    NotGameRoot,

    /// <summary>目录下有国服客户端才有的东西（sdo 文件夹等）, 多半选成了国服目录</summary>
    LooksLikeChineseClient
}

/// <summary>
///     国际服游戏目录的检查: 应该是含 boot 和 game 两个文件夹的那一层。
///     判定参照 goatcorp XIVLauncher.Common/Util/GameHelpers.cs:19-36（要有 game 与 boot; 有 sdo 或 boot\FFXIV_Boot.exe 的是国服）。
/// </summary>
public static class InternationalGamePath
{
    /// <summary>
    ///     检查目录
    /// </summary>
    public static InternationalGamePathState Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return InternationalGamePathState.NotSet;

        try
        {
            if (!Directory.Exists(path))
                return InternationalGamePathState.NotFound;

            if (!File.Exists(Path.Combine(path, "game", "ffxiv_dx11.exe")) || !Directory.Exists(Path.Combine(path, "boot")))
                return InternationalGamePathState.NotGameRoot;

            if (Directory.Exists(Path.Combine(path, "sdo")) ||
                Directory.Exists(Path.Combine(path, "rail_files")) ||
                File.Exists(Path.Combine(path, "boot", "FFXIV_Boot.exe")))
                return InternationalGamePathState.LooksLikeChineseClient;

            return InternationalGamePathState.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return InternationalGamePathState.NotFound;
        }
    }

    /// <summary>
    ///     给人看的说明; <see cref="InternationalGamePathState.Ok" /> 返回 null
    /// </summary>
    public static string? Describe(InternationalGamePathState state) =>
        state switch
        {
            InternationalGamePathState.Ok                     => null,
            InternationalGamePathState.NotSet                 => "还没有设置国际服游戏目录",
            InternationalGamePathState.NotFound               => "国际服游戏目录不存在",
            InternationalGamePathState.NotGameRoot            => "国际服游戏目录不对: 应该选含 boot 和 game 两个文件夹的那一层",
            InternationalGamePathState.LooksLikeChineseClient => "国际服游戏目录里是国服客户端, 请改选国际服客户端的目录",
            _                                                 => "国际服游戏目录无效"
        };
}
