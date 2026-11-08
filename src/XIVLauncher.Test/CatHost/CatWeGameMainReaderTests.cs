using System;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     主界面「启动」与协议勾选框的像素识别: 用合成的图（有起伏的背景、纯色暗底的协议框、勾选框、橙色或暗色的按钮）
/// </summary>
public sealed class CatWeGameMainReaderTests
{
    [Fact]
    public void PlainPage_StartReady_NoAgreement()
    {
        var page = Page(1600, 900, startReady: true);

        var main = CatWeGameMainReader.Read(page, 1600, 900);

        Assert.True(main.StartReady);
        Assert.False(main.AgreementOpen);
        Assert.False(main.AgreeAllChecked);
    }

    [Fact]
    public void Starting_ButtonIsDark_NotReady()
    {
        var main = CatWeGameMainReader.Read(Page(1600, 900, startReady: false), 1600, 900);

        Assert.False(main.StartReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgreementOpen_ReadsTheAgreeAllTick(bool ticked)
    {
        var page = Page(1600, 900, startReady: true, agreement: true, ticked: ticked);

        var main = CatWeGameMainReader.Read(page, 1600, 900);

        Assert.True(main.AgreementOpen);
        Assert.Equal(ticked, main.AgreeAllChecked);
    }

    [Fact]
    public void SmallerWindow_IsReadAtScale()
    {
        var page = Page(800, 450, startReady: true, agreement: true, ticked: true);

        var main = CatWeGameMainReader.Read(page, 800, 450);

        Assert.True(main.StartReady);
        Assert.True(main.AgreementOpen);
        Assert.True(main.AgreeAllChecked);
        Assert.Equal((691, 424), CatWeGameMainReader.Scale(CatWeGameMainReader.StartButton, 800, 450));
    }

    [Fact]
    public void BadInput_ReadsNothing()
    {
        var main = CatWeGameMainReader.Read([], 1600, 900);

        Assert.False(main.StartReady);
        Assert.False(main.AgreementOpen);
    }

    /// <summary>
    ///     合成一张主界面: 背景是有起伏的暗色画面; 「启动」按钮在 (1250–1515, 822–874); 协议框在 (1249–1515, 600–805), 底色亮度 20
    /// </summary>
    private static byte[] Page(int width, int height, bool startReady, bool agreement = false, bool ticked = false)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(7);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var v = (byte)random.Next(10, 90);
            pixels[i]     = v;
            pixels[i + 1] = v;
            pixels[i + 2] = v;
        }

        if (agreement)
        {
            Fill(pixels, width, height, 1249, 600, 1515, 805, 20, 20, 20);
            // 「以上所有内容」勾选框: 没勾是暗框, 勾上框里有亮的勾
            Fill(pixels, width, height, 1268, 768, 1286, 786, 8, 8, 8);

            if (ticked)
                Fill(pixels, width, height, 1271, 771, 1283, 783, 230, 230, 230);
        }

        if (startReady)
            Fill(pixels, width, height, 1250, 822, 1515, 874, 60, 169, 255);
        else
            Fill(pixels, width, height, 1250, 822, 1515, 874, 30, 58, 82);

        return pixels;
    }

    /// <summary>按 1600×900 量的矩形填色（BGR）</summary>
    private static void Fill(byte[] pixels, int width, int height, int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        for (var y = y0 * height / 900; y <= Math.Min(height - 1, y1 * height / 900); y++)
        {
            for (var x = x0 * width / 1600; x <= Math.Min(width - 1, x1 * width / 1600); x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset]     = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
            }
        }
    }
}
