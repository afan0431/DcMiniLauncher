using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace XIVLauncher.Test.International;

/// <summary>
///     假 HTTP: 记下每个请求（含正文）, 按顺序或按地址给出预设的响应。不联网。
/// </summary>
internal sealed class FakeHttpHandler(Func<FakeHttpHandler.Recorded, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, string Url, Dictionary<string, string> Headers, string? Body, string? ContentType);

    public List<Recorded> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 取未经解析的原始头值（与实际发出去的一致）
        var headers = request.Headers.NonValidated.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var body    = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var record  = new Recorded(request.Method, request.RequestUri!.OriginalString, headers, body, request.Content?.Headers.ContentType?.ToString());

        lock (Requests)
            Requests.Add(record);

        return respond(record);
    }

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/html") };
}

/// <summary>
///     临时的假国际服游戏目录: boot 下四个可执行文件 + 各 .ver
/// </summary>
internal sealed class FakeGameDirectory : IDisposable
{
    public const string BOOT_VERSION = "2026.05.01.0000.0001";
    public const string GAME_VERSION = "2026.09.15.0000.0000";

    public FakeGameDirectory(int expansions = 5, bool withBootFiles = true)
    {
        Root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "dml-intl-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(Root.FullName, "boot"));
        Directory.CreateDirectory(Path.Combine(Root.FullName, "game"));

        File.WriteAllText(Path.Combine(Root.FullName, "boot", "ffxivboot.ver"), BOOT_VERSION);
        File.WriteAllText(Path.Combine(Root.FullName, "game", "ffxivgame.ver"), GAME_VERSION);

        if (withBootFiles)
        {
            File.WriteAllBytes(Path.Combine(Root.FullName, "boot", "ffxivboot.exe"), "abc"u8.ToArray());
            File.WriteAllBytes(Path.Combine(Root.FullName, "boot", "ffxivboot64.exe"), []);
            File.WriteAllBytes(Path.Combine(Root.FullName, "boot", "ffxivlauncher64.exe"), "hello"u8.ToArray());
            File.WriteAllBytes(Path.Combine(Root.FullName, "boot", "ffxivupdater64.exe"), "abc"u8.ToArray());
        }

        for (var i = 1; i <= expansions; i++)
        {
            var dir = Path.Combine(Root.FullName, "game", "sqpack", $"ex{i}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"ex{i}.ver"), ExVersion(i));
        }
    }

    public DirectoryInfo Root { get; }

    public static string ExVersion(int i) => $"2026.09.0{i}.0000.0000";

    /// <summary>SHA1("abc")</summary>
    public const string SHA1_ABC = "a9993e364706816aba3e25717850c26c9cd0d89d";

    /// <summary>SHA1("")</summary>
    public const string SHA1_EMPTY = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    /// <summary>SHA1("hello")</summary>
    public const string SHA1_HELLO = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d";

    public static string BootHashLine =>
        $"{BOOT_VERSION}=ffxivboot.exe/3/{SHA1_ABC},ffxivboot64.exe/0/{SHA1_EMPTY},ffxivlauncher64.exe/5/{SHA1_HELLO},ffxivupdater64.exe/3/{SHA1_ABC}";

    public void Dispose()
    {
        try
        {
            Root.Delete(true);
        }
        catch
        {
            // ignored
        }
    }
}

/// <summary>
///     把测试期间的 Serilog 输出（含异常全文）收进内存, 用来查敏感值有没有被写进本地日志
/// </summary>
internal sealed class CapturedLogs : ILogEventSink, IDisposable
{
    private readonly ILogger previous = Log.Logger;
    private readonly List<string> lines = [];

    public CapturedLogs() =>
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();

    public void Emit(LogEvent logEvent)
    {
        lock (lines)
            lines.Add(logEvent.RenderMessage() + (logEvent.Exception == null ? string.Empty : "\n" + logEvent.Exception));
    }

    public string All
    {
        get
        {
            lock (lines)
                return string.Join("\n", lines);
        }
    }

    public void Dispose() =>
        Log.Logger = previous;
}

/// <summary>会改全局 Serilog 日志器的测试放同一组, 不并行</summary>
[CollectionDefinition(NAME, DisableParallelization = true)]
public sealed class SerilogCaptureCollection
{
    public const string NAME = "SerilogCapture";
}

/// <summary>
///     联网冒烟测试: 缺省跳过, 设环境变量 DML_NETWORK_SMOKE=1 才跑
/// </summary>
public sealed class NetworkSmokeFactAttribute : FactAttribute
{
    public NetworkSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DML_NETWORK_SMOKE") != "1")
            Skip = "联网冒烟测试, 设 DML_NETWORK_SMOKE=1 才跑";
    }
}
