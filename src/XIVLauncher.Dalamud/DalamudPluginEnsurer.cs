using System.IO.Compression;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud;

/// <summary>
///     起游戏前把一个第三方插件装成它仓库里的最新版, 并在 Dalamud 的默认合集里开着（Cat 自动做的号要靠游戏里的 Afan）。
///     <para>
///         不交给 Dalamud 自己: 它的自动更新只认仓库列表里的仓库, 插件开关与自动更新设置又会被同机在跑的游戏用内存里那份整份写回
///         （2026-10-30 PC2 的 Afan 在默认合集里被关着, 进游戏后没加载, 跨区也就没发起; PC3 改的自动更新被写回 2）。
///         在这里每次起游戏前现装现开, 新起的这个游戏读到的就是对的。
///     </para>
///     <para>
///         配置里开着同名开发插件（DevPluginLoadLocations）的机器是开发机, 什么都不动。
///         装的做法照 Dalamud 的 PluginManager: 解到 installedPlugins/名/版本/, 旁边写本地清单（仓库条目 + 安装来源、WorkingPluginId 等）。
///     </para>
/// </summary>
public static class DalamudPluginEnsurer
{
    /// <summary>Ok = 这次起的游戏会加载到它; Action 是做了什么（日志用）; Error 是给人看的原因（Ok 时也可能带, 表示退而求其次）</summary>
    public sealed record Result(bool Ok, string Action, string? Error);

    private const string PROFILE_PLUGIN_TYPE = "Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud";

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromMinutes(2);

