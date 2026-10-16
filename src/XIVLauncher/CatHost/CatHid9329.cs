using System.IO.Ports;
using Serilog;

namespace XIVLauncher.CatHost;

/// <summary>
///     工作台外壳靠程序集里有没有这个类型名判断启动器支不支持「用本机硬件键鼠自动输入 WeGame 账号密码」, 名字和命名空间不能改。
/// </summary>
public static class CatWeGameHwLogin
{
    /// <summary>能力标记名, 和工作台外壳 support.rs 里查的字符串一致</summary>
    public const string CAPABILITY = "weGameHwLogin";
}

/// <summary>硬件键盘上的单个按键 (USB HID Usage, 键盘页)</summary>
public enum HidKey : byte
{
    /// <summary>无</summary>
    None = 0x00,

    /// <summary>字母 A (配合 Ctrl 做全选)</summary>
    A = 0x04,

    /// <summary>回车</summary>
    Enter = 0x28,

    /// <summary>Tab</summary>
    Tab = 0x2B,

    /// <summary>退格</summary>
    Backspace = 0x2A,

    /// <summary>Delete</summary>
    Delete = 0x4C,
}

/// <summary>硬件键盘的修饰键 (USB HID 第 1 字节的位)</summary>
[Flags]
public enum HidModifiers : byte
{
    /// <summary>无</summary>
    None = 0x00,

    /// <summary>左 Ctrl</summary>
    Ctrl = 0x01,

    /// <summary>左 Shift</summary>
    Shift = 0x02,

    /// <summary>左 Alt</summary>
    Alt = 0x04,
}

/// <summary>
///     本机外接的硬件键鼠 (CH9329): 对当前前台窗口模拟真实的键盘输入和鼠标点击。
///     <para>
///         WeGame 的密码框有安全键盘, 软件模拟的按键 (SendInput 等) 打不进去;
///         硬件键鼠在操作系统看来就是一套真实的 USB 键鼠, 所以能打进去。见 task 10-16。
///     </para>
///     <para>硬件按键只会落到当前前台窗口。调用方务必在输入前把目标窗口切到前台、并在每次输入前后核对前台窗口。</para>
/// </summary>
public interface ICatHidKeyboard
{
    /// <summary>找到并打开本机的硬件键鼠; 没接 / 没应答返回 false。可重复调用。</summary>
    bool TryOpen();

    /// <summary>打开成功后用到的串口名 (如 COM3); 没打开则为 null</summary>
    string? PortName { get; }

    /// <summary>
    ///     逐字符把一段文字打出来。遇到硬件键盘打不出的字符 (非 US 布局可打印的 ASCII) 时,
    ///     一个字符都不打、返回 false, 让调用方退回人工输入。
    /// </summary>
    Task<bool> TypeTextAsync(string text, CancellationToken ct);

    /// <summary>按下并抬起一个 (带修饰键的) 按键, 如 Ctrl+A、Delete</summary>
    Task PressAsync(HidModifiers modifiers, HidKey key, CancellationToken ct);

    /// <summary>在主屏的某个像素位置点一下左键 (绝对定位)</summary>
    Task ClickAsync(int screenX, int screenY, int screenWidth, int screenHeight, CancellationToken ct);
}

/// <summary>和硬件之间收发字节的底层通道, 抽出来好让测试不接真串口</summary>
public interface ICatHidTransport
{
    /// <summary>本机所有串口名</summary>
    IReadOnlyList<string> PortNames();

    /// <summary>打开一个串口; 打不开返回 null</summary>
    ICatHidPort? Open(string portName);
}

/// <summary>一个已打开的串口</summary>
public interface ICatHidPort : IDisposable
{
    /// <summary>发出一帧</summary>
    void Write(byte[] frame);

    /// <summary>读回最多 count 个字节, 超时就返回已读到的; 读不到返回空数组</summary>
    byte[] Read(int count, TimeSpan timeout);
}

/// <summary>
///     CH9329 的串口帧格式与键码 / 坐标换算。全是纯函数, 单测直接覆盖, 不碰硬件。
///     帧: 0x57 0xAB 0x00 CMD LEN DATA... SUM, SUM = 前面所有字节之和的低 8 位。
/// </summary>
internal static class CatHid9329Protocol
{
    /// <summary>串口波特率 (CH9329 出厂默认)</summary>
    internal const int BAUD_RATE = 9600;

