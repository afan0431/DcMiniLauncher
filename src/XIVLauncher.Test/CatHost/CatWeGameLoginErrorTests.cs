using System;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     登录报错页（账号或密码错误）的像素识别: 合成的报错页、快捷安全登录页、账号密码页
/// </summary>
public sealed class CatWeGameLoginErrorTests
{
    [Fact]
    public void ErrorPage_IsRecognized()
    {
        var page = Window(1210, 680);
        Fill(page, 1210, 680, 49, 356, 251, 392, 5, 118, 255);   // 「重新登录」橙色
        Fill(page, 1210, 680, 49, 400, 251, 436, 51, 63, 95);    // 「退出」灰褐色

        Assert.True(CatWeGameRealScreen.LoginErrorShown(page, 1210, 680));
    }

    [Fact]
    public void ErrorPage_SmallerWindow_IsRecognized()
    {
        var page = Window(864, 486);
        Fill(page, 864, 486, 49, 356, 251, 392, 5, 118, 255);
        Fill(page, 864, 486, 49, 400, 251, 436, 51, 63, 95);

        Assert.True(CatWeGameRealScreen.LoginErrorShown(page, 864, 486));
    }

    [Fact]
    public void QuickLoginPage_IsNotAnError()
    {
        // 快捷安全登录页: 同一位置也是橙色按钮, 但页签是亮的、下面没有灰按钮
        var page = Window(1210, 680);
        Fill(page, 1210, 680, 99, 264, 201, 290, 116, 116, 116);
        Fill(page, 1210, 680, 49, 368, 251, 404, 6, 98, 210);

        Assert.False(CatWeGameRealScreen.LoginErrorShown(page, 1210, 680));
    }

    [Fact]
    public void PasswordPage_IsNotAnError()
    {
        var page = Window(1210, 680);
        Fill(page, 1210, 680, 99, 264, 201, 290, 116, 116, 116);
        Fill(page, 1210, 680, 49, 360, 251, 392, 9, 8, 7);       // 暗色的密码框
        Fill(page, 1210, 680, 49, 448, 251, 484, 5, 118, 255);   // 「登录」

        Assert.False(CatWeGameRealScreen.LoginErrorShown(page, 1210, 680));
    }

    [Fact]
    public void BadInput_IsNotAnError()
    {
        Assert.False(CatWeGameRealScreen.LoginErrorShown([], 1210, 680));
    }

    /// <summary>暗色底的登录窗（亮度约 12）</summary>
    private static byte[] Window(int width, int height)
    {
        var pixels = new byte[width * height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i]     = 12;
            pixels[i + 1] = 11;
            pixels[i + 2] = 10;
        }

        return pixels;
    }

    /// <summary>按 1210×680 量的矩形填色（BGR）</summary>
    private static void Fill(byte[] pixels, int width, int height, int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        for (var y = y0 * height / 680; y <= Math.Min(height - 1, y1 * height / 680); y++)
        {
            for (var x = x0 * width / 1210; x <= Math.Min(width - 1, x1 * width / 1210); x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset]     = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
            }
        }
    }
}
