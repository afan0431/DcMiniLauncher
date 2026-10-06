// 移植自 goatcorp/FFXIVQuickLauncher（GPL-3.0）提交 a6f33a9:
//   src/XIVLauncher.Common/Dalamud/DalamudUpdater.cs（本体与 .NET 运行时）、AssetManager.cs（资源包）。
// 各方法注明原文件行号。相对原版的改动:
//   - 不做测试分支（betaKind / betaKey）, 只取 release;
//   - 状态与等待方式对齐本项目的 IDalamudUpdater（由 DalamudSession 使用）, 进度交给 IDalamudProgressSink;
//   - 多个无界面进程共用同一套目录, 更新时加跨进程锁（原版删运行时目录、清理旧版本都没有锁）;
//   - 全部状态都是实例字段, 目录全由构造函数给定: 与国服 DalamudUpdater 的静态字段和目录互不相干;
//   - HttpMessageHandler 可注入, 便于假 HTTP 单测。

using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud.International;

/// <summary>
///     <see cref="InternationalDalamudUpdater" /> 的可注入项
/// </summary>
public sealed record InternationalDalamudUpdaterOptions
{
    /// <summary>HTTP 处理器; 测试传假的</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>进度输出</summary>
    public IDalamudProgressSink? ProgressSink { get; init; }

    /// <summary>分桶（Canary / Control）; 不传时读 addon 目录里存的, 没存过就随机一次并存下</summary>
    public string? RolloutBucket { get; init; }

    /// <summary>最多试几次。出处: DalamudUpdater.cs:134（原版 10 次且不间隔）</summary>
    public int MaxTries { get; init; } = 10;

    /// <summary>两次尝试之间的间隔</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>删掉目录后等多久再往里写（原版固定 1 秒: DalamudUpdater.cs:533; AssetManager.cs:105）</summary>
    public TimeSpan DeleteSettleDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>是否有游戏在运行（有则不清理旧版本目录）; 不传时按进程名看</summary>
    public Func<bool>? IsGameOpen { get; init; }

    /// <summary>
    ///     等别的进程更新完, 单次最多等多久。缺省 45 分钟: 明显长于持锁方一次完整下载的上限（4 个文件, 每个请求最多 10 分钟）。
    ///     等到超时算本次尝试失败, 进入下一次尝试继续等; 绝不在别人还持锁时不加锁就进去 —— 那会删掉对方正在写的目录。
    /// </summary>
    public TimeSpan UpdateMutexTimeout { get; init; } = TimeSpan.FromMinutes(45);
}

/// <summary>
///     goatcorp 原版 Dalamud 的下载与更新: 版本信息 JSON + 本体 zip + .NET 运行时 zip + 资源包。
///     只给国际服用; 目录必须与国服 Dalamud 的目录完全分开（两边都会清理「不是自己当前版本」的目录）。
/// </summary>
public sealed class InternationalDalamudUpdater : IDalamudUpdater, IDisposable
{
    private const string UPDATE_MUTEX_PREFIX = "DcMiniLauncher-IntlDalamudUpdate";

    private readonly DirectoryInfo                      addonDirectory;
    private readonly DirectoryInfo                      assetRootDirectory;
    private readonly HttpClient                         client;
    private readonly InternationalDalamudUpdaterOptions options;
    private readonly object                             runLock = new();

    private Task? updateTask;

    /// <summary>
    ///     创建更新器
    /// </summary>
    /// <param name="addonDirectory">本体根目录（其下建 Hooks\&lt;版本&gt;）</param>
    /// <param name="runtimeDirectory">.NET 运行时目录（更新运行时会先整个删掉重建, 不能与别的东西共用）</param>
    /// <param name="assetRootDirectory">资源根目录</param>
    /// <param name="options">可注入项</param>
    public InternationalDalamudUpdater
    (
        DirectoryInfo                       addonDirectory,
        DirectoryInfo                       runtimeDirectory,
        DirectoryInfo                       assetRootDirectory,
        InternationalDalamudUpdaterOptions? options = null
    )
    {
        this.addonDirectory     = addonDirectory;
        this.assetRootDirectory = assetRootDirectory;
        this.options            = options ?? new InternationalDalamudUpdaterOptions();
        Runtime                 = runtimeDirectory;

        client = this.options.HttpHandler == null ? new HttpClient() : new HttpClient(this.options.HttpHandler, false);
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
    }

