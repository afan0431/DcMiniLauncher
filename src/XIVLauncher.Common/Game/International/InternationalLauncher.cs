// 移植自 goatcorp/FFXIVQuickLauncher（GPL-3.0）提交 a6f33a9: src/XIVLauncher.Common/Game/Launcher.cs
// （另含 Game/Exceptions/OauthLoginException.cs 的错误文字解析、Util/ApiHelpers.cs 的 Accept-Language 生成）。
// 各方法注明原文件行号。相对原版的改动:
//   - 去掉 Steam、一次性密码输入、会话值缓存、补丁列表解析与补丁下载（无界面模式只报「需要更新」, 不打补丁）;
//   - 不把任何响应正文写进日志或异常（原版有三处: 取不到 _STORED_、解析不出错误文字、InvalidResponseException.Document）;
//   - 登录成功串字段不足时报错而不是数组越界; 响应带压缩时自行解压（原版声明支持 gzip 却不解压）;
//   - HttpMessageHandler、时钟、电脑标识可注入, 不依赖界面、账号库和 App 全局状态。

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Game.International;

/// <summary>
///     <see cref="InternationalLauncher" /> 的可注入项, 都有缺省值
/// </summary>
public sealed record InternationalLauncherOptions
{
    /// <summary>HTTP 处理器; 不传时用不带 Cookie 的 SocketsHttpHandler（与 goatcorp 相同）。测试传假的处理器</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>当前 UTC 时间</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>电脑标识（10 个十六进制字符）; 不传时按本机信息算</summary>
    public string? ComputerId { get; init; }

    /// <summary>语言代码是否按北美; 不传时看系统区域</summary>
    public bool? IsNorthAmerica { get; init; }

    /// <summary>
    ///     每取得一个敏感值（登录页的表单隐藏值、login.send 返回的会话值、版本上报返回的会话值）就回调一次,
    ///     调用方据此登记脱敏。本类自己不把这些值写进日志和异常。
    /// </summary>
    public Action<string>? OnSecret { get; init; }

    /// <summary>单个请求的超时</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
///     国际服（Square Enix 账号, Windows 版, 不带一次性密码）的登录与版本上报:
///     登录页 GET → login.send POST → patch-gamever POST, 另有 boot 版本检查和两个维护状态接口。
///     只做 HTTP 和读游戏目录, 不启动任何进程。
/// </summary>
public sealed partial class InternationalLauncher : IDisposable
{
    /// <summary>登录页与状态接口的 User-Agent, {0} = 电脑标识。出处: Launcher.cs:57</summary>
    public const string USER_AGENT_TEMPLATE = "SQEXAuthor/2.0.0(Windows 6.2; ja-jp; {0})";

    /// <summary>补丁服务器的 User-Agent。出处: goatcorp XIVLauncher.Common/Constants.cs:22</summary>
    public const string PATCHER_USER_AGENT = "FFXIV PATCH CLIENT";

    /// <summary>登录页与 login.send 的 Accept。出处: Launcher.cs:469, 559</summary>
    public const string ACCEPT = "image/gif, image/jpeg, image/pjpeg, application/x-ms-application, application/xaml+xml, application/x-ms-xbap, */*";

    /// <summary>登录成功串按逗号拆开后至少要有这么多段（用到的最大下标是 13）。出处: Launcher.cs:601-609</summary>
    public const int LOGIN_OK_MIN_FIELDS = 14;

    /// <summary>版本上报时要报长度和哈希的 boot 文件, 都在 &lt;游戏根&gt;\boot 下。出处: Launcher.cs:60-66</summary>
    public static readonly IReadOnlyList<string> FilesToHash =
    [
        "ffxivboot.exe",
        "ffxivboot64.exe",
        "ffxivlauncher64.exe",
        "ffxivupdater64.exe"
    ];

    private readonly HttpClient                   client;
    private readonly string                       frontierUrlTemplate;
    private readonly string                       acceptLanguage;
    private readonly string                       userAgent;
    private readonly InternationalLauncherOptions options;

