using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using XIVLauncher.Common;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.International;
using Xunit;

namespace XIVLauncher.Test.International;

[Collection(SerilogCaptureCollection.NAME)]
public sealed class InternationalLauncherTests
{
    private const string FRONTIER   = "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}";
    private const string ACCEPT_LANG = "en-US";
    private const string COMPUTER_ID = "0011223344";
    private const string USER        = "seAccountName";
    private const string PASSWORD    = "P@ss word+1/=";
    private const string STORED      = "STOREDVALUE0123456789abcdefSTORED";
    private const string SESSION_ID  = "sid0123456789abcdef0123456789abcdef";
    private const string UNIQUE_ID   = "uid9876543210fedcba9876543210fedcba";

    private static readonly DateTime Now = new(2026, 10, 6, 13, 47, 59, DateTimeKind.Utc);

    private const string TOP_URL = "https://ffxiv-login.square-enix.com/oauth/ffxivarr/login/top?lng=en&rgn=3&isft=0&cssmode=1&isnew=1&launchver=3";

    private static string TopPage(string stored = STORED) =>
        "<html><body>\n<form>\n\t<input type=\"hidden\" name=\"_STORED_\" value=\"" + stored + "\">\n\t<input name=\"sqexid\">\n</form></body></html>";

    /// <summary>成功串: 下标 [1] 会话值、[3] 协议、[5] 区域、[9] 可玩、[13] 最高资料片（goatcorp Launcher.cs:601-609）</summary>
    private static string OkReply(string sid = SESSION_ID, string terms = "1", string region = "3", string playable = "1", string maxEx = "5") =>
        $"<script>window.external.user(\"login=auth,ok,sid,{sid},terms,{terms},region,{region},etmadd,0,playable,{playable},ps3pkg,0,maxex,{maxEx},product,1\");</script>";

    private static string ErrorReply(string message) =>
        $"<script>window.external.user(\"login=auth,ng,err,{message}\");</script>";

    private static InternationalLauncher NewLauncher(FakeHttpHandler handler, Action<string>? onSecret = null) =>
        new
        (
            FRONTIER,
            ACCEPT_LANG,
            new InternationalLauncherOptions
            {
                HttpHandler    = handler,
                UtcNow         = () => Now,
                ComputerId     = COMPUTER_ID,
                IsNorthAmerica = false,
                OnSecret       = onSecret
            }
        );

    private static HttpResponseMessage RegisterOk(string body = "", string? uid = UNIQUE_ID, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = FakeHttpHandler.Text(body, status);

        if (uid != null)
            response.Headers.TryAddWithoutValidation("X-Patch-Unique-Id", uid);

        return response;
    }

    private static FakeHttpHandler HappyHandler(string? loginReply = null, Func<HttpResponseMessage>? register = null) =>
        new
        (request =>
            {
                if (request.Url.Contains("/login/top", StringComparison.Ordinal))
                    return FakeHttpHandler.Text(TopPage());

                if (request.Url.Contains("/login/login.send", StringComparison.Ordinal))
                    return FakeHttpHandler.Text(loginReply ?? OkReply());

                if (request.Url.Contains("patch-gamever", StringComparison.Ordinal))
                    return register?.Invoke() ?? RegisterOk();

                return FakeHttpHandler.Text("unexpected", HttpStatusCode.NotFound);
            }
        );

    #region 电脑标识与公共值

    [Theory]
    [InlineData("PC3Administrator" + "Microsoft Windows NT 10.0.26200.0" + "36")]
    [InlineData("")]
    [InlineData("挂机电脑员工")]
    public void MakeComputerId_Is10HexChars_AndChecksumMakesByteSumZero(string seed)
    {
        var id = InternationalLauncher.MakeComputerId(seed);

        Assert.Equal(10, id.Length);
        Assert.Matches("^[0-9a-f]{10}$", id);
        Assert.Equal(0, Convert.FromHexString(id).Sum(x => x) % 256);
        Assert.Equal(id, InternationalLauncher.MakeComputerId(seed));
    }

