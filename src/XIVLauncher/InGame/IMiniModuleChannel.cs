namespace XIVLauncher.InGame;

/// <summary>
///     向游戏内模块发命令、收回应的通道。真实实现是 <see cref="MiniModuleClient" />（命名管道）, 测试里换成假模块。
/// </summary>
public interface IMiniModuleChannel
{
    /// <summary>发一条命令并等回应。回应形如 <c>OK …</c> / <c>FAIL …</c>, 原样返回给调用方判读。</summary>
    Task<string> SendAsync(string command, CancellationToken cancellationToken);
}