    /// <summary>鼠标绝对坐标的满量程</summary>
    internal const int ABS_FULL_SCALE = 4096;

    private const byte H0 = 0x57;
    private const byte H1 = 0xAB;
    private const byte ADDR = 0x00;

    private const byte CMD_GET_INFO = 0x01;
    private const byte CMD_KEYBOARD = 0x02;
    private const byte CMD_MOUSE_ABS = 0x04;

    private const byte REPLY_GET_INFO = 0x81;

    /// <summary>GET_INFO 查询帧; 用来确认某个串口后面接的是 CH9329</summary>
    internal static byte[] InfoFrame() => Frame(CMD_GET_INFO, []);

    /// <summary>一帧键盘报文: 修饰键 + 最多 6 个同时按下的键</summary>
    internal static byte[] KeyboardFrame(HidModifiers modifiers, params HidKey[] keys)
    {
        var data = new byte[8];
        data[0] = (byte)modifiers;
        // data[1] 保留 0
        for (var i = 0; i < keys.Length && i < 6; i++)
            data[2 + i] = (byte)keys[i];
        return Frame(CMD_KEYBOARD, data);
    }

    /// <summary>所有键抬起</summary>
    internal static byte[] KeyboardReleaseFrame() => Frame(CMD_KEYBOARD, new byte[8]);

    /// <summary>一帧鼠标绝对移动 + 左键状态 (button: 0 抬起, 1 左键按下)</summary>
    internal static byte[] MouseAbsFrame(byte button, int absX, int absY)
    {
        var data = new byte[]
        {
            0x02, // 绝对坐标模式
            button,
            (byte)(absX & 0xFF), (byte)((absX >> 8) & 0xFF),
            (byte)(absY & 0xFF), (byte)((absY >> 8) & 0xFF),
            0x00 // 滚轮
        };
        return Frame(CMD_MOUSE_ABS, data);
    }

    /// <summary>屏幕像素 → 0..4095 绝对坐标</summary>
    internal static int ToAbs(int pixel, int span)
    {
        if (span <= 0) return 0;
        var v = (int)((long)pixel * ABS_FULL_SCALE / span);
        return Math.Clamp(v, 0, ABS_FULL_SCALE - 1);
    }

    /// <summary>收到的字节是不是一帧合法的 GET_INFO 应答</summary>
    internal static bool IsInfoReply(byte[] bytes)
    {
        if (bytes.Length < 6 || bytes[0] != H0 || bytes[1] != H1 || bytes[3] != REPLY_GET_INFO)
            return false;

        int len = bytes[4];
        var total = 5 + len + 1;
        if (bytes.Length < total)
            return false;

        return Checksum(bytes, total - 1) == bytes[total - 1];
    }

