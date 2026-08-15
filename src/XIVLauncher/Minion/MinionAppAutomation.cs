using System.Diagnostics;
using System.Windows.Automation;
using Serilog;

namespace XIVLauncher.Minion;

/// <summary>
///     一次「让 MINIONAPP 来挂 bot」的结果
/// </summary>
internal sealed record MinionAppInjectResult(bool Ok, string? Error)
{
    public static MinionAppInjectResult Succeeded() => new(true, null);

    public static MinionAppInjectResult Failed(string error) => new(false, error);
}

/// <summary>
///     MINIONAPP 在运行时，由它来发起注入，而不是我们自己拉 MinionLauncher。
///     <para>
///         为什么必须这样（2026-08-14/15 实测定案）：
///         bot 注入后会向 MINIONAPP 汇报自己，而 MINIONAPP 只认**自己发起**的那次挂载
///         （它记的是自己拉起的 MinionLauncher 返回的 PID）。我们自己拉起时它没有对应记录，
///         会话就永远停在「注入中」，十几秒后客户端被杀（游戏退出码 0xFFFFFFFF）。
///         对照：<b>同一个游戏进程</b>由启动器起、改由 MINIONAPP 点「注入」，一切正常。
///     </para>
///     <para>
///         两边传给 MinionLauncher 的命令行逐字相同、环境块也相同（实测抓取对比过），
///         差别只在「这次挂载是谁发起的」，所以只能把发起权交还给它。
///     </para>
/// </summary>
internal static class MinionAppAutomation
{
    public const string PROCESS_NAME = "MINIONAPP";

    /// <summary>账号表格的 AutomationId</summary>
    private const string ACCOUNT_GRID_AUTOMATION_ID = "lstAccountList";

    /// <summary>beta 账号那一行的按钮文案带这个后缀</summary>
    private const string BETA_MARKER = "BETA";

    private const string INJECT_BUTTON_PREFIX = "注入";

    private const string STOP_BUTTON_PREFIX = "停止";

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(PROCESS_NAME);

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    ///     点 MINIONAPP 里对应账号那一行的「注入」，然后等它把 PID 列刷成我们的游戏进程。
    /// </summary>
    /// <param name="group">分组号，与 MINIONAPP 表格里的分组标题一致</param>
    /// <param name="rowIndex">该账号在本分组内的序号（与 Accounts.json 内顺序一致）</param>
    /// <param name="useBeta">该账号是不是 beta —— 同一分组里 beta 与非 beta 常常同 Keycode，靠按钮上的 (BETA) 区分</param>
    /// <param name="keycode">用于二次核对行是否找对</param>
    /// <param name="gamePid">本次要挂的游戏进程</param>
    public static MinionAppInjectResult Inject
    (
        string?           group,
        int               rowIndex,
        bool              useBeta,
        string?           keycode,
        int               gamePid,
        TimeSpan          timeout,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(group))
            return MinionAppInjectResult.Failed("没有选择 Minion 分组");

        var window = FindMainWindow();
        if (window == null)
            return MinionAppInjectResult.Failed("找不到 MINIONAPP 的窗口（它可能最小化到托盘了，先把主界面打开）");

