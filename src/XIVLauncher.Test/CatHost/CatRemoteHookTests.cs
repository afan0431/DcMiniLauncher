using System.IO;
using XIVLauncher.CatHost;
using XIVLauncher.InGame;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     远程截取模块的注入: 管道名插入进程号、init 参数格式、init 返回值与注入失败折成的结果
/// </summary>
public sealed class CatRemoteHookTests : IDisposable
{
    private const string HEX   = "0123456789abcdef0123456789abcdef";
    private const string PIPE  = @"\\.\pipe\cat-remote-" + HEX;
    private const string TOKEN = "remote-hook-token-0123456789abcdef";

    private readonly string dllPath = Path.Combine(Path.GetTempPath(), $"cat-remote-hook-{Guid.NewGuid():N}.dll");

    public CatRemoteHookTests() => File.WriteAllBytes(dllPath, [0]);

    public void Dispose() => File.Delete(dllPath);

    private CatRemoteHookParams Params => new(dllPath, PIPE, TOKEN);

    [Fact]
    public void PipeNameFor_InsertsTheProcessId() =>
        Assert.Equal($@"\\.\pipe\cat-remote-4242-{HEX}", CatRemoteHook.PipeNameFor(PIPE, 4242));

    [Fact]
    public void Validate_AcceptsWellFormedParams() =>
        Assert.Null(CatRemoteHook.Validate(new CatRemoteHookParams(@"C:\Cat\cat_remote_hook.dll", PIPE, TOKEN)));

    [Fact]
    public void Ready_LoadsThenCallsInitWithPipeAndToken()
    {
        var game = new FakeTarget();

        var result = CatRemoteHook.Inject(game, Params);

        Assert.Equal(CatRemoteHookResult.Ready($@"\\.\pipe\cat-remote-4242-{HEX}"), result);
        Assert.Equal([$"load:{dllPath}", $"init:{dllPath}"], game.Calls);
        Assert.Equal($"\\\\.\\pipe\\cat-remote-4242-{HEX}\t{TOKEN}", game.InitArgument);
    }

    [Fact]
    public void HookNotInstalled_KeepsThePipeSoTheHostCanReadWhy()
    {
        var game = new FakeTarget { InitResult = CatRemoteHook.INIT_HOOK_FAILED };

        var result = CatRemoteHook.Inject(game, Params);

        Assert.False(result.Ok);
        Assert.Equal(CatCodes.REMOTE_HOOK_FAILED, result.Code);
        Assert.Equal($@"\\.\pipe\cat-remote-4242-{HEX}", result.PipeName);
    }

    [Theory]
    [InlineData(CatRemoteHook.INIT_BAD_ARGUMENT)]
    [InlineData(CatRemoteHook.INIT_PIPE_FAILED)]
    [InlineData(CatRemoteHook.INIT_PANICKED)]
    [InlineData(99u)]
    public void InitFailure_HasNoPipe(uint initResult)
    {
        var result = CatRemoteHook.Inject(new FakeTarget { InitResult = initResult }, Params);

        Assert.False(result.Ok);
        Assert.Null(result.PipeName);
        Assert.Equal(CatCodes.REMOTE_HOOK_FAILED, result.Code);
    }

    [Fact]
    public void AccessDenied_IsReportedAsElevated()
    {
        var game = new FakeTarget { LoadFailure = new InjectFailure("OpenProcess 失败 (err=5)", true) };

        var result = CatRemoteHook.Inject(game, Params);

        Assert.Equal(CatCodes.REMOTE_TARGET_ELEVATED, result.Code);
        Assert.Equal([$"load:{dllPath}"], game.Calls);
    }

    [Fact]
    public void LoadFailure_DoesNotCallInit()
    {
        var game = new FakeTarget { LoadFailure = new InjectFailure("LoadLibraryW 失败") };

        var result = CatRemoteHook.Inject(game, Params);

        Assert.Equal(CatCodes.REMOTE_HOOK_FAILED, result.Code);
        Assert.Contains("LoadLibraryW 失败", result.Message);
        Assert.Equal([$"load:{dllPath}"], game.Calls);
    }

    [Fact]
    public void InitCallFailure_IsReported()
    {
        var game = new FakeTarget { InitFailure = new InjectFailure("没有导出 cat_remote_init") };

        var result = CatRemoteHook.Inject(game, Params);

        Assert.Equal(CatCodes.REMOTE_HOOK_FAILED, result.Code);
        Assert.Contains("cat_remote_init", result.Message);
    }

    [Fact]
    public void MissingDll_OrExitedGame_TouchesNothing()
    {
        var missing = new FakeTarget();
        var exited  = new FakeTarget { HasExited = true };

        Assert.Equal(CatCodes.REMOTE_HOOK_FAILED, CatRemoteHook.Inject(missing, Params with { DllPath = dllPath + ".missing.dll" }).Code);
        Assert.Equal(CatCodes.REMOTE_HOOK_FAILED, CatRemoteHook.Inject(exited, Params).Code);
        Assert.Empty(missing.Calls);
        Assert.Empty(exited.Calls);
    }

    [Fact]
    public void ThrowingTarget_IsFoldedIntoAResult()
    {
        var result = CatRemoteHook.Inject(new FakeTarget { Throw = true }, Params);

        Assert.False(result.Ok);
        Assert.Equal(CatCodes.REMOTE_HOOK_FAILED, result.Code);
    }

    private sealed class FakeTarget : ICatRemoteHookTarget
    {
        public List<string> Calls { get; } = [];

        public string? InitArgument { get; private set; }

        public uint InitResult { get; init; } = CatRemoteHook.INIT_OK;

        public InjectFailure? LoadFailure { get; init; }

        public InjectFailure? InitFailure { get; init; }

        public bool Throw { get; init; }

        public int Pid => 4242;

        public bool HasExited { get; init; }

        public InjectFailure? LoadLibrary(string path)
        {
            if (Throw)
                throw new InvalidOperationException("boom");

            Calls.Add($"load:{path}");
            return LoadFailure;
        }

        public InjectFailure? CallInit(string path, string argument, out uint result)
        {
            Calls.Add($"init:{path}");
            InitArgument = argument;
            result       = InitResult;
            return InitFailure;
        }
    }
}