    /// <summary>
    ///     一个字符 → (修饰键, 键码)。只认 US 布局能直接打出的 ASCII 可打印字符;
    ///     其它字符返回 false, 调用方据此判定不能自动输入。
    /// </summary>
    internal static bool TryMapChar(char c, out HidModifiers modifiers, out HidKey key)
    {
        modifiers = HidModifiers.None;
        key = HidKey.None;

        switch (c)
        {
            case >= 'a' and <= 'z':
                key = (HidKey)(0x04 + (c - 'a'));
                return true;
            case >= 'A' and <= 'Z':
                modifiers = HidModifiers.Shift;
                key = (HidKey)(0x04 + (c - 'A'));
                return true;
            case >= '1' and <= '9':
                key = (HidKey)(0x1E + (c - '1'));
                return true;
            case '0':
                key = (HidKey)0x27;
                return true;
        }

        // 其余可打印 ASCII: (是否按 Shift, 键码)
        var map = c switch
        {
            ' '  => (false, (byte)0x2C),
            '-'  => (false, (byte)0x2D),
            '='  => (false, (byte)0x2E),
            '['  => (false, (byte)0x2F),
            ']'  => (false, (byte)0x30),
            '\\' => (false, (byte)0x31),
            ';'  => (false, (byte)0x33),
            '\'' => (false, (byte)0x34),
            '`'  => (false, (byte)0x35),
            ','  => (false, (byte)0x36),
            '.'  => (false, (byte)0x37),
            '/'  => (false, (byte)0x38),
            '!'  => (true,  (byte)0x1E),
            '@'  => (true,  (byte)0x1F),
            '#'  => (true,  (byte)0x20),
            '$'  => (true,  (byte)0x21),
            '%'  => (true,  (byte)0x22),
            '^'  => (true,  (byte)0x23),
            '&'  => (true,  (byte)0x24),
            '*'  => (true,  (byte)0x25),
            '('  => (true,  (byte)0x26),
            ')'  => (true,  (byte)0x27),
            '_'  => (true,  (byte)0x2D),
            '+'  => (true,  (byte)0x2E),
            '{'  => (true,  (byte)0x2F),
            '}'  => (true,  (byte)0x30),
            '|'  => (true,  (byte)0x31),
            ':'  => (true,  (byte)0x33),
            '"'  => (true,  (byte)0x34),
            '~'  => (true,  (byte)0x35),
            '<'  => (true,  (byte)0x36),
            '>'  => (true,  (byte)0x37),
            '?'  => (true,  (byte)0x38),
            _    => ((bool, byte)?)null
        };

        if (map is not { } m)
            return false;

        modifiers = m.Item1 ? HidModifiers.Shift : HidModifiers.None;
        key = (HidKey)m.Item2;
        return true;
    }

    /// <summary>整段文字是否都能用硬件键盘打出</summary>
    internal static bool CanType(string text) => text.All(c => TryMapChar(c, out _, out _));

    private static byte[] Frame(byte cmd, byte[] data)
    {
        var frame = new byte[5 + data.Length + 1];
        frame[0] = H0;
        frame[1] = H1;
        frame[2] = ADDR;
        frame[3] = cmd;
        frame[4] = (byte)data.Length;
        Array.Copy(data, 0, frame, 5, data.Length);
        frame[^1] = Checksum(frame, frame.Length - 1);
        return frame;
    }

    private static byte Checksum(byte[] bytes, int count)
    {
        var sum = 0;
        for (var i = 0; i < count; i++)
            sum += bytes[i];
        return (byte)(sum & 0xFF);
    }
}

