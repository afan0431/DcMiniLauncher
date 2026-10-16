using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     CH9329 硬件键鼠: 帧编码 / 键码映射 / 坐标换算 / 设备发现。全用假串口, 不接硬件。
/// </summary>
public sealed class CatHid9329Tests
{
    /// <summary>工作台外壳靠程序集里有没有这个类型名判断能力, 名字和命名空间不能改</summary>
    [Fact]
    public void CapabilityTypeNameIsStable()
    {
        Assert.Equal("XIVLauncher.CatHost.CatWeGameHwLogin", typeof(CatWeGameHwLogin).FullName);
        Assert.Equal("weGameHwLogin", CatWeGameHwLogin.CAPABILITY);
        Assert.Same(typeof(CatWeGameLoginCapture).Assembly, typeof(CatWeGameHwLogin).Assembly);
    }

    [Fact]
    public void InfoFrame_HasCorrectHeaderAndChecksum()
    {
        var frame = CatHid9329Protocol.InfoFrame();
        Assert.Equal(new byte[] { 0x57, 0xAB, 0x00, 0x01, 0x00, 0x03 }, frame);
    }

    [Fact]
    public void KeyboardFrame_EncodesModifierAndKeys()
    {
        // Shift + a(0x04): 修饰键 0x02, data[2] = 0x04
        var frame = CatHid9329Protocol.KeyboardFrame(HidModifiers.Shift, (HidKey)0x04);
        Assert.Equal(new byte[] { 0x57, 0xAB, 0x00, 0x02, 0x08, 0x02, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00 }, frame[..13]);
        // SUM = 低 8 位
        var sum = frame.Take(frame.Length - 1).Aggregate(0, (a, b) => a + b) & 0xFF;
        Assert.Equal((byte)sum, frame[^1]);
    }

