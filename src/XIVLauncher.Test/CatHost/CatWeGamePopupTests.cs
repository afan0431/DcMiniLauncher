using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XIVLauncher.CatHost;
using Xunit;

namespace XIVLauncher.Test.CatHost;

/// <summary>
///     用实机截的 WeGame 登录窗（PrintWindow, 960×540 与 1210×680）认图片验证码与手机 QQ 一键认证。
///     缺的画面用实机截图拼: 就绪的验证码 = 答错提示那张的网格 + 选中那张的题目条（同一题）; 已过期的手机确认 = 用户截的已过期卡片贴到等确认那张的卡片上
/// </summary>
public sealed class CatWeGamePopupTests
{
    private const string CAPTCHA_960_RETRY      = "captcha-960-retry.png";
    private const string CAPTCHA_960_SELECTED   = "captcha-960-selected.png";
    private const string CAPTCHA_960_SUCCESS    = "captcha-960-success.png";
    private const string CAPTCHA_1210_LOADING   = "captcha-1210-loading.png";
    private const string CAPTCHA_1210_SUBMIT    = "captcha-1210-submitting.png";
    private const string CAPTCHA_CHANGED_CARD   = "captcha-changed-card.png";
    private const string CONFIRM_960            = "confirm-960.png";
    private const string CONFIRM_1210           = "confirm-1210.png";
    private const string CONFIRM_EXPIRED_CARD   = "confirm-expired-card.png";

    /// <summary>960×540 下验证码卡片的左上角</summary>
    private static readonly (int X, int Y) Captcha960 = (284, 52);

    /// <summary>1210×680 下验证码卡片的左上角</summary>
    private static readonly (int X, int Y) Captcha1210 = (409, 122);

    #region 找卡片

    [Theory]
    [InlineData(CAPTCHA_960_RETRY, 284, 52, true)]
    [InlineData(CAPTCHA_960_SELECTED, 284, 52, true)]
    [InlineData(CAPTCHA_960_SUCCESS, 284, 52, true)]
    [InlineData(CAPTCHA_1210_LOADING, 409, 122, true)]
    [InlineData(CAPTCHA_1210_SUBMIT, 409, 122, true)]
    [InlineData(CONFIRM_960, 300, 29, false)]
    [InlineData(CONFIRM_1210, 425, 99, false)]
    public void FindCard_FindsTheCardInTheMiddle_WhateverTheWindowSize(string file, int left, int top, bool captcha)
    {
        var shot = Shot.Load(file);

        var card = CatWeGamePopupReader.FindCard(shot.Pixels, shot.Width, shot.Height);

        Assert.NotNull(card);
        Assert.Equal((left, top), (card.Value.Left, card.Value.Top));
        Assert.Equal(captcha ? (392, 436) : (360, 481), (card.Value.Width, card.Value.Height));
        Assert.Equal(captcha, card.Value.Captcha);
        Assert.Equal(1.0, card.Value.Scale, 3);
    }

    [Fact]
    public void Read_NoPopup_ReturnsNull()
    {
        // 登录窗本来的样子: 暗色背景, 左侧登录栏; 正中没有白卡片
        var shot = Shot.Background(1210, 680);
        shot.Fill(135, 0, 165, 230, 255, 255, 255);   // 1210×680 版式左上角那块白色残影

        Assert.Null(CatWeGamePopupReader.Read(shot.Pixels, shot.Width, shot.Height));
        Assert.Null(CatWeGamePopupReader.Read([], 0, 0));
    }

    [Fact]
    public void Read_WhiteCardOfAnotherShape_IsNotAPopup()
    {
        var shot = Shot.Background(960, 540);
        shot.Fill(330, 120, 300, 300, 255, 255, 255);

        Assert.Null(CatWeGamePopupReader.Read(shot.Pixels, shot.Width, shot.Height));
    }

    #endregion

    #region 验证码各种状态

    [Fact]
    public void Captcha_Loading_WhiteCard()
    {
        var popup = Read(Shot.Load(CAPTCHA_1210_LOADING));

        Assert.Equal(CatWeGamePopupKind.CaptchaLoading, popup.Kind);
        Assert.Null(popup.CaptchaJpeg);
    }

    [Theory]
    [InlineData(CAPTCHA_960_RETRY)]    // 题目条红字「请再作答一次」
    [InlineData(CAPTCHA_960_SUCCESS)]  // 绿勾「验证成功!」, 格子变淡
    [InlineData(CAPTCHA_1210_SUBMIT)]  // 提交中: 格子变淡、转圈
    public void Captcha_Transitions_AreBusy(string file)
    {
        var popup = Read(Shot.Load(file));

        Assert.Equal(CatWeGamePopupKind.CaptchaBusy, popup.Kind);
        Assert.Null(popup.CaptchaJpeg);
        Assert.Null(popup.GridThumb);
    }

