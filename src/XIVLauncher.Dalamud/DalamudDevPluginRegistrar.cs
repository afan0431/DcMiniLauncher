using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud;

/// <summary>
///     把登录器自带的一个插件登记成 Dalamud 的开发插件并让它随游戏启动加载。dalamudConfig.json 里四处都对上, Dalamud 才会在开游戏时加载它:
///     DevMode 为真; DevPluginLoadLocations 有它的路径且开着; DevPluginSettings 里它那条 StartOnBoot 为真并带固定的 WorkingPluginId;
///     默认方案（DefaultProfile）的插件表里按同一个 WorkingPluginId 记着「启用」。
///     按插件 dll 的文件名认「自己的」那一条: 已有就只校正, 没有才加; 其它条目（如开发机上的 Afan）原样不动。
/// </summary>
public static class DalamudDevPluginRegistrar
{
    /// <summary>Ok = 这次起的游戏会加载到它; Action 是做了什么（日志用）; Error 是给人看的原因</summary>
    public sealed record Result(bool Ok, string Action, string? Error);

    private const string LIST_TYPE     = "System.Collections.Generic.List`1[[Dalamud.Configuration.DevPluginLocationSettings, Dalamud]], System.Private.CoreLib";
    private const string LOCATION_TYPE = "Dalamud.Configuration.DevPluginLocationSettings, Dalamud";

    private const string SETTINGS_MAP_TYPE = "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Dalamud.Configuration.Internal.DevPluginSettings, Dalamud]], System.Private.CoreLib";
    private const string SETTINGS_TYPE     = "Dalamud.Configuration.Internal.DevPluginSettings, Dalamud";
    private const string STRING_LIST_TYPE  = "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib";

    private const string PROFILE_TYPE             = "Dalamud.Plugin.Internal.Profiles.ProfileModelV1, Dalamud";
    private const string PROFILE_PLUGIN_TYPE      = "Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud";
    private const string PROFILE_PLUGIN_LIST_TYPE = "System.Collections.Generic.List`1[[Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud]], System.Private.CoreLib";

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
        // Dalamud 按 FileInfo.FullName 查 DevPluginSettings, 几处用同一个规范化后的路径
        pluginDllPath = Path.GetFullPath(pluginDllPath);

        var actions = new List<string>();

        ApplyLocation(config, pluginDllPath, actions);

        var workingId = ApplySettings(config, pluginDllPath, actions);

        ApplyDefaultProfile(config, Path.GetFileNameWithoutExtension(pluginDllPath), workingId, actions);

        // 开发模式关着时 Dalamud 开游戏根本不看开发插件列表
        if (config["DevMode"]?.Type != JTokenType.Boolean || !config.Value<bool>("DevMode"))
        {
            config["DevMode"] = true;
            actions.Add("打开开发模式");
        }

