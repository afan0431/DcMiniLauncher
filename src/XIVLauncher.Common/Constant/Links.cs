namespace XIVLauncher.Common.Constant;

public static class Links
{
    #region 杂项

    /// <remarks>GitHub 反代</remarks>
    public const string GITHUB_PROXY_BASE_URL = "https://gh.atmoomen.top";
    
    /// <remarks>判断网络出口 (Cloudflare)</remarks>
    public const string CLOUDFLARE_TRACE_URL = "https://www.cloudflare.com/cdn-cgi/trace";
    
    /// <remarks>判断网络出口 (IPIP)</remarks>
    public const string IPIP_LOCATION_URL = "https://myip.ipip.net/json";

    #endregion

    
    #region 软件官网

    /// <remarks>GitHub 仓库页面</remarks>
    public const string REPO_URL = "https://github.com/AtmoOmen/FFXIVQuickLauncher";

    /// <remarks>Discord 服务器</remarks>
    public const string DISCORD_URL = "https://discord.gg/MDvv8Ejntw";

    #endregion
    

    #region 启动器

    /// <remarks>
    ///     启动器更新源。DcMiniLauncher: 指向本 fork 的 GitHub Releases, 不能用上游的
    ///     (https://xl-dis.atmoomen.top) —— 那会把本 fork 「更新」回上游版本。
    ///     每个 Release 都附带只含本版本的 releases.win.json + 完整 nupkg,
    ///     latest/download/&lt;文件名&gt; 永远重定向到最新 Release 的同名附件, 所以 SimpleWebSource 直接可用。
    /// </remarks>
    public const string LAUNCHER_DISTRIBUTE_BASE_URL = "https://github.com/afan0431/DcMiniLauncher/releases/latest/download";
    
    #endregion

    
    #region Dalamud

    /// <remarks>Dalamud (Cloudflare R2)</remarks>
    public const string DALAMUD_DISTRIBUTE_R2_BASE_URL = "https://dalamud-dis.atmoomen.top";

    /// <remarks>Dalamud 版本 (Cloudflare R2)</remarks>
    public const string DALAMUD_DISTRIBUTE_R2_VERSION_URL = $"{DALAMUD_DISTRIBUTE_R2_BASE_URL}/RELEASE";

    #endregion

    
    #region 国际服 Dalamud（goatcorp 原版）

    // 取自 goatcorp/FFXIVQuickLauncher（GPL-3.0）提交 a6f33a9。与上面国服那套源互不相干, 文件格式也完全不同

    /// <remarks>版本信息, {0} = 分桶（Canary / Control）。出处: XIVLauncher.Common/Dalamud/DalamudLauncher.cs:48; DalamudUpdater.cs:176</remarks>
    public const string GOATCORP_DALAMUD_VERSION_INFO_URL_FORMAT = "https://kamori.goats.dev/Dalamud/Release/VersionInfo?track=release&bucket={0}";

    /// <remarks>.NET 运行时哈希清单, {0} = 运行时版本。出处: DalamudUpdater.cs:501</remarks>
    public const string GOATCORP_DALAMUD_RUNTIME_HASHES_URL_FORMAT = "https://kamori.goats.dev/Dalamud/Release/Runtime/Hashes/{0}";

    /// <remarks>.NET 运行时, {0} = 运行时版本。出处: DalamudUpdater.cs:535</remarks>
    public const string GOATCORP_DALAMUD_RUNTIME_DOTNET_URL_FORMAT = "https://kamori.goats.dev/Dalamud/Release/Runtime/DotNet/{0}";

    /// <remarks>.NET 桌面运行时, {0} = 运行时版本。出处: DalamudUpdater.cs:536</remarks>
    public const string GOATCORP_DALAMUD_RUNTIME_DESKTOP_URL_FORMAT = "https://kamori.goats.dev/Dalamud/Release/Runtime/WindowsDesktop/{0}";