    public static async Task<Result> EnsureAsync(DirectoryInfo configDirectory, string name, string repoUrl, CancellationToken cancellationToken)
    {
        // 同机几个启动器同时起游戏时只让一个在改 installedPlugins 与 dalamudConfig.json; 拿不到锁照常做
        using var _ = await CrossProcessMutex.TryAcquireAsync(CrossProcessMutex.NameForPath("DcMiniLauncher-PluginEnsure", configDirectory.FullName), MutexTimeout)
                                             .ConfigureAwait(false);

        var configPath = Path.Combine(configDirectory.FullName, "dalamudConfig.json");
        var config     = File.Exists(configPath) ? ReadJson(configPath) as JObject : null;

        if (config != null && HasEnabledDevPlugin(config, name))
            return new Result(true, "dev", null);

        var pluginRoot = new DirectoryInfo(Path.Combine(configDirectory.FullName, "installedPlugins", name));
        var installed  = FindInstalled(pluginRoot, name);
        var actions    = new List<string>();
        string? warning = null;

        JObject? entry = null;

        try
        {
            entry = await FetchRepoEntryAsync(repoUrl, name, cancellationToken).ConfigureAwait(false);

            if (entry == null)
                warning = $"插件仓库里没有 {name}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "[PluginEnsure] 读插件仓库 {Url} 失败", repoUrl);
            warning = $"读不到插件仓库（{ex.Message}）";
        }

        if (entry != null && Version.TryParse(entry.Value<string>("AssemblyVersion"), out var latest) && (installed == null || installed.Version < latest))
        {
            try
            {
                installed = await InstallAsync(pluginRoot, name, repoUrl, entry, latest, installed, cancellationToken).ConfigureAwait(false);
                actions.Add($"装了 {latest}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Log.Warning(ex, "[PluginEnsure] 装 {Name} {Version} 失败", name, latest);
                warning = $"下载或解压 {name} {latest} 失败（{ex.Message}）";
            }
        }

        if (installed == null)
            return new Result(false, "none", warning ?? $"没有装 {name}");

        if (config != null && EnableInProfiles(config, name, installed.WorkingPluginId))
        {
            WriteJsonAtomic(configPath, config);
            actions.Add("在默认合集里开启");
        }

        if (warning != null)
            actions.Add($"仍用已装的 {installed.Version}");

        return new Result(true, actions.Count == 0 ? $"已是 {installed.Version} 且开着" : string.Join("，", actions), warning);
    }

    private sealed record Installed(DirectoryInfo Directory, Version Version, string WorkingPluginId);

    private static bool HasEnabledDevPlugin(JObject config, string name) =>
        config["DevPluginLoadLocations"]?["$values"] is JArray locations &&
        locations.OfType<JObject>().Any(x => x.Value<bool?>("IsEnabled") == true &&
                                             string.Equals(Path.GetFileName(x.Value<string>("Path") ?? ""), name + ".dll", StringComparison.OrdinalIgnoreCase));

    private static Installed? FindInstalled(DirectoryInfo pluginRoot, string name)
    {
        if (!pluginRoot.Exists)
            return null;

        Installed? best = null;

        foreach (var dir in pluginRoot.EnumerateDirectories())
        {
            var manifest = Path.Combine(dir.FullName, name + ".json");

            if (!File.Exists(Path.Combine(dir.FullName, name + ".dll")) || !File.Exists(manifest))
                continue;

            try
            {
                if (ReadJson(manifest) is not JObject m || !Version.TryParse(m.Value<string>("AssemblyVersion"), out var v) || m.Value<bool?>("ScheduledForDeletion") == true)
                    continue;

                var id = m.Value<string>("WorkingPluginId");

                if (best == null || v > best.Version)
                    best = new Installed(dir, v, Guid.TryParse(id, out _) ? id! : "");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[PluginEnsure] 读本地清单 {Path} 失败", manifest);
            }
        }

        return best;
    }

    private static async Task<JObject?> FetchRepoEntryAsync(string repoUrl, string name, CancellationToken cancellationToken)
    {
        using var http = CreateHttp();
        var text = await http.GetStringAsync(repoUrl, cancellationToken).ConfigureAwait(false);

        return ParseJson(text) is JArray list
                   ? list.OfType<JObject>().FirstOrDefault(x => string.Equals(x.Value<string>("InternalName"), name, StringComparison.Ordinal))
                   : null;
    }

    private static async Task<Installed> InstallAsync(DirectoryInfo pluginRoot, string name, string repoUrl, JObject entry, Version version, Installed? previous,
                                                      CancellationToken cancellationToken)
    {
        var url = entry.Value<string>("DownloadLinkUpdate") ?? entry.Value<string>("DownloadLinkInstall")
                  ?? throw new InvalidDataException("仓库条目没有下载地址");

        pluginRoot.Create();
        var staging = new DirectoryInfo(Path.Combine(pluginRoot.FullName, $".staging-{Guid.NewGuid():N}"));
        var zipPath = staging.FullName + ".zip";

        try
        {
            using (var http = CreateHttp())
            await using (var source = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            ZipFile.ExtractToDirectory(zipPath, staging.FullName);

            if (!File.Exists(Path.Combine(staging.FullName, name + ".dll")))
                throw new InvalidDataException($"压缩包里没有 {name}.dll");

            // 本地清单: 仓库条目 + Dalamud 装插件时补的几项; WorkingPluginId 沿用旧版的, 合集里的开关是按它记的
            var workingId = previous is { WorkingPluginId.Length: > 0 } ? previous.WorkingPluginId : Guid.NewGuid().ToString();
            var manifest  = (JObject)entry.DeepClone();
            manifest["InstalledFromUrl"]     = repoUrl;
            manifest["WorkingPluginId"]      = workingId;
            manifest["Disabled"]             = false;
            manifest["Testing"]              = false;
            manifest["ScheduledForDeletion"] = false;
            manifest["IsThirdParty"]         = true;
            File.WriteAllText(Path.Combine(staging.FullName, name + ".json"), manifest.ToString(Formatting.Indented), new UTF8Encoding(false));

            var target = new DirectoryInfo(Path.Combine(pluginRoot.FullName, version.ToString()));

            // 同名目录里没有完整的 dll + 清单（不然上面就当已装了）, 是上次没装完的残留
            if (target.Exists)
                target.Delete(true);

            staging.MoveTo(target.FullName);
            Log.Information("[PluginEnsure] 装好 {Name} {Version} → {Dir}", name, version, target.FullName);
            return new Installed(target, version, workingId);
        }
        finally
        {
            TryDelete(() => File.Delete(zipPath));

            if (Directory.Exists(staging.FullName) && staging.FullName.Contains(".staging-", StringComparison.Ordinal))
                TryDelete(() => Directory.Delete(staging.FullName, true));
        }
    }

    /// <summary>
    ///     默认合集里按 WorkingPluginId 开启（没有就加一条）; 别的合集里关着它的条目也打开（Dalamud 只要有一个开着的合集要它就加载）。
    ///     返回有没有改动。
    /// </summary>
    private static bool EnableInProfiles(JObject config, string name, string workingId)
    {
        var changed = false;

        if (workingId.Length == 0)
            return false;

        if (config["DefaultProfile"] is JObject defaultProfile)
        {
            if (defaultProfile["Plugins"]?["$values"] is not JArray plugins)
            {
                plugins = new JArray();
                defaultProfile["Plugins"] = new JObject
                {
                    ["$type"]   = "System.Collections.Generic.List`1[[Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud]], System.Private.CoreLib",
                    ["$values"] = plugins,
                };
            }

            var mine = plugins.OfType<JObject>().FirstOrDefault(x => SameId(x, workingId));

            if (mine == null)
            {
                plugins.Add(new JObject
                {
                    ["$type"]           = PROFILE_PLUGIN_TYPE,
                    ["InternalName"]    = name,
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
        }

        if (config["SavedProfiles"]?["$values"] is JArray saved)
        {
            foreach (var profile in saved.OfType<JObject>().Where(p => p.Value<bool?>("e") == true))
            {
                if (profile["Plugins"]?["$values"] is not JArray plugins)
                    continue;

                foreach (var p in plugins.OfType<JObject>().Where(x => SameId(x, workingId) && x.Value<bool?>("IsEnabled") != true))
                {
                    p["IsEnabled"] = true;
                    changed        = true;
                }
            }
        }

        return changed;
    }

    private static bool SameId(JObject plugin, string workingId) =>
        string.Equals(plugin.Value<string>("WorkingPluginId"), workingId, StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttp()
    {
        var http = XLHttpClientFactory.Create(TimeSpan.FromSeconds(10), 50, DecompressionMethods.All);
        http.Timeout = TimeSpan.FromMinutes(2);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
        return http;
    }

    // 不让 Newtonsoft 把日期、$type 之类按自己的理解改写: 原样读、原样写
    private static JToken ParseJson(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
        return JToken.ReadFrom(reader);
    }

    private static JToken ReadJson(string path) => ParseJson(File.ReadAllText(path));

    /// <summary>Dalamud 写的就是 UTF-8 无 BOM、缩进两格、CRLF; 先写临时文件再整份换上, 读到的不会是写了一半的</summary>
    private static void WriteJsonAtomic(string path, JObject config)
    {
        var tmp = path + ".afan-tmp";
        File.WriteAllText(tmp, config.ToString(Formatting.Indented).Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[PluginEnsure] 清理临时文件失败");
        }
    }
}