/// <summary>System.IO.Ports 上的真实串口通道</summary>
public sealed class SerialHidTransport : ICatHidTransport
{
    /// <inheritdoc />
    public IReadOnlyList<string> PortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch
        {
            return [];
        }
    }

    /// <inheritdoc />
    public ICatHidPort? Open(string portName)
    {
        try
        {
            var port = new SerialPort(portName, CatHid9329Protocol.BAUD_RATE, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500
            };
            port.Open();
            return new SerialHidPort(port);
        }
        catch
        {
            return null;
        }
    }

    private sealed class SerialHidPort(SerialPort port) : ICatHidPort
    {
        public void Write(byte[] frame)
        {
            port.DiscardInBuffer();
            port.Write(frame, 0, frame.Length);
        }

        public byte[] Read(int count, TimeSpan timeout)
        {
            var buffer = new List<byte>(count);
            var deadline = DateTime.UtcNow + timeout;
            port.ReadTimeout = Math.Max(1, (int)timeout.TotalMilliseconds);

            while (buffer.Count < count && DateTime.UtcNow < deadline)
            {
                try
                {
                    buffer.Add((byte)port.ReadByte());
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            return [.. buffer];
        }

        public void Dispose()
        {
            try
            {
                port.Close();
            }
            catch
            {
                // 关串口出错不影响流程
            }

            port.Dispose();
        }
    }
}

/// <summary>
///     用 CH9329 做硬件键鼠输入。发现本机的芯片、把文字 / 按键 / 点击翻成串口帧发出去。
///     <para>同一台电脑可能有多个 DcMiniLauncher 进程 (每个游戏一个), 共用一块芯片, 用全局锁把「整段输入」串起来, 串口用完即关。</para>
/// </summary>
public sealed class CatHid9329(ICatHidTransport? transport = null) : ICatHidKeyboard
{
    /// <summary>探测应答的等待时长</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>每个按键按下到抬起、以及键之间的间隔</summary>
    private static readonly TimeSpan KeyInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>整段输入的全局互斥锁 (跨本机所有 DcMiniLauncher 进程)</summary>
    private const string MUTEX_NAME = @"Global\cat-ch9329";

    private readonly ICatHidTransport transport = transport ?? new SerialHidTransport();
    private readonly object sync = new();

    /// <inheritdoc />
    public string? PortName { get; private set; }

    /// <summary>有歧义时优先用这个串口 (对应设置里的 HidSerialPort); 空表示自动找</summary>
    public string? PreferredPort { get; init; }

    /// <inheritdoc />
    public bool TryOpen()
    {
        lock (sync)
        {
            var candidates = transport.PortNames();
            if (!string.IsNullOrWhiteSpace(PreferredPort))
                candidates = new[] { PreferredPort! }.Concat(candidates.Where(p => p != PreferredPort)).ToList();

            foreach (var name in candidates)
            {
                if (Probe(name))
                {
                    PortName = name;
                    return true;
                }
            }

            PortName = null;
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> TypeTextAsync(string text, CancellationToken ct)
    {
        if (!CatHid9329Protocol.CanType(text))
        {
            Log.Warning("[CatHid9329] 文字里有硬件键盘打不出的字符, 退回人工输入");
            return Task.FromResult(false);
        }

        return RunExclusiveAsync(port =>
        {
            foreach (var c in text)
            {
                CatHid9329Protocol.TryMapChar(c, out var mods, out var key);
                Tap(port, mods, key, ct);
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task PressAsync(HidModifiers modifiers, HidKey key, CancellationToken ct) =>
        RunExclusiveAsync(port => Tap(port, modifiers, key, ct), ct);

    /// <inheritdoc />
    public Task ClickAsync(int screenX, int screenY, int screenWidth, int screenHeight, CancellationToken ct) =>
        RunExclusiveAsync(port =>
        {
            var absX = CatHid9329Protocol.ToAbs(screenX, screenWidth);
            var absY = CatHid9329Protocol.ToAbs(screenY, screenHeight);
            port.Write(CatHid9329Protocol.MouseAbsFrame(0x00, absX, absY)); // 先移动到位
            SleepOrThrow(ct);
            port.Write(CatHid9329Protocol.MouseAbsFrame(0x01, absX, absY)); // 左键按下
            SleepOrThrow(ct);
            port.Write(CatHid9329Protocol.MouseAbsFrame(0x00, absX, absY)); // 左键抬起
        }, ct);

    private void Tap(ICatHidPort port, HidModifiers modifiers, HidKey key, CancellationToken ct)
    {
        port.Write(CatHid9329Protocol.KeyboardFrame(modifiers, key));
        SleepOrThrow(ct);
        port.Write(CatHid9329Protocol.KeyboardReleaseFrame());
        SleepOrThrow(ct);
    }

    /// <summary>键 / 帧之间的可取消等待</summary>
    private static void SleepOrThrow(CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(KeyInterval))
            ct.ThrowIfCancellationRequested();
    }

    private bool Probe(string name)
    {
        using var port = transport.Open(name);
        if (port is null)
            return false;

        try
        {
            port.Write(CatHid9329Protocol.InfoFrame());
            var reply = port.Read(14, ProbeTimeout);
            return CatHid9329Protocol.IsInfoReply(reply);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     整段输入放到单独线程上同步执行: 命名 Mutex 有线程亲和性, 必须在同一线程 acquire / release,
    ///     所以不能跨 await 持锁。本机所有 DcMiniLauncher 进程共用这把锁, 串口用完即关。
    /// </summary>
    private Task<bool> RunExclusiveAsync(Action<ICatHidPort> work, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (PortName is null && !TryOpen())
                throw new InvalidOperationException("没有可用的硬件键鼠");

            using var mutex = new Mutex(false, MUTEX_NAME);
            var owned = false;
            try
            {
                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromSeconds(30));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (!owned)
                    throw new TimeoutException("等别的上号释放硬件键鼠超时");

                using var port = transport.Open(PortName!);
                if (port is null)
                {
                    PortName = null;
                    throw new InvalidOperationException("硬件键鼠打不开了");
                }

                work(port);
                return true;
            }
            finally
            {
                if (owned)
                    mutex.ReleaseMutex();
            }
        }, ct);
}