    /// <summary>
    ///     创建登录器
    /// </summary>
    /// <param name="frontierUrlTemplate">登录页地址模板（Referer 用）, {0} = 语言代码, {1} = 时间; 见 <see cref="InternationalClientConfigProvider" /></param>
    /// <param name="acceptLanguage">Accept-Language; 见 <see cref="GenerateAcceptLanguage" /></param>
    /// <param name="options">可注入项</param>
    public InternationalLauncher(string frontierUrlTemplate, string acceptLanguage, InternationalLauncherOptions? options = null)
    {
        // 出处: Launcher.cs:38 —— 模板为空直接拒绝
        if (string.IsNullOrWhiteSpace(frontierUrlTemplate))
            throw new ArgumentException("Frontier URL template is null, this is now required", nameof(frontierUrlTemplate));

        this.frontierUrlTemplate = frontierUrlTemplate;
        this.acceptLanguage      = acceptLanguage;
        this.options             = options ?? new InternationalLauncherOptions();

        // 出处: Launcher.cs:41-47 —— 不带 Cookie; 不开自动解压（开了会给补丁服务器的请求多加 Accept-Encoding 头）
        client = new HttpClient(this.options.HttpHandler ?? new SocketsHttpHandler { UseCookies = false }, this.options.HttpHandler == null)
        {
            Timeout = this.options.RequestTimeout
        };

        userAgent = string.Format(CultureInfo.InvariantCulture, USER_AGENT_TEMPLATE, this.options.ComputerId ?? MakeComputerId());
    }

    /// <inheritdoc />
    public void Dispose() =>
        client.Dispose();

    #region 登录

    /// <summary>
    ///     登录并上报版本。出处: Launcher.cs:86-214（去掉 Steam 与缓存分支）
    /// </summary>
    /// <param name="userName">Square Enix 账号名（不是邮箱）</param>
    /// <param name="password">密码</param>
    /// <param name="gamePath">国际服游戏根目录（含 boot、game）</param>
    /// <param name="language">客户端语言（只影响 Referer 里的语言代码）</param>
    /// <param name="cancellationToken">取消</param>
    /// <exception cref="InternationalLoginRejectedException">SE 拒绝登录</exception>
    /// <exception cref="InternationalInvalidResponseException">服务器返回不符合预期</exception>
    /// <exception cref="InvalidVersionFilesException">本地版本文件损坏</exception>
    /// <exception cref="HttpRequestException">网络错误</exception>
    /// <exception cref="IOException">读不到游戏目录里的文件</exception>
    public async Task<InternationalLoginResult> LoginAsync
    (
        string            userName,
        string            password,
        DirectoryInfo     gamePath,
        ClientLanguage    language,
        CancellationToken cancellationToken = default
    )
    {
        var oauth = await OauthLoginAsync(userName, password, language, cancellationToken).ConfigureAwait(false);

        Log.Information
        (
            "[International] OAuth 登录成功 - playable:{IsPlayable} terms:{TermsAccepted} region:{Region} ex:{MaxExpansion}",
            oauth.Playable,
            oauth.TermsAccepted,
            oauth.Region,
            oauth.MaxExpansion
        );

        // 出处: Launcher.cs:168-182 —— 先看资格, 再看协议
        if (!oauth.Playable)
            return new InternationalLoginResult { State = InternationalLoginState.NoService, Region = oauth.Region, MaxExpansion = oauth.MaxExpansion };

        if (!oauth.TermsAccepted)
            return new InternationalLoginResult { State = InternationalLoginState.NoTerms, Region = oauth.Region, MaxExpansion = oauth.MaxExpansion };

        var (uniqueId, state) = await RegisterSessionAsync(oauth, gamePath, cancellationToken).ConfigureAwait(false);

        // 出处: Launcher.cs:204-205
        if (state == InternationalLoginState.Ok && string.IsNullOrEmpty(uniqueId))
            throw new InternationalInvalidResponseException(InternationalInvalidResponseKind.UniqueIdMissing, "LoginState is Ok, but UID is null or empty.");

        return new InternationalLoginResult
        {
            State        = state,
            UniqueId     = uniqueId,
            Region       = oauth.Region,
            MaxExpansion = oauth.MaxExpansion
        };
    }

    /// <summary>
    ///     界面版登录前对账号名的处理: 去掉所有空格。出处: goatcorp MainWindowViewModel.cs:263
    /// </summary>
    public static string NormalizeUserName(string userName) =>
        userName.Replace(" ", string.Empty);

    /// <summary>登录页地址。出处: Launcher.cs:526-530（rgn 固定 3: Launcher.cs:160; 不是免费试玩）</summary>
    public static string GetOauthTopUrl(bool isFreeTrial = false) =>
        string.Format(CultureInfo.InvariantCulture, Links.SE_OAUTH_TOP_URL_FORMAT, isFreeTrial ? "1" : "0");