    /// <remarks>资源清单。出处: XIVLauncher.Common/Dalamud/AssetManager.cs:19</remarks>
    public const string GOATCORP_DALAMUD_ASSET_META_URL = "https://kamori.goats.dev/Dalamud/Asset/Meta";

    #endregion


    #region Dalamud 资源

    /// <remarks>资源 (Cloudflare R2)</remarks>
    public const string DALAMUD_ASSET_DISTRIBUTE_R2_BASE_URL = $"{DALAMUD_DISTRIBUTE_R2_BASE_URL}/assets";
    
    /// <remarks>资源版本 (Cloudflare R2)</remarks>
    public const string DALAMUD_ASSET_DISTRIBUTE_R2_VERSION_URL = $"{DALAMUD_ASSET_DISTRIBUTE_R2_BASE_URL}/RELEASE";

    #endregion
    
    
    #region 运行时环境

    /// <remarks>运行时版本</remarks>
    public const string DALAMUD_RUNTIME_INFO_URL = $"{GITHUB_PROXY_BASE_URL}/{DALAMUD_RUNTIME_INFO_RAW_URL}";

    /// <remarks>原始运行时版本</remarks>
    public const string DALAMUD_RUNTIME_INFO_RAW_URL = "https://raw.githubusercontent.com/Dalamud-DailyRoutines/XLCNSoilAssets/master/runtimeInfo";
    
    /// <remarks>微软的 NuGet 源</remarks>
    public const string NUGET_V3_FLAT_CONTAINER_URL = "https://api.nuget.org/v3-flatcontainer";

    /// <remarks>华为的 NuGet 镜像源</remarks>
    public const string HUAWEI_NUGET_V3_REMOTE_URL = "https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote";

    #endregion
    
    
    #region 登录 API

    /// <remarks>请求头</remarks>
    public const string SDO_LAUNCHER_REFERER_URL = "https://ff.web.sdo.com/project/launcher0904/index.html";

    /// <remarks>大区列表</remarks>
    public const string SDO_LOGIN_AREA_URL = "https://ff.dorado.sdo.com/ff/area/serverlist_new.js";

    /// <remarks>总的服务地址</remarks>
    public const string SDO_SERVICE_URL = "http://www.sdo.com";

    #endregion
    
    
    #region 国际服（Square Enix）

    // 以下地址与请求头取自 goatcorp/FFXIVQuickLauncher（GPL-3.0）提交 a6f33a9, 各项注明出处行号

    /// <remarks>登录页, {0} = 免费试玩标志（0 / 1）; rgn 固定 3, lng 固定 en。出处: XIVLauncher.Common/Game/Launcher.cs:526-530, 160</remarks>
    public const string SE_OAUTH_TOP_URL_FORMAT = "https://ffxiv-login.square-enix.com/oauth/ffxivarr/login/top?lng=en&rgn=3&isft={0}&cssmode=1&isnew=1&launchver=3";

    /// <remarks>提交登录。出处: Launcher.cs:556-557</remarks>
    public const string SE_OAUTH_SEND_URL = "https://ffxiv-login.square-enix.com/oauth/ffxivarr/login/login.send";

    /// <remarks>登录服务器主机名（login.send 的 Host 头）。出处: Launcher.cs:565</remarks>
    public const string SE_OAUTH_HOST = "ffxiv-login.square-enix.com";

    /// <remarks>上报游戏版本, {0} = game 版本, {1} = 登录得到的会话值。出处: Launcher.cs:401-402</remarks>
    public const string SE_PATCH_GAMEVER_URL_FORMAT = "https://patch-gamever.ffxiv.com/http/win32/ffxivneo_release_game/{0}/{1}";

    /// <remarks>boot 版本检查（官方就是 http）, {0} = boot 版本, {1} = 时间。出处: Launcher.cs:373-375</remarks>
    public const string SE_PATCH_BOOTVER_URL_FORMAT = "http://patch-bootver.ffxiv.com/http/win32/ffxivneo_release_boot/{0}/?time={1}";