    [Fact]
    public void Captcha_ChangedQuestionNotice_IsBusy_At1210()
    {
        // 「有点难呢, 已更换题目」的卡片贴到 1210×680 的窗口正中
        var shot = Shot.Load(CAPTCHA_1210_LOADING);
        shot.Paste(Shot.Load(CAPTCHA_CHANGED_CARD), 1, 0, 392, 436, Captcha1210.X, Captcha1210.Y);

        Assert.Equal(CatWeGamePopupKind.CaptchaBusy, Read(shot).Kind);
    }

    [Fact]
    public void Captcha_TileTicked_IsSelected_WithThatTile()
    {
        var popup = Read(Shot.Load(CAPTCHA_960_SELECTED));

        Assert.Equal(CatWeGamePopupKind.CaptchaSelected, popup.Kind);
        Assert.Equal(new[] { 2 }, popup.SelectedTiles);
        Assert.Null(popup.CaptchaJpeg);
        Assert.Equal(6, popup.TilePoints!.Count);
    }

    [Fact]
    public void Captcha_Ready_960_ReportsImageTilesAndPoints()
    {
        var shot  = Ready960();
        var popup = Read(shot);

        Assert.Equal(CatWeGamePopupKind.CaptchaReady, popup.Kind);
        Assert.Empty(popup.SelectedTiles!);
        AssertCaptchaGeometry(shot, popup, Captcha960);
        AssertReportedImage(popup);
    }

    [Fact]
    public void Captcha_Ready_1210_ReportsImageTilesAndPoints()
    {
        var shot = Shot.Load(CAPTCHA_1210_LOADING);
        shot.Paste(Ready960(), Captcha960.X, Captcha960.Y, 392, 436, Captcha1210.X, Captcha1210.Y);

        var popup = Read(shot);

        Assert.Equal(CatWeGamePopupKind.CaptchaReady, popup.Kind);
        AssertCaptchaGeometry(shot, popup, Captcha1210);
        AssertReportedImage(popup);
    }

    [Fact]
    public void Captcha_GridThumb_SameQuestionMatches_OtherQuestionDoesNot()
    {
        var first  = Read(Ready960());
        var again  = Read(Ready960());

        // 另一题: 换成「已更换题目」那张卡片的网格（山）, 题目条照旧
        var other = Ready960();
        other.Paste(Shot.Load(CAPTCHA_CHANGED_CARD), 1 + 24, 151, 342, 227, Captcha960.X + 24, Captcha960.Y + 151);
        var changed = Read(other);

        Assert.Equal(CatWeGamePopupKind.CaptchaReady, changed.Kind);
        Assert.True(CatWeGamePopup.SameGrid(first.GridThumb, again.GridThumb));
        Assert.False(CatWeGamePopup.SameGrid(first.GridThumb, changed.GridThumb));
        Assert.False(CatWeGamePopup.SameGrid(first.GridThumb, null));
    }

    [Fact]
    public void TileRatios_AreTheAgreedPerMille()
    {
        Assert.Equal
        (
            new[]
            {
                new[] { 0, 268, 327, 361 }, new[] { 336, 268, 327, 361 }, new[] { 673, 268, 327, 361 },
                new[] { 0, 639, 327, 361 }, new[] { 336, 639, 327, 361 }, new[] { 673, 639, 327, 361 }
            },
            CatWeGamePopupReader.TileRatios
        );
    }

    #endregion

    #region 手机 QQ 确认

    [Theory]
    [InlineData(CONFIRM_960)]
    [InlineData(CONFIRM_1210)]
    public void QqConfirm_Waiting(string file)
    {
        var popup = Read(Shot.Load(file));

        Assert.Equal(CatWeGamePopupKind.QqConfirm, popup.Kind);
        Assert.Null(popup.ReverifyPoint);
    }

    [Theory]
    [InlineData(CONFIRM_960, 300, 29)]
    [InlineData(CONFIRM_1210, 425, 99)]
    public void QqConfirm_Expired_PointsAtReverifyLink(string file, int left, int top)
    {
        var shot = ExpiredOn(file, (left, top));

        var popup = Read(shot);

        Assert.Equal(CatWeGamePopupKind.QqConfirmExpired, popup.Kind);

        // 「重新验证」在卡片内约 (254–301, 322–333)
        var (x, y) = popup.ReverifyPoint!.Value;
        Assert.InRange(x - left, 270, 285);
        Assert.InRange(y - top, 324, 332);
        Assert.True(shot.IsLinkBlue(x, y, 3), $"({x}, {y}) 附近不是「重新验证」的蓝字");
    }

    #endregion