    [Fact]
    public void MakeComputerId_MatchesHandComputedValue()
    {
        // SHA1(UTF-16LE("abc")) = 9f04f41a 84851416 ...; 前 4 字节 9f 04 f4 1a, 和 = 0x1b1, 校验字节 = 0x100 - 0xb1 = 0x4f
        Assert.Equal("4f9f04f41a", InternationalLauncher.MakeComputerId("abc"));
    }

    [Fact]
    public void FormattedTime_LongAndRounded()
    {
        Assert.Equal("2026-10-06-13-47", InternationalLauncher.GetLauncherFormattedTimeLong(Now));
        Assert.Equal("2026-10-06-13-40", InternationalLauncher.GetLauncherFormattedTimeLongRounded(Now));
    }

    [Theory]
    [InlineData(ClientLanguage.Japanese, false, "ja", 0)]
    [InlineData(ClientLanguage.English, false, "en-gb", 1)]
    [InlineData(ClientLanguage.English, true, "en-us", 1)]
    [InlineData(ClientLanguage.German, false, "de", 2)]
    [InlineData(ClientLanguage.French, true, "fr", 3)]
    public void ClientLanguage_CodesAndNumbers(ClientLanguage language, bool northAmerica, string code, int number)
    {
        Assert.Equal(code, language.GetLangCode(northAmerica));
        Assert.Equal(number, (int)language);
    }

    [Fact]
    public void GenerateAcceptLanguage_IsDeterministic_AndWellFormed()
    {
        var value = InternationalLauncher.GenerateAcceptLanguage();

        Assert.Equal(value, InternationalLauncher.GenerateAcceptLanguage());
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.Matches("^(de-DE|en-US|ja|[A-Za-z,;=.0-9-]+)$", value);
    }

    [Fact]
    public void Constructor_RejectsEmptyFrontierTemplate()
    {
        Assert.Throws<ArgumentException>(() => new InternationalLauncher(" ", ACCEPT_LANG));
    }

    [Fact]
    public void NormalizeUserName_RemovesSpaces()
    {
        Assert.Equal("myaccount", InternationalLauncher.NormalizeUserName(" my account "));
    }

    #endregion

    #region 页面解析

    [Fact]
    public void ParseStored_ReadsTabIndentedHiddenInput()
    {
        Assert.Equal(STORED, InternationalLauncher.ParseStored(TopPage()));
    }

    [Theory]
    [InlineData("<html><body>captcha required</body></html>")]
    [InlineData("<input type=\"hidden\" name=\"_STORED_\" value=\"x\">")] // 没有制表符开头: 原版正则不认
    [InlineData("")]
    public void ParseStored_Missing_Throws_WithoutPageInMessage(string page)
    {
        var ex = Assert.Throws<InternationalInvalidResponseException>(() => InternationalLauncher.ParseStored(page));

        Assert.Equal(InternationalInvalidResponseKind.StoredNotFound, ex.Kind);
        Assert.Equal("Could not get STORED.", ex.Message);
    }

    [Fact]
    public void ParseStored_Restartup_Throws()
    {
        var ex = Assert.Throws<InternationalInvalidResponseException>(() => InternationalLauncher.ParseStored("window.external.user(\"restartup\");"));

        Assert.Equal(InternationalInvalidResponseKind.RestartupRequested, ex.Kind);
    }

    [Fact]
    public void ParseLoginReply_Ok_ReadsFiveFieldsByIndex()
    {
        var result = InternationalLauncher.ParseLoginReply(OkReply(region: "2", maxEx: "4"));

        Assert.Equal(SESSION_ID, result.SessionId);
        Assert.Equal(2, result.Region);
        Assert.True(result.TermsAccepted);
        Assert.True(result.Playable);
        Assert.Equal(4, result.MaxExpansion);
        Assert.DoesNotContain(SESSION_ID, result.ToString());
    }

