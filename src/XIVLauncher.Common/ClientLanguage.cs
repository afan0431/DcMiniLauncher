// 移植自 goatcorp/FFXIVQuickLauncher（GPL-3.0）提交 a6f33a9: src/XIVLauncher.Common/ClientLanguage.cs
// 改动: 「是否北美地区」改为可传入（便于测试）, 去掉本项目用不到的 Lodestone 语言代码。

using System.Globalization;

namespace XIVLauncher.Common;

/// <summary>
///     国际服客户端语言; 数值就是游戏参数 language 和原版 Dalamud 的 --dalamud-client-language
/// </summary>
public enum ClientLanguage
{
    /// <summary>日语</summary>
    Japanese = 0,

    /// <summary>英语</summary>
    English = 1,

    /// <summary>德语</summary>
    German = 2,

    /// <summary>法语</summary>
    French = 3
}

/// <summary>
///     客户端语言的语言代码
/// </summary>
public static class ClientLanguageExtensions
{
    /// <summary>
    ///     语言代码: 日 ja、英 en-us（系统区域是美 / 加 / 墨）或 en-gb、德 de、法 fr
    /// </summary>
    /// <param name="language">客户端语言</param>
    /// <param name="isNorthAmerica">是否按北美地区; 不传时看系统区域</param>
    public static string GetLangCode(this ClientLanguage language, bool? isNorthAmerica = null) =>
        language switch
        {
            ClientLanguage.Japanese                                               => "ja",
            ClientLanguage.English when isNorthAmerica ?? IsRegionNorthAmerica() => "en-us",
            ClientLanguage.English                                                => "en-gb",
            ClientLanguage.German                                                 => "de",
            ClientLanguage.French                                                 => "fr",
            _                                                                     => "en-gb"
        };

    /// <summary>
    ///     系统区域是否为美国、墨西哥、加拿大（goatcorp GameHelpers.IsRegionNorthAmerica）
    /// </summary>
    public static bool IsRegionNorthAmerica() =>
        RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "MX" or "CA";
}
