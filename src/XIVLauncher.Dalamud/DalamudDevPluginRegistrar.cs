using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud;

/// <summary>
///     把登录器自带的一个插件登记成 Dalamud 的开发插件（dalamudConfig.json 的 DevPluginLoadLocations）并开着。
///     按插件 dll 的文件名认「自己的」那一条: 已有就只校正路径与开关, 没有才加; 其它条目（如开发机上的 Afan）原样不动。
/// </summary>
public static class DalamudDevPluginRegistrar
{
    /// <summary>Ok = 这次起的游戏会加载到它; Action 是做了什么（日志用）; Error 是给人看的原因</summary>
    public sealed record Result(bool Ok, string Action, string? Error);

    private const string LIST_TYPE     = "System.Collections.Generic.List`1[[Dalamud.Configuration.DevPluginLocationSettings, Dalamud]], System.Private.CoreLib";
    private const string LOCATION_TYPE = "Dalamud.Configuration.DevPluginLocationSettings, Dalamud";

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromMinutes(2);

    public static async Task<Result> EnsureAsync(DirectoryInfo configDirectory, string pluginDllPath)
    {
        if (!File.Exists(pluginDllPath))
            return new Result(false, "none", $"找不到插件文件 {pluginDllPath}");

        try
        {
            // 同机几个启动器同时起游戏时只让一个在改 dalamudConfig.json; 拿不到锁照常做
            using var _ = await CrossProcessMutex.TryAcquireAsync(CrossProcessMutex.NameForPath("DcMiniLauncher-PluginEnsure", configDirectory.FullName), MutexTimeout)
                                                 .ConfigureAwait(false);

            Directory.CreateDirectory(configDirectory.FullName);

            var configPath = Path.Combine(configDirectory.FullName, "dalamudConfig.json");
            var config     = File.Exists(configPath) ? ReadJson(configPath) as JObject : new JObject();

            if (config == null)
                return new Result(false, "none", "dalamudConfig.json 不是有效的配置");

            var action = Apply(config, pluginDllPath);

            if (action != null)
                WriteJsonAtomic(configPath, config);

            return new Result(true, action ?? "已登记且开着", null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DevPlugin] 登记开发插件 {Path} 失败", pluginDllPath);
            return new Result(false, "none", $"改 Dalamud 配置失败（{ex.Message}）");
        }
    }

    /// <summary>
    ///     改内存里的配置; 没有任何改动返回 null, 否则返回做了什么。同名（文件名）的多余条目只留第一条
    /// </summary>
    internal static string? Apply(JObject config, string pluginDllPath)
    {
        var fileName = Path.GetFileName(pluginDllPath);

        if (config["DevPluginLoadLocations"]?["$values"] is not JArray locations)
        {
            locations = new JArray();
            config["DevPluginLoadLocations"] = new JObject
            {
                ["$type"]   = LIST_TYPE,
                ["$values"] = locations,
            };
        }

        var mine = locations.OfType<JObject>()
                            .Where(x => string.Equals(Path.GetFileName(x.Value<string>("Path") ?? ""), fileName, StringComparison.OrdinalIgnoreCase))
                            .ToList();

        if (mine.Count == 0)
        {
            locations.Add(new JObject
            {
                ["$type"]     = LOCATION_TYPE,
                ["Path"]      = pluginDllPath,
                ["IsEnabled"] = true,
                ["Nickname"]  = null,
            });
            return "登记并开启";
        }

        var actions = new List<string>();
        var first   = mine[0];

        if (!string.Equals(first.Value<string>("Path"), pluginDllPath, StringComparison.OrdinalIgnoreCase))
        {
            first["Path"] = pluginDllPath;
            actions.Add("校正路径");
        }

        if (first.Value<bool?>("IsEnabled") != true)
        {
            first["IsEnabled"] = true;
            actions.Add("开启");
        }

        foreach (var duplicate in mine.Skip(1))
        {
            duplicate.Remove();
            actions.Add("删重复条目");
        }

        return actions.Count == 0 ? null : string.Join("，", actions);
    }

    // 不让 Newtonsoft 把日期、$type 之类按自己的理解改写: 原样读、原样写
    private static JToken ReadJson(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
        return JToken.ReadFrom(reader);
    }

    /// <summary>Dalamud 写的就是 UTF-8 无 BOM、缩进两格、CRLF; 先写临时文件再整份换上, 读到的不会是写了一半的</summary>
    private static void WriteJsonAtomic(string path, JObject config)
    {
        var tmp = path + ".cat-tmp";
        File.WriteAllText(tmp, config.ToString(Formatting.Indented).Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}