    [Fact]
    public void ParseLoginReply_TermsZero_And_PlayableZero()
    {
        Assert.False(InternationalLauncher.ParseLoginReply(OkReply(terms: "0")).TermsAccepted);
        Assert.False(InternationalLauncher.ParseLoginReply(OkReply(playable: "0")).Playable);
    }

    [Fact]
    public void ParseLoginReply_Rejected_CarriesSeMessage()
    {
        var ex = Assert.Throws<InternationalLoginRejectedException>
            (() => InternationalLauncher.ParseLoginReply(ErrorReply("ID or password is incorrect.")));

        Assert.Equal("ID or password is incorrect.", ex.SeMessage);
        Assert.Equal("ID or password is incorrect.", ex.Message);
    }

    [Theory]
    [InlineData("<html>service unavailable</html>")]
    [InlineData("")]
    public void ParseLoginReply_RejectedWithoutParsableText_HasNullSeMessage_AndNoPageInMessage(string reply)
    {
        var ex = Assert.Throws<InternationalLoginRejectedException>(() => InternationalLauncher.ParseLoginReply(reply));

        Assert.Null(ex.SeMessage);
        Assert.Equal("Unknown error", ex.Message);
    }

    [Fact]
    public void ParseLoginReply_TwoErrorMatches_TreatedAsUnknown()
    {
        var ex = Assert.Throws<InternationalLoginRejectedException>
            (() => InternationalLauncher.ParseLoginReply(ErrorReply("a") + "\n" + ErrorReply("b")));

        Assert.Null(ex.SeMessage);
    }

    [Theory]
    [InlineData("window.external.user(\"login=auth,ok,sid,abc,terms,1\");")]                                  // 字段不足 14 个
    [InlineData("window.external.user(\"login=auth,ok,sid,abc,terms,1,region,x,e,0,playable,1,p,0,maxex,5\");")] // 区域不是数字
    [InlineData("window.external.user(\"login=auth,ok,sid,abc,terms,1,region,3,e,0,playable,1,p,0,maxex,\");")]  // 资料片为空
    [InlineData("window.external.user(\"login=auth,ok,sid,,terms,1,region,3,e,0,playable,1,p,0,maxex,5\");")]    // 会话值为空
    public void ParseLoginReply_MalformedOk_ThrowsInvalidResponse_NotIndexOutOfRange(string reply)
    {
        var ex = Assert.Throws<InternationalInvalidResponseException>(() => InternationalLauncher.ParseLoginReply(reply));

        Assert.Equal(InternationalInvalidResponseKind.LoginReplyMalformed, ex.Kind);
        Assert.DoesNotContain("abc", ex.Message);
    }

    #endregion

    #region 登录全流程（假 HTTP）

    [Fact]
    public async Task Login_Ok_SendsThreeRequestsWithGoatcorpHeaders()
    {
        using var game = new FakeGameDirectory();
        var handler    = HappyHandler();
        var secrets    = new List<string>();
        using var launcher = NewLauncher(handler, secrets.Add);

        var result = await launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English);

        Assert.Equal(InternationalLoginState.Ok, result.State);
        Assert.Equal(UNIQUE_ID, result.UniqueId);
        Assert.Equal(3, result.Region);
        Assert.Equal(5, result.MaxExpansion);
        Assert.DoesNotContain(UNIQUE_ID, result.ToString());
        Assert.Equal([STORED, SESSION_ID, UNIQUE_ID], secrets);

        Assert.Equal(3, handler.Requests.Count);
        const string USER_AGENT = "SQEXAuthor/2.0.0(Windows 6.2; ja-jp; 0011223344)";
        const string ACCEPT     = "image/gif, image/jpeg, image/pjpeg, application/x-ms-application, application/xaml+xml, application/x-ms-xbap, */*";

