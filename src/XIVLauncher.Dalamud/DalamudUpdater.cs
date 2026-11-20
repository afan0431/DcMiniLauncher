using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Http;
using XIVLauncher.Common.Network;
using XIVLauncher.Common.Runtime;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud;

public class DalamudUpdater : IDalamudUpdater
{
    public DirectoryInfo Runtime { get; }

    private DateTime? lastCompletionUtc;
    private Task?     updateTask;

    public DownloadState State
    {
        get;
        private set
        {
            field = value;

            if (value == DownloadState.Done)
                lastCompletionUtc = DateTime.UtcNow;

            NotifyStatusChanged();
        }
    } = DownloadState.Unknown;

    public Exception?                    EnsurementException { get; private set; }
    public FileInfo?                     RunnerOverride      { get; set; }
    public DirectoryInfo?                AssetDirectory      { get; private set; }
    public string                        LoadingDetail       { get; private set; } = string.Empty;
    public long?                         LoadingTotal        { get; private set; }
    public long                          LoadingDownloaded   { get; private set; }
    public double?                       LoadingProgress     { get; private set; }
    public IDalamudProgressSink?         ProgressSink        { get; set; }
    public event Action<DalamudUpdater>? StatusChanged;
    public static string                 OnlineHash { get; private set; } = string.Empty;
    public static string                 Version    { get; private set; } = string.Empty;

    public FileInfo? Runner
    {
        get => RunnerOverride ?? field;
        private set;
    }

    private static string runtimeVersion = string.Empty;

    /// <summary>Version / OnlineHash / runtimeVersion 是发行源刚给的（而不是从本地记录里读的）</summary>
    private static bool versionInfoFromRemote;

    /// <summary>
    ///     这次没连上发行源, 用的是发行源以前确认过的本地版本（<see cref="DalamudConfirmedLocal" />）。
    ///     起游戏前要由 <see cref="DalamudSession" /> 核对游戏版本没变
    /// </summary>
    public bool UsingConfirmedLocal { get; private set; }