    /// <summary>出处: Launcher.cs:548-611</summary>
    private async Task<InternationalOauthResult> OauthLoginAsync(string userName, string password, ClientLanguage language, CancellationToken cancellationToken)
    {
        var topUrl = GetOauthTopUrl();
        var stored = await GetOauthTopAsync(topUrl, language, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, Links.SE_OAUTH_SEND_URL);

        AddHeader(request, "Accept",          ACCEPT);
        AddHeader(request, "Referer",         topUrl);
        AddHeader(request, "Accept-Language", acceptLanguage);
        AddHeader(request, "User-Agent",      userAgent);
        AddHeader(request, "Accept-Encoding", "gzip, deflate");
        AddHeader(request, "Host",            Links.SE_OAUTH_HOST);
        AddHeader(request, "Connection",      "Keep-Alive");
        AddHeader(request, "Cache-Control",   "no-cache");
        AddHeader(request, "Cookie",          "_rsid=\"\"");

        // 不带一次性密码: otppw 传空串, 键始终在（出处: Launcher.cs:581-589; MainWindowViewModel.cs:278）
        request.Content = new FormUrlEncodedContent
        (
            new Dictionary<string, string>
            {
                { "_STORED_", stored },
                { "sqexid", userName },
                { "password", password },
                { "otppw", string.Empty }
            }
        );

        var reply  = await SendForTextAsync(request, cancellationToken).ConfigureAwait(false);
        var result = ParseLoginReply(reply);

        options.OnSecret?.Invoke(result.SessionId);
        return result;
    }

    /// <summary>出处: Launcher.cs:465-515</summary>
    private async Task<string> GetOauthTopAsync(string url, ClientLanguage language, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        AddHeader(request, "Accept",          ACCEPT);
        AddHeader(request, "Referer",         GenerateFrontierReferer(language));
        AddHeader(request, "Accept-Encoding", "gzip, deflate");
        AddHeader(request, "Accept-Language", acceptLanguage);
        AddHeader(request, "User-Agent",      userAgent);
        AddHeader(request, "Connection",      "Keep-Alive");
        AddHeader(request, "Cookie",          "_rsid=\"\"");

        var text   = await SendForTextAsync(request, cancellationToken).ConfigureAwait(false);
        var stored = ParseStored(text);

        options.OnSecret?.Invoke(stored);
        return stored;
    }

    /// <summary>
    ///     从登录页取表单隐藏值 _STORED_。出处: Launcher.cs:481-496。取不到时不记录页面正文。
    /// </summary>
    public static string ParseStored(string page)
    {
        if (page.Contains("window.external.user(\"restartup\");", StringComparison.Ordinal))
            throw new InternationalInvalidResponseException(InternationalInvalidResponseKind.RestartupRequested, "restartup, but not isSteam?");

        var matches = StoredRegex().Matches(page);

        if (matches.Count == 0)
        {
            Log.Error("[International] 登录页里取不到 STORED（页面长度 {Length}）", page.Length);
            throw new InternationalInvalidResponseException(InternationalInvalidResponseKind.StoredNotFound, "Could not get STORED.");
        }

        return matches[0].Groups["stored"].Value;
    }

