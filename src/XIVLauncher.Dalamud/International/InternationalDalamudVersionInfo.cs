// 移植自 goatcorp/FFXIVQuickLauncher（GPL-3.0）提交 a6f33a9: src/XIVLauncher.Common/Dalamud/DalamudVersionInfo.cs

using Newtonsoft.Json;

namespace XIVLauncher.Dalamud.International;

/// <summary>
///     goatcorp 的 Dalamud 版本信息（VersionInfo 接口的返回, 也是装好后写在版本目录里的 version.json）
/// </summary>
public sealed class InternationalDalamudVersionInfo
{
    /// <summary>Dalamud 版本, 也是 Hooks 下的目录名</summary>
    public string AssemblyVersion { get; set; } = string.Empty;

    /// <summary>这版 Dalamud 支持的游戏版本（与 game\ffxivgame.ver 比较）</summary>
    public string SupportedGameVer { get; set; } = string.Empty;

    /// <summary>需要的 .NET 运行时版本</summary>
    public string RuntimeVersion { get; set; } = string.Empty;

    /// <summary>是否需要下载运行时</summary>
    public bool RuntimeRequired { get; set; }

    /// <summary>分支名</summary>
    public string? Track { get; set; }

    /// <summary>分支显示名</summary>
    public string? DisplayName { get; set; }

    /// <summary>测试分支的密钥（本启动器不用测试分支）</summary>
    public string? Key { get; set; }

    /// <summary>本体 zip 的下载地址</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>读版本目录里的 version.json</summary>
    public static InternationalDalamudVersionInfo? Load(FileInfo file) =>
        JsonConvert.DeserializeObject<InternationalDalamudVersionInfo>(File.ReadAllText(file.FullName));
}