    [Fact]
    public void KeyboardReleaseFrame_IsAllZeroData()
    {
        var frame = CatHid9329Protocol.KeyboardReleaseFrame();
        Assert.Equal(0x02, frame[3]);
        Assert.Equal(8, frame[4]);
        Assert.All(frame[5..13], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData('a', (byte)HidModifiers.None, (byte)0x04)]
    [InlineData('z', (byte)HidModifiers.None, (byte)0x1D)]
    [InlineData('A', (byte)HidModifiers.Shift, (byte)0x04)]
    [InlineData('1', (byte)HidModifiers.None, (byte)0x1E)]
    [InlineData('0', (byte)HidModifiers.None, (byte)0x27)]
    [InlineData('!', (byte)HidModifiers.Shift, (byte)0x1E)]
    [InlineData('_', (byte)HidModifiers.Shift, (byte)0x2D)]
    [InlineData('.', (byte)HidModifiers.None, (byte)0x37)]
    [InlineData(' ', (byte)HidModifiers.None, (byte)0x2C)]
    public void TryMapChar_MapsAsciiPrintable(char c, byte mods, byte key)
    {
        Assert.True(CatHid9329Protocol.TryMapChar(c, out var m, out var k));
        Assert.Equal((HidModifiers)mods, m);
        Assert.Equal((HidKey)key, k);
    }

    [Theory]
    [InlineData('中')]
    [InlineData('é')]
    [InlineData('\t')]
    [InlineData('\n')]
    public void TryMapChar_RejectsNonUsAsciiPrintable(char c)
    {
        Assert.False(CatHid9329Protocol.TryMapChar(c, out _, out _));
    }

    [Theory]
    [InlineData("Flowers.123", true)]
    [InlineData("aA1!_ ", true)]
    [InlineData("密码123", false)]
    public void CanType_ReflectsMappability(string text, bool expected)
    {
        Assert.Equal(expected, CatHid9329Protocol.CanType(text));
    }

    [Theory]
    [InlineData(0, 2560, 0)]
    [InlineData(2560, 2560, 4095)]
    [InlineData(1280, 2560, 2048)]
    [InlineData(100, 0, 0)]
    public void ToAbs_ScalesPixelToFullScale(int pixel, int span, int expected)
    {
        Assert.Equal(expected, CatHid9329Protocol.ToAbs(pixel, span));
    }

    [Fact]
    public void IsInfoReply_AcceptsValidReply_RejectsGarbage()
    {
        // 真实抓到的应答: 57 AB 00 81 08 ...(8 字节)... SUM
        var body = new byte[] { 0x57, 0xAB, 0x00, 0x81, 0x08, 0x38, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var sum = (byte)(body.Aggregate(0, (a, b) => a + b) & 0xFF);
        var reply = body.Append(sum).ToArray();
        Assert.True(CatHid9329Protocol.IsInfoReply(reply));

        Assert.False(CatHid9329Protocol.IsInfoReply(new byte[] { 0x00, 0x01, 0x02 }));
        var badSum = (byte[])reply.Clone();
        badSum[^1] ^= 0xFF;
        Assert.False(CatHid9329Protocol.IsInfoReply(badSum));
    }

    [Fact]
    public void TryOpen_FindsPortThatAnswersInfo()
    {
        var transport = new FakeTransport();
        transport.Ports["COM1"] = FakePort.Silent();
        transport.Ports["COM3"] = FakePort.AnswersInfo();
        var hid = new CatHid9329(transport);

        Assert.True(hid.TryOpen());
        Assert.Equal("COM3", hid.PortName);
    }

    [Fact]
    public void TryOpen_ReturnsFalse_WhenNoDevice()
    {
        var transport = new FakeTransport();
        transport.Ports["COM1"] = FakePort.Silent();
        var hid = new CatHid9329(transport);

        Assert.False(hid.TryOpen());
        Assert.Null(hid.PortName);
    }

    [Fact]
    public async Task TypeTextAsync_ReturnsFalse_OnUnmappableChar_WithoutWriting()
    {
        var transport = new FakeTransport();
        var port = FakePort.AnswersInfo();
        transport.Ports["COM3"] = port;
        var hid = new CatHid9329(transport);
        Assert.True(hid.TryOpen());

        Assert.False(await hid.TypeTextAsync("密码", CancellationToken.None));
        // 不可输入时不发任何键盘报文 (只有探测时的 info 帧 0x01)
        Assert.DoesNotContain(port.Written, f => f.Length >= 5 && f[3] == 0x02);
    }

    [Fact]
    public async Task TypeTextAsync_WritesDownAndUpPerChar()
    {
        var transport = new FakeTransport();
        transport.Ports["COM3"] = FakePort.AnswersInfo();
        var hid = new CatHid9329(transport);
        Assert.True(hid.TryOpen());

        Assert.True(await hid.TypeTextAsync("ab", CancellationToken.None));
        // 每个字符一次按下 + 一次抬起, 打开的那个会话口收到 4 帧键盘报文
        var session = transport.LastOpened!;
        var keyboardFrames = session.Written.Count(f => f.Length >= 5 && f[3] == 0x02);
        Assert.Equal(4, keyboardFrames);
    }

    private sealed class FakeTransport : ICatHidTransport
    {
        public Dictionary<string, FakePort> Ports { get; } = new();
        public FakePort? LastOpened { get; private set; }

        public IReadOnlyList<string> PortNames() => Ports.Keys.ToList();

        public ICatHidPort? Open(string portName)
        {
            if (!Ports.TryGetValue(portName, out var template))
                return null;
            LastOpened = template;
            return template;
        }
    }

    private sealed class FakePort : ICatHidPort
    {
        private readonly bool answersInfo;
        public List<byte[]> Written { get; } = new();

        private FakePort(bool answersInfo) => this.answersInfo = answersInfo;

        public static FakePort Silent() => new(false);
        public static FakePort AnswersInfo() => new(true);

        public void Write(byte[] frame) => Written.Add(frame);

        public byte[] Read(int count, System.TimeSpan timeout)
        {
            if (!answersInfo)
                return [];
            var body = new byte[] { 0x57, 0xAB, 0x00, 0x81, 0x08, 0x38, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var sum = (byte)(body.Aggregate(0, (a, b) => a + b) & 0xFF);
            return body.Append(sum).ToArray();
        }

        public void Dispose() { }
    }
}