        // 1. 登录页
        var top = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, top.Method);
        Assert.Equal(TOP_URL, top.Url);
        Assert.Equal
        (
            new Dictionary<string, string>
            {
                ["Accept"]          = ACCEPT,
                ["Referer"]         = "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang=en_gb&time=2026-10-06-13-47",
                ["Accept-Encoding"] = "gzip, deflate",
                ["Accept-Language"] = ACCEPT_LANG,
                ["User-Agent"]      = USER_AGENT,
                ["Connection"]      = "Keep-Alive",
                ["Cookie"]          = "_rsid=\"\""
            },
            top.Headers
        );

        // 2. 提交登录
        var send = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Equal("https://ffxiv-login.square-enix.com/oauth/ffxivarr/login/login.send", send.Url);
        Assert.Equal
        (
            new Dictionary<string, string>
            {
                ["Accept"]          = ACCEPT,
                ["Referer"]         = TOP_URL,
                ["Accept-Language"] = ACCEPT_LANG,
                ["User-Agent"]      = USER_AGENT,
                ["Accept-Encoding"] = "gzip, deflate",
                ["Host"]            = "ffxiv-login.square-enix.com",
                ["Connection"]      = "Keep-Alive",
                ["Cache-Control"]   = "no-cache",
                ["Cookie"]          = "_rsid=\"\""
            },
            send.Headers
        );
        Assert.Equal("application/x-www-form-urlencoded", send.ContentType);

        var form = send.Body!.Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => WebUtility.UrlDecode(x[1]));
        Assert.Equal(["_STORED_", "sqexid", "password", "otppw"], send.Body.Split('&').Select(x => x.Split('=', 2)[0]));
        Assert.Equal(STORED, form["_STORED_"]);
        Assert.Equal(USER, form["sqexid"]);
        Assert.Equal(PASSWORD, form["password"]);
        Assert.Equal(string.Empty, form["otppw"]);
        Assert.EndsWith("&otppw=", send.Body);

        // 3. 上报版本
        var register = handler.Requests[2];
        Assert.Equal(HttpMethod.Post, register.Method);
        Assert.Equal($"https://patch-gamever.ffxiv.com/http/win32/ffxivneo_release_game/{FakeGameDirectory.GAME_VERSION}/{SESSION_ID}", register.Url);
        Assert.Equal
        (
            new Dictionary<string, string>
            {
                ["Connection"]   = "Keep-Alive",
                ["User-Agent"]   = "FFXIV PATCH CLIENT",
                ["X-Hash-Check"] = "enabled"
            },
            register.Headers
        );
        Assert.Null(register.ContentType);
        Assert.Equal(InternationalLauncher.GetVersionReport(game.Root, 5), register.Body);
    }

    [Theory]
    [InlineData(ClientLanguage.Japanese, "ja")]
    [InlineData(ClientLanguage.German, "de")]
    [InlineData(ClientLanguage.French, "fr")]
    public async Task Login_RefererCarriesLanguageCode(ClientLanguage language, string code)
    {
        using var game = new FakeGameDirectory();
        var handler    = HappyHandler();
        using var launcher = NewLauncher(handler);

        await launcher.LoginAsync(USER, PASSWORD, game.Root, language);

        Assert.Equal($"https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={code}&time=2026-10-06-13-47", handler.Requests[0].Headers["Referer"]);
    }

    [Fact]
    public async Task Login_NotPlayable_ReturnsNoService_WithoutRegisteringSession()
    {
        using var game = new FakeGameDirectory();
        var handler    = HappyHandler(OkReply(playable: "0", terms: "0"));
        using var launcher = NewLauncher(handler);

        var result = await launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English);

        // 原版先看资格再看协议
        Assert.Equal(InternationalLoginState.NoService, result.State);
        Assert.Null(result.UniqueId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Login_TermsNotAccepted_ReturnsNoTerms_WithoutRegisteringSession()
    {
        using var game = new FakeGameDirectory();
        var handler    = HappyHandler(OkReply(terms: "0"));
        using var launcher = NewLauncher(handler);

        var result = await launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English);

        Assert.Equal(InternationalLoginState.NoTerms, result.State);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Login_Rejected_Throws_AndNothingSensitiveReachesLogsOrException()
    {
        using var logs = new CapturedLogs();
        using var game = new FakeGameDirectory();
        var handler    = HappyHandler(ErrorReply("ID or password is incorrect. (code 1234)"));
        using var launcher = NewLauncher(handler);

        var ex = await Assert.ThrowsAsync<InternationalLoginRejectedException>
                     (() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        Assert.Equal("ID or password is incorrect. (code 1234)", ex.SeMessage);
        AssertNoSecrets(ex.ToString());
        AssertNoSecrets(logs.All);
    }

    [Fact]
    public async Task Login_StoredMissing_Throws_AndPageIsNotLogged()
    {
        using var logs = new CapturedLogs();
        using var game = new FakeGameDirectory();
        const string PAGE_MARKER = "PAGE-BODY-MARKER-7788";
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Text($"<html>{PAGE_MARKER} please verify you are human</html>"));
        using var launcher = NewLauncher(handler);

        var ex = await Assert.ThrowsAsync<InternationalInvalidResponseException>
                     (() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        Assert.Equal(InternationalInvalidResponseKind.StoredNotFound, ex.Kind);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(PAGE_MARKER, ex.ToString());
        Assert.DoesNotContain(PAGE_MARKER, logs.All);
    }

    [Fact]
    public async Task Login_UnparsableRejection_DoesNotLogReplyBody()
    {
        using var logs = new CapturedLogs();
        using var game = new FakeGameDirectory();
        const string MARKER = "REPLY-BODY-MARKER-5566";
        var handler = HappyHandler($"<html>{MARKER} {PASSWORD}</html>");
        using var launcher = NewLauncher(handler);

        var ex = await Assert.ThrowsAsync<InternationalLoginRejectedException>
                     (() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        Assert.Null(ex.SeMessage);
        Assert.DoesNotContain(MARKER, logs.All);
        AssertNoSecrets(logs.All);
    }

    [Fact]
    public async Task Login_FullSuccess_LogsNoSecrets()
    {
        using var logs = new CapturedLogs();
        using var game = new FakeGameDirectory();
        using var launcher = NewLauncher(HappyHandler());

        await launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English);

        Assert.Contains("OAuth 登录成功", logs.All);
        AssertNoSecrets(logs.All);
    }

    [Fact]
    public async Task Login_NetworkError_PropagatesHttpRequestException_WithoutSecrets()
    {
        using var game = new FakeGameDirectory();
        var handler = new FakeHttpHandler
        (request => request.Url.Contains("patch-gamever", StringComparison.Ordinal)
                        ? throw new HttpRequestException("No such host is known. (patch-gamever.ffxiv.com:443)")
                        : request.Url.Contains("login.send", StringComparison.Ordinal)
                            ? FakeHttpHandler.Text(OkReply())
                            : FakeHttpHandler.Text(TopPage())
        );
        using var launcher = NewLauncher(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        AssertNoSecrets(ex.ToString());
    }

    [Fact]
    public async Task Login_GzipResponse_IsDecompressed()
    {
        using var game = new FakeGameDirectory();
        var handler = new FakeHttpHandler
        (request =>
            {
                if (request.Url.Contains("/login/top", StringComparison.Ordinal))
                {
                    using var buffer = new MemoryStream();

                    using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
                        gzip.Write(Encoding.UTF8.GetBytes(TopPage()));

                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
                    response.Content.Headers.ContentEncoding.Add("gzip");
                    return response;
                }

                return request.Url.Contains("login.send", StringComparison.Ordinal) ? FakeHttpHandler.Text(OkReply()) : RegisterOk();
            }
        );
        using var launcher = NewLauncher(handler);

        var result = await launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English);

        Assert.Equal(InternationalLoginState.Ok, result.State);
        Assert.Contains("_STORED_=" + STORED, handler.Requests[1].Body);
    }

    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain(PASSWORD, text);
        Assert.DoesNotContain(WebUtility.UrlEncode(PASSWORD), text);
        Assert.DoesNotContain(Uri.EscapeDataString(PASSWORD), text);
        Assert.DoesNotContain(STORED, text);
        Assert.DoesNotContain(SESSION_ID, text);
        Assert.DoesNotContain(UNIQUE_ID, text);
    }

    #endregion

    #region 版本上报

    [Fact]
    public void VersionReport_MaxExpansion0_IsBootLineOnly()
    {
        using var game = new FakeGameDirectory();

        Assert.Equal(FakeGameDirectory.BootHashLine + "\n", InternationalLauncher.GetVersionReport(game.Root, 0));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void VersionReport_ListsExpansionsUpToMaxExpansion(int maxExpansion)
    {
        using var game = new FakeGameDirectory();

        var expected = FakeGameDirectory.BootHashLine + "\n";

        for (var i = 1; i <= maxExpansion; i++)
            expected += $"ex{i}\t{FakeGameDirectory.ExVersion(i)}\n";

        Assert.Equal(expected, InternationalLauncher.GetVersionReport(game.Root, maxExpansion));
    }

    [Fact]
    public void VersionReport_MissingExpansionVersionFile_ReportsBaseVersion()
    {
        using var game = new FakeGameDirectory(1);

        Assert.EndsWith("ex1\t2026.09.01.0000.0000\nex2\t2012.01.01.0000.0000\n", InternationalLauncher.GetVersionReport(game.Root, 2));
    }

    [Fact]
    public void VersionReport_MissingBootFile_ThrowsIoException()
    {
        using var game = new FakeGameDirectory(withBootFiles: false);

        Assert.ThrowsAny<IOException>(() => InternationalLauncher.GetVersionReport(game.Root, 0));
    }

    [Theory]
    [InlineData("2026.09.15.0000.0000\n")]
    [InlineData("\0\0\0\0")]
    public void EnsureVersionSanity_BadGameVersionFile_Throws(string content)
    {
        using var game = new FakeGameDirectory();
        File.WriteAllText(Path.Combine(game.Root.FullName, "game", "ffxivgame.ver"), content);

        Assert.Throws<InvalidVersionFilesException>(() => InternationalLauncher.EnsureVersionSanity(game.Root, 5));
    }

    [Fact]
    public void EnsureVersionSanity_OnlyChecksOwnedExpansions()
    {
        using var game = new FakeGameDirectory();
        File.WriteAllText(Path.Combine(game.Root.FullName, "game", "sqpack", "ex3", "ex3.ver"), "bad\nversion");

        InternationalLauncher.EnsureVersionSanity(game.Root, 2);
        Assert.Throws<InvalidVersionFilesException>(() => InternationalLauncher.EnsureVersionSanity(game.Root, 3));
    }

    [Fact]
    public async Task RegisterSession_Conflict409_IsNeedsPatchBoot_WithoutUniqueId()
    {
        using var game = new FakeGameDirectory();
        using var launcher = NewLauncher(HappyHandler(register: () => RegisterOk("ignored", null, HttpStatusCode.Conflict)));

        var result = await launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English);

        Assert.Equal(InternationalLoginState.NeedsPatchBoot, result.State);
        Assert.Null(result.UniqueId);
    }

    [Fact]
    public async Task RegisterSession_Gone410_ThrowsInvalidResponse()
    {
        using var game = new FakeGameDirectory();
        using var launcher = NewLauncher(HappyHandler(register: () => RegisterOk("gone body", UNIQUE_ID, HttpStatusCode.Gone)));

        var ex = await Assert.ThrowsAsync<InternationalInvalidResponseException>
                     (() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        Assert.Equal(InternationalInvalidResponseKind.GameVersionGone, ex.Kind);
        AssertNoSecrets(ex.ToString());
    }

    [Fact]
    public async Task RegisterSession_MissingUniqueIdHeader_ThrowsInvalidResponse()
    {
        using var game = new FakeGameDirectory();
        using var launcher = NewLauncher(HappyHandler(register: () => RegisterOk(string.Empty, null)));

        var ex = await Assert.ThrowsAsync<InternationalInvalidResponseException>
                     (() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        Assert.Equal(InternationalInvalidResponseKind.UniqueIdMissing, ex.Kind);
        AssertNoSecrets(ex.ToString());
    }

    [Fact]
    public async Task RegisterSession_EmptyUniqueIdHeaderWithEmptyBody_ThrowsInsteadOfReturningOk()
    {
        using var game = new FakeGameDirectory();
        using var launcher = NewLauncher(HappyHandler(register: () => RegisterOk(string.Empty, string.Empty)));

        var ex = await Assert.ThrowsAsync<InternationalInvalidResponseException>
                     (() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        Assert.Equal(InternationalInvalidResponseKind.UniqueIdMissing, ex.Kind);
    }

    [Fact]
    public async Task RegisterSession_NonEmptyBody_IsNeedsPatchGame_AndPatchListIsNotLogged()
    {
        using var logs = new CapturedLogs();
        using var game = new FakeGameDirectory();
        using var launcher = NewLauncher(HappyHandler(register: () => RegisterOk("--477D80B1_38BC_41d4_8B48_5273ADB89CAC\r\npatch list here")));

        var result = await launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English);

        Assert.Equal(InternationalLoginState.NeedsPatchGame, result.State);
        Assert.Equal(UNIQUE_ID, result.UniqueId);
        Assert.DoesNotContain("patch list here", logs.All);
    }

    [Fact]
    public async Task RegisterSession_BadVersionFiles_ThrowsBeforeSending()
    {
        using var game = new FakeGameDirectory();
        File.WriteAllText(Path.Combine(game.Root.FullName, "game", "ffxivgame.ver"), "2026.09.15.0000.0000\r\n");
        var handler = HappyHandler();
        using var launcher = NewLauncher(handler);

        await Assert.ThrowsAsync<InvalidVersionFilesException>(() => launcher.LoginAsync(USER, PASSWORD, game.Root, ClientLanguage.English));

        Assert.Equal(2, handler.Requests.Count);
    }

    #endregion

    #region boot 检查与维护状态

    [Theory]
    [InlineData("", false)]
    [InlineData("  \r\n", false)]
    [InlineData("--boundary\r\npatch entry", true)]
    public async Task BootCheck_BlankBodyMeansUpToDate(string body, bool expected)
    {
        using var game = new FakeGameDirectory();
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Text(body));
        using var launcher = NewLauncher(handler);

        Assert.Equal(expected, await launcher.IsBootUpdateRequiredAsync(game.Root));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"http://patch-bootver.ffxiv.com/http/win32/ffxivneo_release_boot/{FakeGameDirectory.BOOT_VERSION}/?time=2026-10-06-13-40", request.Url);
        Assert.Equal
        (
            new Dictionary<string, string> { ["User-Agent"] = "FFXIV PATCH CLIENT", ["Host"] = "patch-bootver.ffxiv.com" },
            request.Headers
        );
    }

    [Fact]
    public async Task BootCheck_ErrorStatus_Throws_InsteadOfReportingUpdate()
    {
        using var game = new FakeGameDirectory();
        using var launcher = NewLauncher(new FakeHttpHandler(_ => FakeHttpHandler.Text("<html>502</html>", HttpStatusCode.BadGateway)));

        var ex = await Assert.ThrowsAsync<InternationalInvalidResponseException>(() => launcher.IsBootUpdateRequiredAsync(game.Root));

        Assert.Equal(InternationalInvalidResponseKind.BootCheckFailed, ex.Kind);
    }

    [Fact]
    public async Task LoginStatus_And_GateStatus_UseLauncherHeaders()
    {
        var handler = new FakeHttpHandler
        (request => request.Url.Contains("login_status", StringComparison.Ordinal)
                        ? FakeHttpHandler.Text("{\"status\":1}".Replace("1", "true"))
                        : FakeHttpHandler.Text("{\"status\":false,\"message\":[\"All Worlds Maintenance\",\"until 10:00 GMT\"],\"news\":[\"n1\"]}")
        );
        using var launcher = NewLauncher(handler);

        var login = await launcher.GetLoginStatusAsync();
        var gate  = await launcher.GetGateStatusAsync(ClientLanguage.Japanese);

        Assert.True(login.Status);
        Assert.False(gate.Status);
        Assert.Equal(["All Worlds Maintenance", "until 10:00 GMT"], gate.Message);
        Assert.Equal(["n1"], gate.News);

        var millis = new DateTimeOffset(Now).ToUnixTimeMilliseconds();
        Assert.Equal($"https://frontier.ffxiv.com/worldStatus/login_status.json?_={millis}", handler.Requests[0].Url);
        Assert.Equal($"https://frontier.ffxiv.com/worldStatus/gate_status.json?lang=ja&_={millis}", handler.Requests[1].Url);
        Assert.Equal
        (
            new Dictionary<string, string>
            {
                ["User-Agent"]      = "SQEXAuthor/2.0.0(Windows 6.2; ja-jp; 0011223344)",
                ["Accept-Encoding"] = "gzip, deflate",
                ["Accept-Language"] = ACCEPT_LANG,
                ["Origin"]          = "https://launcher.finalfantasyxiv.com",
                ["Referer"]         = "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang=ja&time=2026-10-06-13-47",
                ["Connection"]      = "Keep-Alive"
            },
            handler.Requests[1].Headers
        );

        // 登录状态接口固定按英语生成 Referer（goatcorp Launcher.cs:647）
        Assert.Equal("https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang=en_gb&time=2026-10-06-13-47", handler.Requests[0].Headers["Referer"]);
    }

    #endregion

    #region 启动参数

    [Fact]
    public void GameArguments_EightKeysInGoatcorpOrder()
    {
        var arguments = InternationalLauncher.BuildGameArguments(UNIQUE_ID, 3, 5, ClientLanguage.English, FakeGameDirectory.GAME_VERSION);

        Assert.Equal
        (
            " DEV.DataPathType=1 DEV.MaxEntitledExpansionID=5 " +
            $"DEV.TestSID={UNIQUE_ID} DEV.UseSqPack=1 SYS.Region=3 language=1 resetConfig=0 ver={FakeGameDirectory.GAME_VERSION}",
            arguments.Build()
        );
    }

    [Theory]
    [InlineData(ClientLanguage.Japanese, "language=0")]
    [InlineData(ClientLanguage.German, "language=2")]
    [InlineData(ClientLanguage.French, "language=3")]
    public void GameArguments_LanguageNumber(ClientLanguage language, string expected)
    {
        Assert.Contains(expected, InternationalLauncher.BuildGameArguments("u", 1, 0, language, "v").Build());
    }

    [Fact]
    public void GameArguments_AdditionalArguments_AreAppendedAfterVer()
    {
        var built = InternationalLauncher.BuildGameArguments("u", 1, 0, ClientLanguage.English, "v", "SYS.FPS=60  UI.Scale = 2").Build();

        Assert.EndsWith(" ver=v SYS.FPS=60 UI.Scale=2", built);
    }

    [Fact]
    public void GameArguments_Encrypted_HasSqexEnvelope_AndHidesUniqueId()
    {
        var encrypted = InternationalLauncher.BuildGameArguments(UNIQUE_ID, 3, 5, ClientLanguage.English, "v").BuildEncrypted(0x12340000);

        Assert.StartsWith("//**sqex0003", encrypted);
        Assert.EndsWith("**//", encrypted);
        Assert.DoesNotContain(UNIQUE_ID, encrypted);
    }

    [Fact]
    public void GameExePath_IsUnderGameFolder()
    {
        Assert.Equal(@"D:\FFXIV\game\ffxiv_dx11.exe", InternationalLauncher.GetGameExePath(new DirectoryInfo(@"D:\FFXIV")));
    }

    #endregion
}