    /// <inheritdoc />
    public DalamudUpdater.DownloadState State { get; private set; } = DalamudUpdater.DownloadState.Unknown;

    /// <inheritdoc />
    public Exception? EnsurementException { get; private set; }

    /// <inheritdoc />
    public FileInfo? Runner { get; private set; }

    /// <inheritdoc />
    public DirectoryInfo Runtime { get; }

    /// <inheritdoc />
    public DirectoryInfo? AssetDirectory { get; private set; }

    /// <summary>服务器实际给出的版本信息; 更新成功后才有</summary>
    public InternationalDalamudVersionInfo? ResolvedBranch { get; private set; }

    /// <inheritdoc />
    public void Dispose() =>
        client.Dispose();

    /// <summary>
    ///     开始更新（已在更新或已完成则不重复）。出处: DalamudUpdater.cs:125-155
    /// </summary>
    public void Run()
    {
        lock (runLock)
        {
            if (updateTask is { IsCompleted: false } || State == DalamudUpdater.DownloadState.Done)
                return;

            Log.Information("[DUPDATE-INTL] 开始更新国际服 Dalamud...");
            EnsurementException = null;
            State               = DalamudUpdater.DownloadState.Unknown;
            ResolvedBranch      = null;
            updateTask          = Task.Run(RunCoreAsync);
        }
    }

    /// <inheritdoc />
    public void WaitForCompletion()
    {
        Task? current;

        lock (runLock)
            current = updateTask;

        current?.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void ShowLoading() =>
        options.ProgressSink?.ShowLoading();

    /// <inheritdoc />
    public void HideLoading() =>
        options.ProgressSink?.HideLoading();

    /// <summary>
    ///     这版 Dalamud 支持的游戏版本是否就是本地的游戏版本; 还没更新完返回 null。出处: DalamudUpdater.cs:157-169
    /// </summary>
    public bool? ReCheckVersion(DirectoryInfo gamePath)
    {
        if (State != DalamudUpdater.DownloadState.Done || Runner?.DirectoryName == null)
            return null;

        var info = InternationalDalamudVersionInfo.Load(new FileInfo(Path.Combine(Runner.DirectoryName, "version.json")));

        if (info == null)
            return null;

        return Repository.Ffxiv.GetVer(gamePath) == info.SupportedGameVer;
    }

    private async Task RunCoreAsync()
    {
        var isUpdated = false;

        for (var tries = 0; tries < options.MaxTries; tries++)
        {
            try
            {
                // 多个无界面进程共用这套目录, 同一时刻只允许一个在更新。拿到锁之后会重新校验, 所以等待方通常什么都不用再下
                using (await AcquireUpdateLockAsync().ConfigureAwait(false))
                    await UpdateDalamudAsync().ConfigureAwait(false);

                isUpdated = true;
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE-INTL] 更新失败, 第 {TryCnt}/{MaxTries} 次", tries + 1, options.MaxTries);
                EnsurementException = ex;

                if (tries + 1 < options.MaxTries && options.RetryDelay > TimeSpan.Zero)
                    await Task.Delay(options.RetryDelay).ConfigureAwait(false);
            }
        }

        State = isUpdated ? DalamudUpdater.DownloadState.Done : DalamudUpdater.DownloadState.NoIntegrity;
    }