    /// <summary>
    ///     解析 login.send 的返回。出处: Launcher.cs:595-610; 失败时的错误文字: OauthLoginException.cs:10-32。
    ///     不记录返回正文。
    /// </summary>
    /// <exception cref="InternationalLoginRejectedException">不是成功串</exception>
    /// <exception cref="InternationalInvalidResponseException">是成功串但字段不对</exception>
    public static InternationalOauthResult ParseLoginReply(string reply)
    {
        var matches = LoginOkRegex().Matches(reply);

        if (matches.Count == 0)
        {
            var errors = LoginErrorRegex().Matches(reply);

            // 原版: 错误文字恰好匹配到 1 处才采用, 否则当作未知错误
            if (errors.Count != 1)
            {
                Log.Error("[International] 登录被拒绝, 但解析不出错误文字（返回长度 {Length}, 匹配 {Count} 处）", reply.Length, errors.Count);
                throw new InternationalLoginRejectedException(null);
            }

            throw new InternationalLoginRejectedException(errors[0].Groups["errorMessage"].Value);
        }

        var launchParams = matches[0].Groups["launchParams"].Value.Split(',');

        if (launchParams.Length < LOGIN_OK_MIN_FIELDS ||
            string.IsNullOrEmpty(launchParams[1]) ||
            !int.TryParse(launchParams[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var region) ||
            !int.TryParse(launchParams[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxExpansion))
        {
            Log.Error("[International] 登录成功但返回的字段不符合预期（{Count} 段）", launchParams.Length);
            throw new InternationalInvalidResponseException(InternationalInvalidResponseKind.LoginReplyMalformed, $"Unexpected login reply ({launchParams.Length} fields).");
        }

        return new InternationalOauthResult
        {
            SessionId     = launchParams[1],
            Region        = region,
            TermsAccepted = launchParams[3] != "0",
            Playable      = launchParams[9] != "0",
            MaxExpansion  = maxExpansion
        };
    }

    #endregion

    #region 版本

    /// <summary>
    ///     boot 是否需要更新。出处: Launcher.cs:370-397（只看正文是否为空白, 不解析补丁列表）
    /// </summary>
    public async Task<bool> IsBootUpdateRequiredAsync(DirectoryInfo gamePath, CancellationToken cancellationToken = default)
    {
        var bootVersion = Repository.Boot.GetVer(gamePath);

        using var request = new HttpRequestMessage
        (
            HttpMethod.Get,
            string.Format(CultureInfo.InvariantCulture, Links.SE_PATCH_BOOTVER_URL_FORMAT, bootVersion, GetLauncherFormattedTimeLongRounded(options.UtcNow()))
        );

        AddHeader(request, "User-Agent", PATCHER_USER_AGENT);
        AddHeader(request, "Host",       Links.SE_PATCH_BOOTVER_HOST);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var       text     = await ReadTextAsync(response, cancellationToken).ConfigureAwait(false);

        // 原版会把正文交给补丁列表解析器, 错误页解析不了就报错; 这里不解析, 所以先看状态码, 免得把错误页当成「有更新」
        if (!response.IsSuccessStatusCode)
            throw new InternationalInvalidResponseException(InternationalInvalidResponseKind.BootCheckFailed, $"Boot version check failed. ({response.StatusCode})");

        return !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    ///     上报版本换取会话值。出处: Launcher.cs:399-446（不解析补丁列表: 正文非空就是需要更新）
    /// </summary>
    private async Task<(string? UniqueId, InternationalLoginState State)> RegisterSessionAsync
    (
        InternationalOauthResult oauth,
        DirectoryInfo            gamePath,
        CancellationToken        cancellationToken
    )
    {
        using var request = new HttpRequestMessage
        (
            HttpMethod.Post,
            string.Format(CultureInfo.InvariantCulture, Links.SE_PATCH_GAMEVER_URL_FORMAT, Repository.Ffxiv.GetVer(gamePath), oauth.SessionId)
        );

        AddHeader(request, "Connection",   "Keep-Alive");
        AddHeader(request, "User-Agent",   PATCHER_USER_AGENT);
        AddHeader(request, "X-Hash-Check", "enabled");

        EnsureVersionSanity(gamePath, oauth.MaxExpansion);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(GetVersionReport(gamePath, oauth.MaxExpansion)));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var       text     = await ReadTextAsync(response, cancellationToken).ConfigureAwait(false);

        // 409: 服务器要求更新 boot, 可刚才 boot 检查是通过的 —— 说明 boot 文件被改过, 没有会话值也没有补丁列表（原版注释 Launcher.cs:415-427）
        if (response.StatusCode == HttpStatusCode.Conflict)
            return (null, InternationalLoginState.NeedsPatchBoot);

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new InternationalInvalidResponseException
            (
                InternationalInvalidResponseKind.GameVersionGone,
                "The server indicated that the version requested is no longer being serviced or not present."
            );
        }

        if (!response.Headers.TryGetValues("X-Patch-Unique-Id", out var uniqueIdValues))
            throw new InternationalInvalidResponseException(InternationalInvalidResponseKind.UniqueIdMissing, $"Could not get X-Patch-Unique-Id. ({response.StatusCode})");

        var uniqueId = uniqueIdValues.First();
        options.OnSecret?.Invoke(uniqueId);

        return string.IsNullOrEmpty(text)
                   ? (uniqueId, InternationalLoginState.Ok)
                   : (uniqueId, InternationalLoginState.NeedsPatchGame);
    }

    /// <summary>
    ///     版本上报的正文。出处: Launcher.cs:266-286, 354-368
    /// </summary>
    public static string GetVersionReport(DirectoryInfo gamePath, int exLevel)
    {
        var verReport = $"{GetBootVersionHash(gamePath)}\n";

        if (exLevel >= 1)
            verReport += $"ex1\t{Repository.Ex1.GetVer(gamePath)}\n";

        if (exLevel >= 2)
            verReport += $"ex2\t{Repository.Ex2.GetVer(gamePath)}\n";

        if (exLevel >= 3)
            verReport += $"ex3\t{Repository.Ex3.GetVer(gamePath)}\n";

        if (exLevel >= 4)
            verReport += $"ex4\t{Repository.Ex4.GetVer(gamePath)}\n";

        if (exLevel >= 5)
            verReport += $"ex5\t{Repository.Ex5.GetVer(gamePath)}\n";

        return verReport;
    }

    /// <summary>出处: Launcher.cs:354-368</summary>
    private static string GetBootVersionHash(DirectoryInfo gamePath)
    {
        var result = Repository.Boot.GetVer(gamePath) + "=";

        for (var i = 0; i < FilesToHash.Count; i++)
        {
            result += $"{FilesToHash[i]}/{GetFileHash(Path.Combine(gamePath.FullName, "boot", FilesToHash[i]))}";

            if (i != FilesToHash.Count - 1)
                result += ",";
        }

        return result;
    }

    /// <summary>「长度/小写十六进制 SHA1」。出处: Launcher.cs:613-623</summary>
    private static string GetFileHash(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var hash  = Convert.ToHexStringLower(SHA1.HashData(bytes));

        return bytes.LongLength.ToString(CultureInfo.InvariantCulture) + "/" + hash;
    }

    /// <summary>
    ///     检查各 .ver 和 .bck: 不能为空白、不能含换行、不能全是 0 字节。出处: Launcher.cs:293-347
    /// </summary>
    /// <exception cref="InvalidVersionFilesException">有文件不合格</exception>
    public static void EnsureVersionSanity(DirectoryInfo gamePath, int exLevel)
    {
        Repository[] expansions = [Repository.Ex1, Repository.Ex2, Repository.Ex3, Repository.Ex4, Repository.Ex5];

        var failed = IsBadVersionSanity(gamePath, Repository.Ffxiv);
        failed |= IsBadVersionSanity(gamePath, Repository.Ffxiv, true);

        for (var i = 0; i < expansions.Length; i++)
        {
            if (exLevel < i + 1)
                continue;

            failed |= IsBadVersionSanity(gamePath, expansions[i]);
            failed |= IsBadVersionSanity(gamePath, expansions[i], true);
        }

        if (failed)
            throw new InvalidVersionFilesException();
    }

    private static bool IsBadVersionSanity(DirectoryInfo gamePath, Repository repo, bool isBck = false)
    {
        var text = repo.GetVer(gamePath, isBck);

        var nullOrWhitespace = string.IsNullOrWhiteSpace(text);
        var containsNewline  = text.Contains('\n');
        var allNullBytes     = Encoding.UTF8.GetBytes(text).All(x => x == 0x00);

        if (nullOrWhitespace || containsNewline || allNullBytes)
        {
            Log.Error
            (
                "[International] 版本文件检查不通过 {Repo}/{IsBck}: {NullOrWhitespace}, {ContainsNewline}, {AllNullBytes}",
                repo,
                isBck,
                nullOrWhitespace,
                containsNewline,
                allNullBytes
            );
            return true;
        }

        return false;
    }

    #endregion

    #region 维护状态

    /// <summary>登录服务是否开放。出处: Launcher.cs:641-655</summary>
    public async Task<InternationalGateStatus> GetLoginStatusAsync(CancellationToken cancellationToken = default) =>
        await GetStatusAsync
            (
                string.Format(CultureInfo.InvariantCulture, Links.SE_LOGIN_STATUS_URL_FORMAT, GetUnixMillis(options.UtcNow())),
                ClientLanguage.English,
                cancellationToken
            )
            .ConfigureAwait(false);

    /// <summary>游戏是否开放。出处: Launcher.cs:625-639</summary>
    public async Task<InternationalGateStatus> GetGateStatusAsync(ClientLanguage language, CancellationToken cancellationToken = default) =>
        await GetStatusAsync
            (
                string.Format
                (
                    CultureInfo.InvariantCulture,
                    Links.SE_GATE_STATUS_URL_FORMAT,
                    language.GetLangCode(options.IsNorthAmerica),
                    GetUnixMillis(options.UtcNow())
                ),
                language,
                cancellationToken
            )
            .ConfigureAwait(false);

    /// <summary>出处: Launcher.cs:675-696（DownloadAsLauncher）</summary>
    private async Task<InternationalGateStatus> GetStatusAsync(string url, ClientLanguage language, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        AddHeader(request, "User-Agent",      userAgent);
        AddHeader(request, "Accept-Encoding", "gzip, deflate");
        AddHeader(request, "Accept-Language", acceptLanguage);
        AddHeader(request, "Origin",          Links.SE_LAUNCHER_ORIGIN);
        AddHeader(request, "Referer",         GenerateFrontierReferer(language));
        AddHeader(request, "Connection",      "Keep-Alive");

        var text = await SendForTextAsync(request, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<InternationalGateStatus>(text, StatusJsonOptions)
               ?? throw new JsonException("status response was null");
    }

    #endregion

    #region 启动参数

    /// <summary>游戏可执行文件。出处: Launcher.cs:226</summary>
    public static string GetGameExePath(DirectoryInfo gamePath) =>
        Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");

    /// <summary>
    ///     国际服游戏启动参数, 顺序与 goatcorp 相同。出处: Launcher.cs:229-252（不含 Steam 的 IsSteam）
    /// </summary>
    /// <param name="uniqueId">版本上报返回的会话值（不是 login.send 返回的那个）</param>
    /// <param name="region">账号区域</param>
    /// <param name="maxExpansion">账号拥有的最高资料片</param>
    /// <param name="language">客户端语言</param>
    /// <param name="gameVersion">game\ffxivgame.ver 的内容</param>
    /// <param name="additionalArguments">附加参数（key=value, 空格分隔）; 可空</param>
    public static ArgumentBuilder BuildGameArguments
    (
        string         uniqueId,
        int            region,
        int            maxExpansion,
        ClientLanguage language,
        string         gameVersion,
        string?        additionalArguments = null
    )
    {
        var argumentBuilder = new ArgumentBuilder()
                              .Append("DEV.DataPathType",           "1")
                              .Append("DEV.MaxEntitledExpansionID", maxExpansion.ToString(CultureInfo.InvariantCulture))
                              .Append("DEV.TestSID",                uniqueId)
                              .Append("DEV.UseSqPack",              "1")
                              .Append("SYS.Region",                 region.ToString(CultureInfo.InvariantCulture))
                              .Append("language",                   ((int)language).ToString(CultureInfo.InvariantCulture))
                              .Append("resetConfig",                "0")
                              .Append("ver",                        gameVersion);

        if (!string.IsNullOrEmpty(additionalArguments))
        {
            foreach (Match match in AdditionalArgumentsRegex().Matches(additionalArguments))
                argumentBuilder.Append(match.Groups["key"].Value, match.Groups["value"].Value.Trim());
        }

        return argumentBuilder;
    }

    #endregion

    #region 公共值

    /// <summary>
    ///     电脑标识: 「机器名 + 用户名 + 系统版本 + 处理器数」的 UTF-16LE 字节取 SHA1, 前 4 字节放 [1..4], [0] 是使 5 字节之和为 0 的校验字节,
    ///     转小写十六进制共 10 个字符。同一台电脑同一个 Windows 用户下所有号用同一个标识。出处: Launcher.cs:657-673
    /// </summary>
    public static string MakeComputerId() =>
        MakeComputerId(Environment.MachineName + Environment.UserName + Environment.OSVersion + Environment.ProcessorCount);

    /// <summary>按给定的原始字符串算电脑标识</summary>
    public static string MakeComputerId(string hashString)
    {
        var bytes = new byte[5];

        Array.Copy(SHA1.HashData(Encoding.Unicode.GetBytes(hashString)), 0, bytes, 1, 4);

        var checkSum = (byte)-(bytes[1] + bytes[2] + bytes[3] + bytes[4]);
        bytes[0] = checkSum;

        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    ///     Accept-Language。goatcorp 首次运行时调 GenerateAcceptLanguage()（种子缺省为 0）生成一次后存进设置,
    ///     种子固定, 所以所有用户得到的是同一个值; 这里照同样的算法现算, 不必存。出处: ApiHelpers.cs:14-41; App.xaml.cs:133-136
    /// </summary>
    public static string GenerateAcceptLanguage(int seed = 0)
    {
        string[] codes     = ["de-DE", "en-US", "ja"];
        string[] codesMany = ["de-DE", "en-US,en", "en-GB,en", "fr-BE,fr", "ja", "fr-FR,fr", "fr-CH,fr"];
        var      rng       = new Random(seed);

        var many = rng.Next(10) < 3;

        if (many)
        {
            var howMany = rng.Next(2, 4);
            var deck    = codesMany.OrderBy(_ => rng.Next()).Take(howMany).ToArray();

            var hdr = string.Empty;

            for (var i = 0; i < deck.Length; i++)
            {
                hdr += deck[i] + $";q=0.{10 - (i + 1)}";

                if (i != deck.Length - 1)
                    hdr += ";";
            }

            return hdr;
        }

        return codes[rng.Next(0, codes.Length)];
    }

    /// <summary>Referer。出处: Launcher.cs:698-704</summary>
    public string GenerateFrontierReferer(ClientLanguage language)
    {
        var langCode      = language.GetLangCode(options.IsNorthAmerica).Replace("-", "_");
        var formattedTime = GetLauncherFormattedTimeLong(options.UtcNow());

        return string.Format(CultureInfo.InvariantCulture, frontierUrlTemplate, langCode, formattedTime);
    }

    /// <summary>UTC「年-月-日-时-分」。出处: Launcher.cs:709</summary>
    public static string GetLauncherFormattedTimeLong(DateTime utcNow) =>
        utcNow.ToString("yyyy-MM-dd-HH-mm", CultureInfo.InvariantCulture);

    /// <summary>同上, 分钟的个位改成 0。出处: Launcher.cs:711-717</summary>
    public static string GetLauncherFormattedTimeLongRounded(DateTime utcNow)
    {
        var formatted = GetLauncherFormattedTimeLong(utcNow).ToCharArray();
        formatted[15] = '0';

        return new string(formatted);
    }

    /// <summary>出处: ApiHelpers.cs:9-12</summary>
    private static long GetUnixMillis(DateTime utcNow) =>
        (long)utcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;

    #endregion

    #region HTTP

    private static readonly JsonSerializerOptions StatusJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static void AddHeader(HttpRequestMessage request, string key, string value)
    {
        if (!request.Headers.TryAddWithoutValidation(key, value))
            throw new InvalidOperationException($"Could not add header - {key}");
    }

    private async Task<string> SendForTextAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadTextAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     读响应正文; 请求头里声明了支持 gzip / deflate, 服务器真压缩了就在这里解开
    /// </summary>
    private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var encodings = response.Content.Headers.ContentEncoding;

        if (encodings.Count == 0)
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        foreach (var encoding in encodings.Reverse())
        {
            stream = encoding.ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(stream, CompressionMode.Decompress),
                "deflate"          => new ZLibStream(stream, CompressionMode.Decompress),
                "identity"         => stream,
                _                  => throw new HttpRequestException($"Unsupported content encoding: {encoding}")
            };
        }

        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        var textEncoding = Encoding.UTF8;

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                textEncoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // 不认识的字符集按 UTF-8
            }
        }

        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, textEncoding);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion

    // 以制表符开头, 依赖页面的缩进和属性顺序。出处: Launcher.cs:489
    [GeneratedRegex(@"\t<\s*input .* name=""_STORED_"" value=""(?<stored>.*)"">")]
    private static partial Regex StoredRegex();

    // 出处: Launcher.cs:595
    [GeneratedRegex(@"window.external.user\(""login=auth,ok,(?<launchParams>.*)\);")]
    private static partial Regex LoginOkRegex();

    // 出处: OauthLoginException.cs:10-11
    [GeneratedRegex(@"window.external.user\(""login=auth,ng,err,(?<errorMessage>.*)\""\);", RegexOptions.CultureInvariant)]
    private static partial Regex LoginErrorRegex();

    // 出处: Launcher.cs:249
    [GeneratedRegex(@"\s*(?<key>[^\s=]+)\s*=\s*(?<value>([^=]*$|[^=]*\s(?=[^\s=]+)))\s*")]
    private static partial Regex AdditionalArgumentsRegex();
}
