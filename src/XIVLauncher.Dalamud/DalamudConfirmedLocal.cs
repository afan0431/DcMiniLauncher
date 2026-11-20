using Newtonsoft.Json;
using Serilog;

namespace XIVLauncher.Dalamud;

/// <summary>
///     发行源最近一次确认过的本地 Dalamud: 哪一版、它的 hashes.json 哈希、配的 .NET 运行时, 以及确认时游戏是哪一版。
///     连不上发行源（查不到最新版本号）时, 只要没有迹象说明本地这份过时了, 就用它起游戏, 不让一次网络抖动挡住上号。
///     游戏版本是判过时的依据: 国服 Dalamud 随游戏补丁换版, 游戏换了版而发行源又问不到, 本地这份就不一定适用。
/// </summary>
public sealed record DalamudConfirmedLocal(string Version, string Hash, string Runtime, string? GameVersion)
{
    private const string FILE_NAME = "confirmed.json";

    /// <summary>记录放在 Hooks 目录下, 与各版本目录同级, 多个启动器进程共用</summary>
    public static FileInfo PathIn(DirectoryInfo hooksDirectory) =>
        new(Path.Combine(hooksDirectory.FullName, FILE_NAME));

    public static DalamudConfirmedLocal? Read(DirectoryInfo hooksDirectory)
    {
        var file = PathIn(hooksDirectory);

        try
        {
            if (!file.Exists)
                return null;

            var record = JsonConvert.DeserializeObject<DalamudConfirmedLocal>(File.ReadAllText(file.FullName));
            return record is { Version.Length: > 0, Hash.Length: > 0, Runtime.Length: > 0 } ? record : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DUPDATE] 读不出 {File}", file.FullName);
            return null;
        }
    }

    /// <summary>先写临时文件再换名, 别的进程同时读也不会读到半截</summary>
    public void Write(DirectoryInfo hooksDirectory)
    {
        var file = PathIn(hooksDirectory);
        var temp = $"{file.FullName}.{Environment.ProcessId}.tmp";

        try
        {
            hooksDirectory.Create();
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(temp, file.FullName, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DUPDATE] 写不了 {File}", file.FullName);

            try
            {
                File.Delete(temp);
            }
            catch
            {
                // 临时文件, 删不掉不影响结果
            }
        }
    }

    /// <summary>
    ///     发行源确认了 <paramref name="version" /> 是最新版: 版本没变就保留已记下的游戏版本, 换了版就清掉, 等下次真正起游戏时再记
    /// </summary>
    public static void Confirm(DirectoryInfo hooksDirectory, string version, string hash, string runtime)
    {
        var old = Read(hooksDirectory);
        var gameVersion = old != null && old.Version == version && old.Hash == hash ? old.GameVersion : null;
        var record = new DalamudConfirmedLocal(version, hash, runtime, gameVersion);

        if (record != old)
            record.Write(hooksDirectory);
    }

    /// <summary>用发行源确认过的这一版起了游戏: 记下当时的游戏版本</summary>
    public static void ConfirmGameVersion(DirectoryInfo hooksDirectory, string version, string gameVersion)
    {
        var old = Read(hooksDirectory);

        if (old == null || old.Version != version || old.GameVersion == gameVersion)
            return;

        (old with { GameVersion = gameVersion }).Write(hooksDirectory);
    }
}