        var grid = window.FindFirst
        (
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, ACCOUNT_GRID_AUTOMATION_ID)
        );

        if (grid == null)
            return MinionAppInjectResult.Failed($"在 MINIONAPP 界面里找不到账号表格（{ACCOUNT_GRID_AUTOMATION_ID}），它的界面可能改版了");

        var groupElement = FindGroup(grid, group);
        if (groupElement == null)
            return MinionAppInjectResult.Failed($"MINIONAPP 里没有分组 {group}");

        var rows = GetChildren(groupElement)
                   .Where(element => element.Current.ControlType == ControlType.DataItem)
                   .ToList();

        if (rowIndex < 0 || rowIndex >= rows.Count)
            return MinionAppInjectResult.Failed($"分组 {group} 在 MINIONAPP 里只有 {rows.Count} 行，取不到第 {rowIndex + 1} 行");

        var row     = rows[rowIndex];
        var button  = FindActionButton(row);

        if (button == null)
            return MinionAppInjectResult.Failed($"分组 {group} 第 {rowIndex + 1} 行上找不到「注入」按钮");

        var buttonName = button.Current.Name ?? string.Empty;

        // 行认错了比挂不上更糟（会挂到别的账号），所以核对 beta 标记与 Keycode 再动手
        if (buttonName.Contains(BETA_MARKER, StringComparison.OrdinalIgnoreCase) != useBeta)
            return MinionAppInjectResult.Failed($"MINIONAPP 里第 {rowIndex + 1} 行的按钮是「{buttonName}」，与所选账号的 beta 设置不符，已放弃自动注入");

        if (!string.IsNullOrWhiteSpace(keycode) && !RowContainsText(row, keycode))
            return MinionAppInjectResult.Failed($"MINIONAPP 里第 {rowIndex + 1} 行的 Keycode 与所选账号不符，已放弃自动注入");

        if (buttonName.StartsWith(STOP_BUTTON_PREFIX, StringComparison.Ordinal))
            return MinionAppInjectResult.Failed($"该账号在 MINIONAPP 里已经是运行状态（按钮为「{buttonName}」），请先在 MINIONAPP 里停止它");

        if (!buttonName.StartsWith(INJECT_BUTTON_PREFIX, StringComparison.Ordinal))
            return MinionAppInjectResult.Failed($"MINIONAPP 里第 {rowIndex + 1} 行的按钮是「{buttonName}」，不是「注入」，已放弃自动注入");

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || pattern is not InvokePattern invoke)
            return MinionAppInjectResult.Failed("「注入」按钮不支持自动点击");

        Log.Information("[Minion] 让 MINIONAPP 注入: 分组={Group}, 行={Row}, 按钮=「{Button}」, 目标 PID={GamePid}", group, rowIndex + 1, buttonName, gamePid);
        invoke.Invoke();

        return WaitForRowPid(row, gamePid, timeout, cancellationToken);
    }

    /// <summary>
    ///     等这一行的 PID 列变成我们的游戏进程 —— 只认它自己刷出来的 PID，不认「点过按钮了」。
    /// </summary>
    private static MinionAppInjectResult WaitForRowPid(AutomationElement row, int gamePid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var expected  = gamePid.ToString();

        while (stopwatch.Elapsed < timeout)
        {
            if (cancellationToken.IsCancellationRequested)
                return MinionAppInjectResult.Failed("已取消");

            string? observedPid = null;

            try
            {
                if (RowContainsText(row, expected))
                {
                    Log.Information("[Minion] MINIONAPP 已把 bot 挂到 PID={GamePid}，用时 {Elapsed:0.0}s", gamePid, stopwatch.Elapsed.TotalSeconds);
                    return MinionAppInjectResult.Succeeded();
                }

                observedPid = ReadLastNumericCell(row);
            }
            catch (ElementNotAvailableException)
            {
                return MinionAppInjectResult.Failed("MINIONAPP 界面在注入过程中发生变化，无法确认结果");
            }

            // 它挂到别的客户端上了 —— 多开时可能发生，直说，别假装成功
            if (!string.IsNullOrEmpty(observedPid) && observedPid != "0" && observedPid != expected)
                return MinionAppInjectResult.Failed($"MINIONAPP 把 bot 挂到了 PID={observedPid}，不是本次启动的 {gamePid}");

            Thread.Sleep(500);
        }

        return MinionAppInjectResult.Failed($"等了 {timeout.TotalSeconds:0} 秒，MINIONAPP 那一行的 PID 仍未变成 {gamePid}");
    }

    private static AutomationElement? FindMainWindow()
    {
        var processes = Process.GetProcessesByName(PROCESS_NAME);

        try
        {
            foreach (var process in processes)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                    return AutomationElement.FromHandle(process.MainWindowHandle);
            }

            // 最小化到托盘时 MainWindowHandle 会是 0，从桌面根节点按进程号找
            foreach (var process in processes)
            {
                var window = AutomationElement.RootElement.FindFirst
                (
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)
                );

                if (window != null)
                    return window;
            }

            return null;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static AutomationElement? FindGroup(AutomationElement root, string name)
    {
        var groups = root.FindAll
        (
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)
        );

        foreach (AutomationElement group in groups)
        {
            if (string.Equals(group.Current.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return group;
        }

        return null;
    }

    private static AutomationElement? FindActionButton(AutomationElement row)
    {
        var buttons = row.FindAll
        (
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)
        );

        foreach (AutomationElement button in buttons)
        {
            var name = button.Current.Name ?? string.Empty;

            if (name.StartsWith(INJECT_BUTTON_PREFIX, StringComparison.Ordinal) || name.StartsWith(STOP_BUTTON_PREFIX, StringComparison.Ordinal))
                return button;
        }

        return null;
    }

    private static bool RowContainsText(AutomationElement row, string text) =>
        GetRowTexts(row).Any(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     PID 列是行里最后一个纯数字单元格
    /// </summary>
    private static string? ReadLastNumericCell(AutomationElement row) =>
        GetRowTexts(row).LastOrDefault(value => value.Length > 0 && value.All(char.IsDigit));

    private static List<string> GetRowTexts(AutomationElement row)
    {
        var texts = new List<string>();

        var elements = row.FindAll
        (
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)
        );

        foreach (AutomationElement element in elements)
        {
            var name = element.Current.Name?.Trim();

            if (!string.IsNullOrEmpty(name))
                texts.Add(name);
        }

        return texts;
    }

    private static List<AutomationElement> GetChildren(AutomationElement element)
    {
        var children = new List<AutomationElement>();
        var walker   = TreeWalker.ControlViewWalker;

        for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child))
            children.Add(child);

        return children;
    }
}