    /// <remarks>boot 版本检查的 Host 头。出处: Launcher.cs:378</remarks>
    public const string SE_PATCH_BOOTVER_HOST = "patch-bootver.ffxiv.com";

    /// <remarks>登录服务是否开放, {0} = 毫秒时间戳。出处: Launcher.cs:647</remarks>
    public const string SE_LOGIN_STATUS_URL_FORMAT = "https://frontier.ffxiv.com/worldStatus/login_status.json?_={0}";

    /// <remarks>游戏是否开放（维护）, {0} = 语言代码, {1} = 毫秒时间戳。出处: Launcher.cs:631</remarks>
    public const string SE_GATE_STATUS_URL_FORMAT = "https://frontier.ffxiv.com/worldStatus/gate_status.json?lang={0}&_={1}";

    /// <remarks>状态接口的 Origin 头。出处: Launcher.cs:689</remarks>
    public const string SE_LAUNCHER_ORIGIN = "https://launcher.finalfantasyxiv.com";

    /// <remarks>
    ///     登录页地址模板（Referer 用）等运行时配置, 不在 goatcorp 源码里, 由它的服务器下发。
    ///     出处: XIVLauncher.Common/Util/DebugHelpers.cs:77-94
    /// </remarks>
    public const string GOATCORP_LAUNCHER_CLIENT_CONFIG_URL = "https://kamori.goats.dev/Launcher/GetLauncherClientConfig";

    /// <remarks>
    ///     登录页地址模板的内置兜底值: 2026-10-06 从上面的接口实测取得。只在接口取不到、本地也没有上次成功的缓存时才用, 可能已过期。
    ///     {0} = 语言代码（- 换成 _）, {1} = UTC 时间 yyyy-MM-dd-HH-mm
    /// </remarks>
    public const string SE_FRONTIER_URL_TEMPLATE_BUILTIN = "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}";

    #endregion


    #region 新闻 API

    /// <remarks>文章正文</remarks>
    public const string SDO_NEWS_ARTICLE_BASE_URL = "https://ff.web.sdo.com/web8/index.html#/newstab/newscont/";

    /// <remarks>轮播图</remarks>
    public const string SDO_NEWS_BANNER_API_URL = "https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=5203&pageIndex=0&pageSize=8";

    /// <remarks>文章列表</remarks>
    public const string SDO_NEWS_LIST_API_URL = "https://cqnews.web.sdo.com/api/news/newsList?gameCode=ff&CategoryCode=8324,8325,8326,8327,5309,5310,5311,5312,5313&pageIndex=0&pageSize=16";

    #endregion
    
    
    #region 盛趣官方网站

    /// <remarks>超域旅行官网</remarks>
    public const string DC_TRAVEL_PAGE_URL = "https://ff14bjz.sdo.com/RegionKanTelepo";

    /// <remarks>充值官网</remarks>
    public const string SDO_PAYMENT_URL = $"https://pay.sdo.com/item/GWPAY-{SdoInfos.APP_ID}/";

    /// <remarks>商城官网</remarks>
    public const string SDO_SHOPPING_URL = "https://qu.sdo.com/game/1";

    /// <remarks>石之家社区</remarks>
    public const string RISING_STONE_URL = "https://ff14risingstones.web.sdo.com/pc/#/post";

    /// <remarks>官方哔哩哔哩账号</remarks>
    public const string SDO_BILIBILI_URL = "https://space.bilibili.com/6655514";

    /// <remarks>官方小红书账号</remarks>
    public const string SDO_XIAOHONGSHU_URL = "https://www.xiaohongshu.com/user/profile/5f814cbe0000000001003455";

    /// <remarks>官方微博账号</remarks>
    public const string SDO_WEIBO_URL = "https://weibo.com/u/1797798792";

    /// <remarks>官方抖音账号</remarks>
    public const string SDO_DOUYIN_URL = "https://www.douyin.com/user/MS4wLjABAAAAHJts6kVkO7Lob9_H5VMSc3UZXCSq6gw5s02kplXQ7k0";

    #endregion
}