        return actions.Count == 0 ? null : string.Join("，", actions);
    }

    private static bool IsMine(string? path, string fileName) =>
        string.Equals(Path.GetFileName(path ?? ""), fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     DevPluginSettings: 这个路径下那条要 StartOnBoot 为真并带固定的 WorkingPluginId。
    ///     登录器换版本后插件路径会变: 沿用旧路径那条的 WorkingPluginId, 旧条目删掉。返回这个插件的 WorkingPluginId
    /// </summary>
    private static string ApplySettings(JObject config, string pluginDllPath, List<string> actions)
    {
        var fileName = Path.GetFileName(pluginDllPath);

        if (config["DevPluginSettings"] is not JObject map)
        {
            map = new JObject { ["$type"] = SETTINGS_MAP_TYPE };
            config["DevPluginSettings"] = map;
        }

        var mine    = map.Properties().Where(x => x.Name != "$type" && x.Value is JObject && IsMine(x.Name, fileName)).ToList();
        var current = mine.FirstOrDefault(x => string.Equals(x.Name, pluginDllPath, StringComparison.Ordinal));

        var workingId = new[] { current }.Concat(mine)
                                         .Select(x => x?.Value.Value<string>("WorkingPluginId"))
                                         .FirstOrDefault(x => Guid.TryParse(x, out var id) && id != Guid.Empty);

        var changed = false;

        if (workingId == null)
        {
            workingId = Guid.NewGuid().ToString();
            changed   = true;
        }

        foreach (var stale in mine.Where(x => x != current))
        {
            stale.Remove();
            changed = true;
        }

        if (current?.Value is not JObject settings)
        {
            map[pluginDllPath] = new JObject
            {
                ["$type"]              = SETTINGS_TYPE,
                ["StartOnBoot"]        = true,
                ["NotifyForErrors"]    = false,
                ["AutomaticReloading"] = false,
                ["WorkingPluginId"]    = workingId,
                ["DismissedValidationProblems"] = new JObject
                {
                    ["$type"]   = STRING_LIST_TYPE,
                    ["$values"] = new JArray(),
                },
            };
            changed = true;
        }
        else
        {
            if (settings.Value<bool?>("StartOnBoot") != true)
            {
                settings["StartOnBoot"] = true;
                changed                 = true;
            }

            if (!string.Equals(settings.Value<string>("WorkingPluginId"), workingId, StringComparison.OrdinalIgnoreCase))
            {
                settings["WorkingPluginId"] = workingId;
                changed                     = true;
            }
        }

        if (changed)
            actions.Add("设为随游戏启动加载");

        return workingId;
    }

    /// <summary>默认方案的插件表里按 WorkingPluginId 记着「启用」; 同名但 id 不同的旧条目删掉</summary>
    private static void ApplyDefaultProfile(JObject config, string internalName, string workingId, List<string> actions)
    {
        if (config["DefaultProfile"] is not JObject profile)
        {
            profile = new JObject
            {
                ["$type"] = PROFILE_TYPE,
                ["e"]     = true,
                ["id"]    = Guid.Empty.ToString(),
                ["n"]     = "DEFAULT",
            };
            config["DefaultProfile"] = profile;
        }

        if (profile["Plugins"]?["$values"] is not JArray plugins)
        {
            plugins = new JArray();
            profile["Plugins"] = new JObject
            {
                ["$type"]   = PROFILE_PLUGIN_LIST_TYPE,
                ["$values"] = plugins,
            };
        }

        var changed = false;
        var entries = plugins.OfType<JObject>().ToList();
        var mine    = entries.FirstOrDefault(x => string.Equals(x.Value<string>("WorkingPluginId"), workingId, StringComparison.OrdinalIgnoreCase));

        foreach (var stale in entries.Where(x => x != mine && string.Equals(x.Value<string>("InternalName"), internalName, StringComparison.Ordinal)))
        {
            stale.Remove();
            changed = true;
        }

        if (mine == null)
        {
            plugins.Add(new JObject
            {
                ["$type"]           = PROFILE_PLUGIN_TYPE,
                ["InternalName"]    = internalName,
                ["WorkingPluginId"] = workingId,
                ["IsEnabled"]       = true,
            });
            changed = true;
        }
        else if (mine.Value<bool?>("IsEnabled") != true)
        {
            mine["IsEnabled"] = true;
            changed           = true;
        }

        if (changed)
            actions.Add("在默认方案里启用");
    }

    private static void ApplyLocation(JObject config, string pluginDllPath, List<string> actions)
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

        var mine = locations.OfType<JObject>().Where(x => IsMine(x.Value<string>("Path"), fileName)).ToList();

        if (mine.Count == 0)
        {
            locations.Add(new JObject
            {
                ["$type"]     = LOCATION_TYPE,
                ["Path"]      = pluginDllPath,
                ["IsEnabled"] = true,
                ["Nickname"]  = null,
            });
            actions.Add("登记并开启");
            return;
        }

        var first = mine[0];

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