    /// <summary>
    ///     各格中心落在格子里、「确定」落在蓝色按钮上、「换一组」落在左下那段文字上
    /// </summary>
    private static void AssertCaptchaGeometry(Shot shot, CatWeGamePopup popup, (int X, int Y) origin)
    {
        (int X, int Y)[] expected =
        [
            (80, 207), (195, 207), (310, 207),
            (80, 322), (195, 322), (310, 322)
        ];

        Assert.Equal(expected.Select(p => (origin.X + p.X, origin.Y + p.Y)), popup.TilePoints!);
        Assert.Equal((origin.X + 336, origin.Y + 397), popup.ConfirmPoint);
        Assert.Equal((origin.X + 98, origin.Y + 398), popup.RefreshPoint);

        var (cx, cy) = popup.ConfirmPoint!.Value;
        Assert.Equal((0, 122, 255), shot.Rgb(cx, cy));
    }

    /// <summary>
    ///     报给客户的图: 342×310 的 JPEG, 最上面是蓝色题目条; 格子位置是约定的千分比
    /// </summary>
    private static void AssertReportedImage(CatWeGamePopup popup)
    {
        var image = Shot.Decode(popup.CaptchaJpeg!);

        Assert.Equal((342, 310), (image.Width, image.Height));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, popup.CaptchaJpeg![..2]);

        var (r, g, b) = image.Rgb(5, 5);
        Assert.InRange(r, 0, 30);
        Assert.InRange(g, 100, 145);
        Assert.InRange(b, 230, 255);

        Assert.Same(CatWeGamePopupReader.TileRatios, popup.Tiles);
        Assert.NotNull(popup.GridThumb);
    }

    private static CatWeGamePopup Read(Shot shot)
    {
        var popup = CatWeGamePopupReader.Read(shot.Pixels, shot.Width, shot.Height);
        Assert.NotNull(popup);
        return popup;
    }

    /// <summary>
    ///     960×540 下就绪的一题: 答错提示那张的网格没有打勾、也没变淡, 题目条换成同一题正常时的样子（选中那张的题目条）
    /// </summary>
    private static Shot Ready960()
    {
        var shot = Shot.Load(CAPTCHA_960_RETRY);
        shot.Paste(Shot.Load(CAPTCHA_960_SELECTED), Captcha960.X + 24, Captcha960.Y + 68, 342, 79, Captcha960.X + 24, Captcha960.Y + 68);
        return shot;
    }

    /// <summary>
    ///     用户截的已过期卡片（卡片从 (5, 6) 起, 少最后一行）贴到等确认那张的卡片位置上
    /// </summary>
    private static Shot ExpiredOn(string file, (int X, int Y) origin)
    {
        var shot = Shot.Load(file);
        var card = Shot.Load(CONFIRM_EXPIRED_CARD);
        shot.Fill(origin.X, origin.Y, 360, 481, 255, 255, 255);
        shot.Paste(card, 5, 6, 360, card.Height - 6, origin.X, origin.Y);
        return shot;
    }

    /// <summary>
    ///     一张 BGRA 截图
    /// </summary>
    private sealed class Shot(byte[] pixels, int width, int height)
    {
        public byte[] Pixels { get; } = pixels;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public static Shot Load(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "CatHost", "WeGameShots", name);
            using var stream = File.OpenRead(path);
            return From(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
        }

        public static Shot Decode(byte[] image)
        {
            using var stream = new MemoryStream(image);
            return From(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
        }

        /// <summary>暗色背景（登录窗没有弹窗时正中一带的样子）</summary>
        public static Shot Background(int width, int height)
        {
            var shot = new Shot(new byte[width * height * 4], width, height);
            shot.Fill(0, 0, width, height, 18, 18, 22);
            return shot;
        }

        private static Shot From(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels    = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);

            // 截窗得到的像素第 4 个字节常常是 0: 照样子清掉, 识别不能依赖它
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 0;

            return new Shot(pixels, converted.PixelWidth, converted.PixelHeight);
        }

        public (int R, int G, int B) Rgb(int x, int y)
        {
            var o = (y * Width + x) * 4;
            return (Pixels[o + 2], Pixels[o + 1], Pixels[o]);
        }

        public bool IsLinkBlue(int x, int y, int radius)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var (r, _, b) = Rgb(x + dx, y + dy);

                    if (b > 180 && b - r > 60)
                        return true;
                }
            }

            return false;
        }

        public void Fill(int left, int top, int width, int height, byte r, byte g, byte b)
        {
            for (var y = top; y < top + height; y++)
            {
                for (var x = left; x < left + width; x++)
                {
                    var o = (y * Width + x) * 4;
                    Pixels[o]     = b;
                    Pixels[o + 1] = g;
                    Pixels[o + 2] = r;
                }
            }
        }

        public void Paste(Shot source, int sourceX, int sourceY, int width, int height, int targetX, int targetY)
        {
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(source.Pixels, ((sourceY + y) * source.Width + sourceX) * 4, Pixels, ((targetY + y) * Width + targetX) * 4, width * 4);
        }
    }
}