    /// <summary>
    ///     拿更新锁。等到超时 = 别的进程还在更新: 抛 <see cref="TimeoutException" />, 算本次尝试失败（由外层重试继续等）, 不降级成无锁。
    ///     互斥量本身打不开（如被提权进程创建、无权访问）时等多久都没用, 只有这种情况才不加锁继续。
    /// </summary>
    private async Task<CrossProcessMutex?> AcquireUpdateLockAsync()
    {
        var name = CrossProcessMutex.NameForPath(UPDATE_MUTEX_PREFIX, addonDirectory.FullName);

        try
        {
            return await CrossProcessMutex.AcquireAsync(name, options.UpdateMutexTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"等了 {options.UpdateMutexTimeout.TotalMinutes:0.#} 分钟, 别的 DcMiniLauncher 进程还在更新国际服 Dalamud");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DUPDATE-INTL] 打不开更新锁 {Name}, 不加锁继续", name);
            return null;
        }
    }

    /// <summary>出处: DalamudUpdater.cs:174-198（只取 release）</summary>
    private async Task<InternationalDalamudVersionInfo> GetVersionInfoAsync()
    {
        using var message = new HttpRequestMessage
        (
            HttpMethod.Get,
            string.Format(CultureInfo.InvariantCulture, Links.GOATCORP_DALAMUD_VERSION_INFO_URL_FORMAT, ResolveRolloutBucket())
        );
        message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await client.SendAsync(message).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var info = JsonConvert.DeserializeObject<InternationalDalamudVersionInfo>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        if (info == null ||
            string.IsNullOrWhiteSpace(info.AssemblyVersion) ||
            string.IsNullOrWhiteSpace(info.DownloadUrl) ||
            info.AssemblyVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            info.AssemblyVersion is "." or ".." or "dev")
            throw new DalamudIntegrityException("Dalamud 版本信息不完整");

        if (info.RuntimeRequired &&
            (string.IsNullOrWhiteSpace(info.RuntimeVersion) || info.RuntimeVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || info.RuntimeVersion is "." or ".."))
            throw new DalamudIntegrityException("Dalamud 版本信息里的运行时版本无效");

        return info;
    }

    /// <summary>出处: DalamudUpdater.cs:200-326</summary>
    private async Task UpdateDalamudAsync()
    {
        var remoteVersionInfo = await GetVersionInfoAsync().ConfigureAwait(false);

        Log.Information("[DUPDATE-INTL] 使用 release 版本 ({Hash})", remoteVersionInfo.AssemblyVersion);

        var versionInfoJson = JsonConvert.SerializeObject(remoteVersionInfo);

        var addonPath          = new DirectoryInfo(Path.Combine(addonDirectory.FullName, "Hooks"));
        var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName, remoteVersionInfo.AssemblyVersion));

        if (!currentVersionPath.Exists || !IsIntegrity(currentVersionPath))
        {
            Log.Information("[DUPDATE-INTL] 本体不存在或校验不过, 重新下载");
            SetLoadingMessage("正在下载国际服 Dalamud");

            try
            {
                await DownloadDalamudAsync(currentVersionPath, remoteVersionInfo).ConfigureAwait(false);
                CleanUpOld(addonPath, remoteVersionInfo.AssemblyVersion);
            }
            catch (Exception ex)
            {
                throw new DalamudIntegrityException("Could not download Dalamud", ex);
            }
        }

        if (remoteVersionInfo.RuntimeRequired)
            await EnsureRuntimeAsync(remoteVersionInfo.RuntimeVersion).ConfigureAwait(false);

        int assetVersion;

        try
        {
            SetLoadingMessage("正在检查国际服 Dalamud 资源");
            (AssetDirectory, assetVersion) = await EnsureAssetsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new DalamudIntegrityException("Could not ensure assets", ex);
        }

        currentVersionPath.Refresh();

        if (!IsIntegrity(currentVersionPath))
            throw new DalamudIntegrityException("No integrity after ensurement");

        File.WriteAllText(Path.Combine(currentVersionPath.FullName, "version.json"), versionInfoJson);

        Log.Information
        (
            "[DUPDATE-INTL] 就绪: 游戏 {GameVersion}, Dalamud {DalamudVersion}（运行时 {RuntimeVersion}, 资源 {AssetVersion}）",
            remoteVersionInfo.SupportedGameVer,
            remoteVersionInfo.AssemblyVersion,
            remoteVersionInfo.RuntimeVersion,
            assetVersion
        );

        ResolvedBranch = remoteVersionInfo;
        Runner         = new FileInfo(Path.Combine(currentVersionPath.FullName, "Dalamud.Injector.exe"));
    }

    #region 本体

    private static bool CanRead(FileInfo info)
    {
        try
        {
            using var stream = info.OpenRead();
            stream.ReadByte();
        }
        catch
        {
            return false;
        }

        return true;
    }

    /// <summary>出处: DalamudUpdater.cs:343-372</summary>
    private static bool IsIntegrity(DirectoryInfo addonPath)
    {
        try
        {
            var files = addonPath.GetFiles();

            if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe")) ||
                !CanRead(files.First(x => x.Name == "Dalamud.dll")) ||
                !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
            {
                Log.Error("[DUPDATE-INTL] 本体文件打不开");
                return false;
            }

            var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");

            if (!File.Exists(hashesPath))
            {
                Log.Error("[DUPDATE-INTL] 没有 hashes.json");
                return false;
            }

            return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE-INTL] 本体校验不过");
            return false;
        }
    }

    /// <summary>按「相对路径 → 大写十六进制 MD5」清单逐个核对。出处: DalamudUpdater.cs:374-411</summary>
    private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
    {
        try
        {
            var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashesJson) ?? throw new InvalidDataException("Hashes deserialized to null");

            foreach (var hash in hashes)
            {
                var file = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));

                using var fileStream = File.OpenRead(file);
                var       hashed     = Convert.ToHexString(MD5.HashData(fileStream));

                if (hashed != hash.Value)
                {
                    Log.Error("[DUPDATE-INTL] 校验不过 {File}（应为 {Expected}, 实为 {Actual}）", file, hash.Value, hashed);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE-INTL] 校验失败");
            return false;
        }

        return true;
    }

    /// <summary>删掉 Hooks 下除 dev 和当前版本以外的目录; 有游戏在跑时不删。出处: DalamudUpdater.cs:413-434</summary>
    private void CleanUpOld(DirectoryInfo addonPath, string currentVersion)
    {
        if (IsGameOpen())
            return;

        if (!addonPath.Exists)
            return;

        foreach (var directory in addonPath.GetDirectories())
        {
            if (directory.Name == "dev" || directory.Name == currentVersion)
                continue;

            try
            {
                directory.Delete(true);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>出处: DalamudUpdater.cs:441-470</summary>
    private async Task DownloadDalamudAsync(DirectoryInfo addonPath, InternationalDalamudVersionInfo version)
    {
        if (addonPath.Exists)
            addonPath.Delete(true);

        addonPath.Create();

        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            await DownloadFileAsync(version.DownloadUrl, downloadPath).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(downloadPath, addonPath.FullName);
        }
        finally
        {
            TryDelete(downloadPath);
        }

        try
        {
            var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));

            PlatformHelpers.DeleteAndRecreateDirectory(devPath);
            PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE-INTL] 复制到 dev 目录失败");
        }
    }

    #endregion

    #region 运行时

    /// <summary>出处: DalamudUpdater.cs:251-295</summary>
    private async Task EnsureRuntimeAsync(string runtimeVersion)
    {
        Log.Information("[DUPDATE-INTL] 检查 .NET 运行时 {Version}", runtimeVersion);

        DirectoryInfo[] runtimePaths =
        [
            new(Path.Combine(Runtime.FullName, "host", "fxr", runtimeVersion)),
            new(Path.Combine(Runtime.FullName, "shared", "Microsoft.NETCore.App", runtimeVersion)),
            new(Path.Combine(Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", runtimeVersion))
        ];

        var versionFile  = new FileInfo(Path.Combine(Runtime.FullName, "version"));
        var localVersion = GetLocalRuntimeVersion(versionFile);

        var runtimeNeedsUpdate = localVersion != runtimeVersion;

        if (!Runtime.Exists)
            Directory.CreateDirectory(Runtime.FullName);

        var isRuntimeIntegrity = false;

        // 版本对得上才核对哈希
        if (!runtimeNeedsUpdate)
        {
            try
            {
                isRuntimeIntegrity = await CheckRuntimeHashesAsync(Runtime, localVersion).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE-INTL] 核对运行时哈希失败");
            }
        }

        if (!runtimePaths.Any(p => !p.Exists) && !runtimeNeedsUpdate && isRuntimeIntegrity)
            return;

        Log.Information("[DUPDATE-INTL] 运行时不存在、版本不符或校验不过: 本地 {LocalVer}, 需要 {RemoteVer}", localVersion, runtimeVersion);
        SetLoadingMessage("正在下载国际服 Dalamud 的 .NET 运行时");

        try
        {
            await DownloadRuntimeAsync(Runtime, runtimeVersion).ConfigureAwait(false);
            File.WriteAllText(versionFile.FullName, runtimeVersion);
        }
        catch (Exception ex)
        {
            throw new DalamudIntegrityException("Could not ensure runtime", ex);
        }
    }

    /// <summary>出处: DalamudUpdater.cs:472-488</summary>
    private static string GetLocalRuntimeVersion(FileInfo versionFile)
    {
        // 原版最早发的版本没写版本文件, 读不到时按它算
        var localVersion = "5.0.6";

        try
        {
            if (versionFile.Exists)
                localVersion = File.ReadAllText(versionFile.FullName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE-INTL] 读本地运行时版本失败");
        }

        return localVersion;
    }

    /// <summary>出处: DalamudUpdater.cs:490-517</summary>
    private async Task<bool> CheckRuntimeHashesAsync(DirectoryInfo runtimePath, string version)
    {
        var hashesFile = new FileInfo(Path.Combine(runtimePath.FullName, $"hashes-{version}.json"));
        string runtimeHashes;

        if (!hashesFile.Exists)
        {
            try
            {
                runtimeHashes = await client.GetStringAsync(string.Format(CultureInfo.InvariantCulture, Links.GOATCORP_DALAMUD_RUNTIME_HASHES_URL_FORMAT, version))
                                            .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE-INTL] 下载运行时 {Version} 的哈希清单失败", version);
                return false;
            }

            File.WriteAllText(hashesFile.FullName, runtimeHashes);
        }
        else
            runtimeHashes = File.ReadAllText(hashesFile.FullName);

        return CheckIntegrity(runtimePath, runtimeHashes);
    }

    /// <summary>出处: DalamudUpdater.cs:519-550。会先把整个运行时目录删掉重建</summary>
    private async Task DownloadRuntimeAsync(DirectoryInfo runtimePath, string version)
    {
        if (runtimePath.Exists)
            runtimePath.Delete(true);

        runtimePath.Create();

        if (options.DeleteSettleDelay > TimeSpan.Zero)
            await Task.Delay(options.DeleteSettleDelay).ConfigureAwait(false);

        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            await DownloadFileAsync(string.Format(CultureInfo.InvariantCulture, Links.GOATCORP_DALAMUD_RUNTIME_DOTNET_URL_FORMAT, version), downloadPath)
                .ConfigureAwait(false);
            ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);

            await DownloadFileAsync(string.Format(CultureInfo.InvariantCulture, Links.GOATCORP_DALAMUD_RUNTIME_DESKTOP_URL_FORMAT, version), downloadPath)
                .ConfigureAwait(false);
            ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);
        }
        finally
        {
            TryDelete(downloadPath);
        }
    }

    #endregion

    #region 资源（AssetManager.cs）

    private sealed class AssetInfo
    {
        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("assets")]
        public List<Asset>? Assets { get; set; }

        [JsonProperty("packageUrl")]
        public string? PackageUrl { get; set; }

        public sealed class Asset
        {
            [JsonProperty("fileName")]
            public string? FileName { get; set; }

            [JsonProperty("hash")]
            public string? Hash { get; set; }
        }
    }

    /// <summary>出处: AssetManager.cs:45-150, 164-189</summary>
    private async Task<(DirectoryInfo AssetDir, int Version)> EnsureAssetsAsync()
    {
        var localVerFile = Path.Combine(assetRootDirectory.FullName, "asset.ver");
        var localVer     = 0;

        try
        {
            if (File.Exists(localVerFile))
                localVer = int.Parse(File.ReadAllText(localVerFile), CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // 读不出就按 0, 会重新下载全部资源
            Log.Error(ex, "[DASSET-INTL] 读 asset.ver 失败");
        }

        using var metaRequest = new HttpRequestMessage(HttpMethod.Get, Links.GOATCORP_DALAMUD_ASSET_META_URL);
        metaRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var metaResponse = await client.SendAsync(metaRequest).ConfigureAwait(false);
        metaResponse.EnsureSuccessStatusCode();

        var info = JsonConvert.DeserializeObject<AssetInfo>(await metaResponse.Content.ReadAsStringAsync().ConfigureAwait(false))
                   ?? throw new InvalidDataException("Could not get remote asset info");

        if (info.Version <= 0 || string.IsNullOrWhiteSpace(info.PackageUrl))
            throw new InvalidDataException("Remote asset info is incomplete");

        var isRefreshNeeded = info.Version > localVer;

        var currentDir = new DirectoryInfo(Path.Combine(assetRootDirectory.FullName, info.Version.ToString(CultureInfo.InvariantCulture)));
        var devDir     = new DirectoryInfo(Path.Combine(assetRootDirectory.FullName, "dev"));

        // 版本没变: 逐个核对文件在不在、SHA1 对不对
        if (!isRefreshNeeded)
        {
            foreach (var entry in info.Assets ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.FileName))
                    continue;

                var filePath = Path.Combine(currentDir.FullName, entry.FileName);

                if (!File.Exists(filePath))
                {
                    Log.Error("[DASSET-INTL] 本地没有 {File}", entry.FileName);
                    isRefreshNeeded = true;
                    break;
                }

                if (string.IsNullOrEmpty(entry.Hash))
                    continue;

                try
                {
                    using var file       = File.OpenRead(filePath);
                    var       stringHash = Convert.ToHexString(SHA1.HashData(file));

                    if (stringHash != entry.Hash)
                    {
                        Log.Error("[DASSET-INTL] {File} 的哈希是 {Local}, 应为 {Remote}", entry.FileName, stringHash, entry.Hash);
                        isRefreshNeeded = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DASSET-INTL] 读资源文件失败");
                    isRefreshNeeded = true;
                    break;
                }
            }
        }

        if (isRefreshNeeded)
        {
            SetLoadingMessage("正在下载国际服 Dalamud 资源");
            PlatformHelpers.DeleteAndRecreateDirectory(currentDir);

            if (options.DeleteSettleDelay > TimeSpan.Zero)
                await Task.Delay(options.DeleteSettleDelay).ConfigureAwait(false);

            var tempPath = PlatformHelpers.GetTempFileName();

            try
            {
                await DownloadFileAsync(info.PackageUrl, tempPath).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(tempPath, currentDir.FullName);
            }
            finally
            {
                TryDelete(tempPath);
            }

            try
            {
                PlatformHelpers.DeleteAndRecreateDirectory(devDir);
                PlatformHelpers.CopyFilesRecursively(currentDir, devDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET-INTL] 复制到 dev 目录失败");
            }

            try
            {
                File.WriteAllText(localVerFile, info.Version.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET-INTL] 写 asset.ver 失败");
            }
        }

        try
        {
            CleanUpOldAssets(devDir, currentDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DASSET-INTL] 清理旧资源失败");
        }

        return (currentDir, info.Version);
    }

    /// <summary>出处: AssetManager.cs:204-222</summary>
    private void CleanUpOldAssets(DirectoryInfo devDir, DirectoryInfo currentDir)
    {
        if (IsGameOpen())
            return;

        if (!assetRootDirectory.Exists)
            return;

        foreach (var toDelete in assetRootDirectory.GetDirectories())
        {
            if (toDelete.Name != devDir.Name && toDelete.Name != currentDir.Name)
                toDelete.Delete(true);
        }
    }

    #endregion

    #region 公共

    private bool IsGameOpen() =>
        options.IsGameOpen?.Invoke() ?? GameHelpers.CheckIsGameOpen();

    private void SetLoadingMessage(string message) =>
        options.ProgressSink?.SetLoadingMessage(message);

    /// <summary>
    ///     分桶: 没存过就随机一次（三成 Canary）并存在 addon 目录里, 之后一直用它。出处: DalamudUpdater.cs:98-102; goatcorp App.xaml.cs:203, 212
    /// </summary>
    private string ResolveRolloutBucket()
    {
        if (!string.IsNullOrWhiteSpace(options.RolloutBucket))
            return options.RolloutBucket;

        var bucketFile = Path.Combine(addonDirectory.FullName, "rolloutBucket");

        try
        {
            if (File.Exists(bucketFile) && File.ReadAllText(bucketFile).Trim() is "Canary" or "Control")
                return File.ReadAllText(bucketFile).Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DUPDATE-INTL] 读分桶失败");
        }

        var bucket = new Random().Next(0, 9) >= 7 ? "Canary" : "Control";

        try
        {
            Directory.CreateDirectory(addonDirectory.FullName);
            File.WriteAllText(bucketFile, bucket);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DUPDATE-INTL] 保存分桶失败");
        }

        return bucket;
    }

    private async Task DownloadFileAsync(string url, string path)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var  buffer     = new byte[81920];
        long downloaded = 0;
        int  read;

        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            downloaded += read;
            options.ProgressSink?.ReportLoadingProgress(total, downloaded, total is > 0 ? downloaded * 100d / total.Value : null);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    #endregion
}