    /// <summary>本地版本被确认时游戏是哪一版; 只在 <see cref="UsingConfirmedLocal" /> 时有意义</summary>
    public string? ConfirmedGameVersion { get; private set; }

    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteTimeUtcTicks, string Hash)> CachedFileHashes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DirectoryInfo addonDirectory;

    /// <summary>等别的进程更新完 addon 目录的上限</summary>
    private static readonly TimeSpan UpdateMutexTimeout = TimeSpan.FromMinutes(5);

    private DirectoryInfo HooksDirectory => new(Path.Combine(addonDirectory.FullName, "Hooks"));

    private string UpdateMutexName => CrossProcessMutex.NameForPath("DcMiniLauncher-DalamudUpdate", addonDirectory.FullName);
    private readonly DirectoryInfo assetDirectory;

    private readonly HttpClient                 httpClient;
    private readonly INetworkEnvironmentService networkEnvironmentService;

    private string releaseBaseURL = Links.DALAMUD_DISTRIBUTE_R2_BASE_URL;

    public DalamudUpdater
    (
        DirectoryInfo              addonDirectory,
        DirectoryInfo              runtimeDirectory,
        DirectoryInfo              assetDirectory,
        INetworkEnvironmentService? networkEnvironmentService = null,
        HttpClient?                 httpClient                 = null
    )
    {
        this.addonDirectory            = addonDirectory;
        Runtime                        = runtimeDirectory;
        this.assetDirectory            = assetDirectory;
        this.networkEnvironmentService = networkEnvironmentService ?? NetworkEnvironmentService.Shared;
        this.httpClient                = httpClient ?? XLHttpClientFactory.Create(TimeSpan.FromSeconds(10), 50, DecompressionMethods.All);

        if (httpClient == null)
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
    }

    public void Run() =>
        Run(false);

    public void Run(bool refreshVersionInfo)
    {
        if (State == DownloadState.Done && !refreshVersionInfo)
        {
            if (lastCompletionUtc is { } lastCompletion && DateTime.UtcNow - lastCompletion < TimeSpan.FromHours(24))
            {
                Log.Information("[DUPDATE] Dalamud 更新已完成，跳过重复检查");
                return;
            }

            Log.Information
            (
                UsingConfirmedLocal ? "[DUPDATE] 上次没连上发行源（用的本地版本），将重新检查" : "[DUPDATE] Dalamud 上次更新已超过 24 小时，将重新检查"
            );
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentTask = Volatile.Read(ref updateTask);
        if (currentTask is { IsCompleted: false } || Interlocked.CompareExchange(ref updateTask, completion.Task, currentTask) != currentTask)
        {
            Log.Information("[DUPDATE] Dalamud 更新器已在运行, 复用当前任务");
            return;
        }

        Log.Information("[DUPDATE] 启动 Dalamud 更新器中...");
        EnsurementException = null;
        UsingConfirmedLocal = false;
        LoadingDetail       = string.Empty;
        LoadingTotal        = null;
        LoadingDownloaded   = 0;
        LoadingProgress     = null;
        State               = DownloadState.Unknown;

        Task.Run
        (async () =>
            {
                try
                {
                    const int MAX_TRIES = 3;

                    var isUpdated = false;

                    for (var tries = 0; tries < MAX_TRIES; tries++)
                    {
                        try
                        {
                            // 多个启动器进程（界面版、Cat 的无界面启动）共用同一个 addon 目录, 同一时刻只允许一个在更新
                            // 拿不到锁（超时、无权打开）时不加锁照常更新, 不让它成为失败原因
                            using (await CrossProcessMutex.TryAcquireAsync(UpdateMutexName, UpdateMutexTimeout).ConfigureAwait(false))
                                await UpdateDalamud(refreshVersionInfo).ConfigureAwait(false);

                            isUpdated = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[DUPDATE] 更新失败, 重试 {TryCnt}/{MaxTries}...", tries + 1, MAX_TRIES);
                            EnsurementException = ex;

                            if (tries + 1 < MAX_TRIES)
                                await Task.Delay(TimeSpan.FromSeconds(tries + 1)).ConfigureAwait(false);
                        }
                    }

                    if (!isUpdated && TryUseConfirmedLocal())
                    {
                        State = DownloadState.Done;
                        // 没连上发行源不算「更新完成」: 下次 Run 照样去问
                        lastCompletionUtc = null;
                        return;
                    }

                    State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
                }
                finally
                {
                    completion.TrySetResult();
                }
            }
        );
    }

    public void WaitForCompletion() =>
        Volatile.Read(ref updateTask)?.GetAwaiter().GetResult();

    #region Steps

    private async Task UpdateDalamud(bool refreshVersionInfo)
    {
        try
        {
            Log.Information("[DUPDATE] 开始 Dalamud 更新进程");

            await InitVersionInfoAsync(refreshVersionInfo);
            var paths                  = PreparePaths();
            var currentVersionVerified = await UpdateDalamudCoreAsync(paths.addonPath, paths.currentVersionPath);
            await UpdateRuntimeAsync();
            await UpdateAssetsAsync();

            if (!currentVersionVerified && !CheckDalamudIntegrity(paths.currentVersionPath))
                throw new DalamudIntegrityException("完整性验证最终失败");

            Runner = new FileInfo(Path.Combine(paths.currentVersionPath.FullName, "Dalamud.Injector.exe"));

            if (versionInfoFromRemote)
                DalamudConfirmedLocal.Confirm(HooksDirectory, Version, OnlineHash, runtimeVersion);

            SetLoadingDetail("正在启动...");
            ReportLoadingProgressCore(null, 0, null);

            Log.Information("[DUPDATE] Dalamud {Version} ({OnlineHash}) 准备完毕", Version, OnlineHash);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 更新 Dalamud 过程中发生错误");
            throw;
        }
    }

    private async Task InitVersionInfoAsync(bool refreshVersionInfo)
    {
        Log.Verbose("[DUPDATE] 开始检查版本信息");

        if (!refreshVersionInfo && versionInfoFromRemote && !string.IsNullOrWhiteSpace(OnlineHash) && !string.IsNullOrWhiteSpace(Version))
        {
            Log.Verbose("[DUPDATE] 版本信息已存在: {Version} ({Hash})", Version, OnlineHash);
            return;
        }

        Log.Information("[DUPDATE] 正在获取最新版本信息");
        await GetDalamudVersionInfoAsync();
        Log.Information("[DUPDATE] 获取到版本: {Version} ({Hash})", Version, OnlineHash);
    }

    private (DirectoryInfo addonPath, DirectoryInfo currentVersionPath) PreparePaths()
    {
        Log.Verbose("[DUPDATE] 开始准备路径信息");

        var addonPath          = new DirectoryInfo(Path.Combine(addonDirectory.FullName, "Hooks"));
        var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName,      Version));

        Log.Verbose
        (
            "[DUPDATE] 路径信息: 版本路径={Path}",
            currentVersionPath.FullName
        );

        return (addonPath, currentVersionPath);
    }

    private async Task<bool> UpdateDalamudCoreAsync(DirectoryInfo addonPath, DirectoryInfo currentVersionPath)
    {
        Log.Information("[DUPDATE] 开始检查 Dalamud 本体完整性");

        if (currentVersionPath.Exists && CheckDalamudIntegrity(currentVersionPath))
        {
            Log.Information("[DUPDATE] Dalamud 本体完整性检查已通过，无需更新");
            return true;
        }

        Log.Information("[DUPDATE] Dalamud 本体完整性检查未通过, 开始更新");
        SetLoadingDetail("正在更新核心...");

        try
        {
            Log.Information("[DUPDATE] 开始下载 Dalamud 本体");
            await DownloadDalamud(currentVersionPath).ConfigureAwait(true);

            Log.Information("[DUPDATE] 清理旧版本 Dalamud 文件");
            CleanUpOld(addonPath, Version);

            Log.Information("[DUPDATE] Dalamud 本体更新完成");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 下载 Dalamud 本体失败");
            throw new DalamudIntegrityException("下载 Dalamud 失败", ex);
        }
    }

    private async Task UpdateRuntimeAsync() =>
        await DotNetRuntimeManager.EnsureRuntimeAsync
        (
            Runtime,
            runtimeVersion,
            "win-x64",
            ".NET 运行时",
            SetLoadingDetail,
            ReportLoadingProgressCore
        ).ConfigureAwait(false);

    private async Task UpdateAssetsAsync()
    {
        Log.Information("[DUPDATE] 开始验证资源文件完整性");

        SetLoadingDetail("正在更新资源文件...");
        ReportLoadingProgressCore(null, 0, null);

        try
        {
            var assetResult = await DalamudAssetManager.EnsureAssets(this, assetDirectory, networkEnvironmentService).ConfigureAwait(true);
            AssetDirectory = assetResult.AssetDir;
            Log.Information("[DUPDATE] 资源文件验证完成: {Path}", AssetDirectory.FullName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 资源文件验证失败");
            throw new DalamudIntegrityException("资源文件验证失败", ex);
        }
    }

    #endregion

    #region Dalamud

    private static void RefreshDalamudDevCache(DirectoryInfo addonPath, DirectoryInfo devPath)
    {
        try
        {
            PlatformHelpers.DeleteAndRecreateDirectory(devPath);
            PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 复制到 dev 目录失败");
        }
    }

    public static bool CheckDalamudIntegrity(DirectoryInfo addonPath) =>
        CheckDalamudIntegrity(addonPath, OnlineHash);

    /// <param name="addonPath">版本目录</param>
    /// <param name="expectedHash">hashes.json 应有的 MD5; 空串时不比对, 只按 hashes.json 逐个核文件</param>
    public static bool CheckDalamudIntegrity(DirectoryInfo addonPath, string expectedHash)
    {
        try
        {
            var injector   = new FileInfo(Path.Combine(addonPath.FullName, "Dalamud.Injector.exe"));
            var dalamud    = new FileInfo(Path.Combine(addonPath.FullName, "Dalamud.dll"));
            var imGuiScene = new FileInfo(Path.Combine(addonPath.FullName, "ImGuiScene.dll"));

            if (!injector.Exists || !dalamud.Exists || !imGuiScene.Exists)
            {
                Log.Error("[DUPDATE] 缺少核心文件");
                return false;
            }

            if (!CanRead(injector) || !CanRead(dalamud) || !CanRead(imGuiScene))
            {
                Log.Error("[DUPDATE] 无法打开核心文件");
                return false;
            }

            var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");

            if (!File.Exists(hashesPath))
            {
                Log.Error("[DUPDATE] 无 hashes.json");
                return false;
            }

            if (!string.IsNullOrEmpty(expectedHash))
            {
                var hashHash = ComputeFileHash(hashesPath);

                if (expectedHash != hashHash)
                {
                    Log.Error($"[UPDATE] hashes.json 哈希比对不一致, 本地: {hashHash}, 应为: {expectedHash}");
                    return false;
                }
            }

            return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 无 Dalamud 完整性");
            return false;
        }
    }

    public async Task GetDalamudVersionInfoAsync()
    {
        versionInfoFromRemote = false;

        var networkEnvironmentTask = networkEnvironmentService.GetCurrentAsync();
        runtimeVersion = await DotNetRuntimeManager.GetLatestVersionAsync(httpClient).ConfigureAwait(false);

        Log.Information("[DUPDATE] 获取到远端 Dalamud 运行时版本: {0}", runtimeVersion);

        var networkEnvironment = await networkEnvironmentTask.ConfigureAwait(false);
        var versionURL         = Links.DALAMUD_DISTRIBUTE_R2_VERSION_URL;
        releaseBaseURL = Links.DALAMUD_DISTRIBUTE_R2_BASE_URL;

        Log.Information
        (
            "[DUPDATE] 网络区域 {Region}, 使用 Dalamud 发行源 {ReleaseBaseURL}",
            networkEnvironment.Region,
            releaseBaseURL
        );

        var releaseText = await httpClient.GetStringAsync(versionURL).ConfigureAwait(false);
        var version     = releaseText.Trim();

        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException($"[DUPDATE] 发行源返回空版本信息: {versionURL}");
        Version = version;
        Log.Information("[DUPDATE] 获取到发行版本: {Version}", Version);

        var hashesURL    = GetReleaseAssetURL("hashes.json");
        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            using (var fileResponse = await httpClient.GetAsync(hashesURL, HttpCompletionOption.ResponseHeadersRead))
            {
                await fileResponse.EnsureSuccessWithDiagnosticsAsync().ConfigureAwait(false);
                await using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await fileResponse.Content.CopyToAsync(fileStream);
            }

            var hash = ComputeFileHash(downloadPath);

            Log.Information($"[DUPDATE] 获取到远端 Dalamud 哈希: {hash}");
            OnlineHash            = hash;
            versionInfoFromRemote = true;
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    private async Task DownloadDalamud(DirectoryInfo addonPath)
    {
        try
        {
            var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));

            if (!addonPath.Exists)
                addonPath.Create();

            var downloadURL = GetReleaseAssetURL("latest.7z");
            Log.Information("[DUPDATE] 下载完整包: {URL}", downloadURL);
            await DownloadDalamudPackage(addonPath, downloadURL).ConfigureAwait(false);

            CachedFileHashes.Clear();
            RefreshDalamudDevCache(addonPath, devPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 下载过程中发生错误");
            throw;
        }
    }

    /// <summary>
    ///     更新反复失败后: 发行源以前确认过的本地版本若完好、且没有迹象说明它过时（发行源这次没说出更新的版本）, 就用它。
    ///     游戏版本核对在 <see cref="DalamudSession.EnsureReady" /> 里做, 这里只看本地文件
    /// </summary>
    private bool TryUseConfirmedLocal()
    {
        var record = DalamudConfirmedLocal.Read(HooksDirectory);

        if (record == null)
        {
            Log.Warning("[DUPDATE] 没有发行源确认过的本地 Dalamud, 不能不经发行源启动");
            return false;
        }

        if (versionInfoFromRemote && (record.Version != Version || record.Hash != OnlineHash || record.Runtime != runtimeVersion))
        {
            Log.Warning
            (
                "[DUPDATE] 发行源有新版本 {Version}/{Runtime}, 本地确认过的是 {LocalVersion}/{LocalRuntime}, 不用旧版",
                Version,
                runtimeVersion,
                record.Version,
                record.Runtime
            );
            return false;
        }

        if (record.GameVersion == null)
        {
            Log.Warning("[DUPDATE] 本地 Dalamud {Version} 还没用来起过游戏, 不能不经发行源启动", record.Version);
            return false;
        }

        var versionPath = new DirectoryInfo(Path.Combine(HooksDirectory.FullName, record.Version));

        if (!versionPath.Exists || !CheckDalamudIntegrity(versionPath, record.Hash))
        {
            Log.Warning("[DUPDATE] 本地 Dalamud {Version} 不完整", record.Version);
            return false;
        }

        if (!DotNetRuntimeManager.IsInstalled(Runtime, record.Runtime))
        {
            Log.Warning("[DUPDATE] 本地 .NET 运行时 {Runtime} 不完整", record.Runtime);
            return false;
        }

        if (DalamudAssetManager.LocalAssets(assetDirectory) is not { } assets)
        {
            Log.Warning("[DUPDATE] 本地没有完整的 Dalamud 资源文件");
            return false;
        }

        Version               = record.Version;
        OnlineHash            = record.Hash;
        runtimeVersion        = record.Runtime;
        versionInfoFromRemote = false;
        Runner                = new FileInfo(Path.Combine(versionPath.FullName, "Dalamud.Injector.exe"));
        AssetDirectory        = assets;
        ConfirmedGameVersion  = record.GameVersion;
        UsingConfirmedLocal   = true;

        Log.Warning
        (
            "[DUPDATE] 连不上 Dalamud 发行源（{Error}）, 用以前确认过的本地版本 {Version}（游戏 {GameVersion}）",
            EnsurementException?.Message,
            record.Version,
            record.GameVersion
        );
        return true;
    }

    /// <summary>
    ///     用发行源刚确认的版本起了游戏: 记下游戏版本, 以后连不上发行源时据此判断本地版本还适不适用
    /// </summary>
    public void ConfirmGameVersion(string gameVersion)
    {
        if (versionInfoFromRemote && !UsingConfirmedLocal)
            DalamudConfirmedLocal.ConfirmGameVersion(HooksDirectory, Version, gameVersion);
    }

    #endregion

    #region Utility

    private string GetReleaseAssetURL(string fileName) =>
        $"{releaseBaseURL}/{Version}/{fileName}";

    private async Task DownloadDalamudPackage(DirectoryInfo addonPath, string downloadURL)
    {
        if (addonPath.Exists) addonPath.Delete(true);
        addonPath.Create();

        var downloadPath = PlatformHelpers.GetTempFileName();

        try
        {
            await DownloadFiles([(downloadURL, downloadPath)]).ConfigureAwait(false);
            PlatformHelpers.Unzip7ZAsset(downloadPath, addonPath.FullName);
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
    {
        try
        {
            Log.Verbose("[DUPDATE] 开始检查目录 {Directory} 的完整性", directory.FullName);

            var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashesJson);
            if (hashes == null) throw new ArgumentNullException(nameof(hashes));

            foreach (var hash in hashes)
            {
                var file   = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));
                var hashed = ComputeFileHash(file);

                if (hashed != hash.Value)
                {
                    Log.Error("[DUPDATE] 完整性检查失败: {0} ({1} - {2})", file, hash.Value, hashed);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 完整性检查失败");
            return false;
        }

        return true;
    }

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

    private static string ComputeFileHash(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();

            var fileLength            = fileInfo.Length;
            var lastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;

            if (CachedFileHashes.TryGetValue(path, out var cachedFileHash) && cachedFileHash.Length == fileLength && cachedFileHash.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks)
                return cachedFileHash.Hash;

            using var stream = fileInfo.Open
            (
                new FileStreamOptions
                {
                    Access     = FileAccess.Read,
                    BufferSize = 128 * 1024,
                    Mode       = FileMode.Open,
                    Options    = FileOptions.SequentialScan,
                    Share      = FileShare.ReadWrite | FileShare.Delete
                }
            );
            using var md5 = MD5.Create();

            var hash = Convert.ToHexString(md5.ComputeHash(stream));
            CachedFileHashes[path] = (fileLength, lastWriteTimeUtcTicks, hash);
            return hash;
        }
        catch (Exception e)
        {
            throw new Exception("Error computing file hash: " + e.Message);
        }
    }

    /// <summary>
    ///     删掉旧版本目录, 但跳过还有游戏在用的: 在跑的游戏从自己那一版的目录加载程序集, 它的崩溃处理器重开游戏时
    ///     也要用那里的 Dalamud.Injector.exe。整目录删会把没被锁住的文件删掉、锁住的留下, 剩下一个残缺的版本
    /// </summary>
    internal static void CleanUpOld(DirectoryInfo addonPath, string currentVer)
    {
        if (!addonPath.Exists)
            return;

        foreach (var directory in addonPath.GetDirectories())
        {
            if (directory.Name == "dev" || directory.Name == currentVer) continue;

            if (FirstFileInUse(directory) is { } inUse)
            {
                Log.Information("[DUPDATE] 旧版本 {Version} 还有文件在用（{File}）, 不删", directory.Name, Path.GetRelativePath(directory.FullName, inUse));
                continue;
            }

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

    /// <summary>
    ///     目录里第一个打不开独占读写的文件; 都打得开时为 null。游戏映射着的 DLL、运行中的 exe 都打不开
    /// </summary>
    internal static string? FirstFileInUse(DirectoryInfo directory)
    {
        try
        {
            // 枚举是惰性的, 子目录打不开的异常在遍历途中才抛出, 所以整个遍历都包在里面
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    using var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception)
                {
                    return file.FullName;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DUPDATE] 枚举 {Directory} 失败, 当作在用", directory.FullName);
            return directory.FullName;
        }

        return null;
    }

    public async Task DownloadFile(string url, string path) =>
        await DownloadFiles([(url, path)]).ConfigureAwait(false);

    private async Task DownloadFiles(IReadOnlyList<(string Url, string Path)> files)
    {
        using var downloader = new HttpClientDownloadWithProgress(files);
        downloader.ProgressChanged += ReportLoadingProgressCore;

        await downloader.Download().ConfigureAwait(false);
    }

    #endregion

    #region UI

    private void SetLoadingDetail(string message)
    {
        LoadingDetail = message;
        ProgressSink?.SetLoadingMessage(message);
        NotifyStatusChanged();
    }

    public void ShowLoading() =>
        ProgressSink?.ShowLoading();

    public void HideLoading() =>
        ProgressSink?.HideLoading();

    private void ReportLoadingProgressCore(long? size, long downloaded, double? progress)
    {
        LoadingTotal      = size;
        LoadingDownloaded = downloaded;
        LoadingProgress   = progress;
        ProgressSink?.ReportLoadingProgress(size, downloaded, progress);
        NotifyStatusChanged();
    }

    private void NotifyStatusChanged() =>
        StatusChanged?.Invoke(this);

    #endregion

    public enum DownloadState
    {
        Unknown,
        Done,
        NoIntegrity // fail with error message
    }
}
