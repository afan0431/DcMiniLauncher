using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using XIVLauncher.CatLobby.Protocol;
using World = Lumina.Excel.Sheets.World;

namespace XIVLauncher.CatLobby.Game;

/// <summary>
///     读写国际服选角相关的游戏状态。所有方法只能在框架线程上调用（由 <see cref="CatLobbyPipe" /> 用 IFramework.RunOnFrameworkThread 包住）。
///     <para>
///         读结构体时做合理性校验, 读歪了回失败而不是错数据。⚠ .NET 抓不到访问违例, 所以指针一律先过 <see cref="Plausible" /> 再解引用。
///     </para>
///     <para>
///         未实机: 本类里所有 addon 名、回调编号、节点 id、结构偏移都只对照过 FFXIVClientStructs 源码与 ECommons / Lifestream 的写法,
///         没有在国际服真实客户端上跑过。对应国服的做法见 XIVLauncher.MiniModule\game.cpp。
///     </para>
/// </summary>
internal sealed unsafe class LobbyGame(IDataManager data, IPluginLog log)
{
    /// <summary>选角界面 addon 的回调编号: _CharaSelectListMenu ← (21, 序号) 只高亮; (29, 0, 序号) 左键点击, 弹登录确认框。未实机</summary>
    private const int CALLBACK_SELECT_CHARA = 21;

    private const int CALLBACK_CLICK_CHARA = 29;

    /// <summary>_CharaSelectWorldServer ← (25, 0, 服务器下标)。未实机</summary>
    private const int CALLBACK_SELECT_WORLD = 25;

    /// <summary>_TitleMenu 上「开始游戏」按钮的节点 id; 和国服小模块一样, 对按钮发它自己登记的点击事件。未实机</summary>
    private const uint TITLE_START_BUTTON_ID = 4;

    /// <summary>SelectYesno: FireCallbackInt(0)=是 (1)=否; SelectOk: FireCallbackInt(0)=确定。未实机</summary>
    private const int CALLBACK_YES = 0;

    private const int CALLBACK_NO = 1;
    private const int CALLBACK_OK = 0;

    /// <summary>Dialogue（标题界面的错误码框）上「确定」按钮的节点 id。未实机</summary>
    private const uint DIALOGUE_OK_BUTTON_ID = 4;

    /// <summary>LobbyData 里「存着的角色列表属于哪个服务器」(ushort), CS 没有这个字段。偏移取自国服逆向，未实机</summary>
    private const int CHARA_SELECT_ENTRIES_WORLD_OFFSET = 0x8F0;

    /// <summary>大区角色条目 +0x74C 的一组标志, CS 里是私有字段。未实机</summary>
    private const int CHARA_ENTRY_CLICK_FLAGS_OFFSET = 0x74C;

    private const int MAX_CHARACTERS = 64;
    private const int MAX_WORLDS     = 64;

    private static bool Plausible(void* pointer)
    {
        var value = (ulong)pointer;
        return value >= 0x10000 && value < 0x00007FFFFFFF0000UL && (value & 7) == 0;
    }

    private static AtkUnitBase* Addon(string name)
    {
        var stage = AtkStage.Instance();

        if (stage == null || stage->RaptureAtkUnitManager == null)
            return null;

        var addon = stage->RaptureAtkUnitManager->GetAddonByName(name);
        return Plausible(addon) ? addon : null;
    }

    private static AgentLobby* Lobby()
    {
        var lobby = AgentLobby.Instance();
        return Plausible(lobby) ? lobby : null;
    }

    // ---- 界面 ------------------------------------------------------------------

    /// <summary>
    ///     当前处在哪。「在游戏里」必须读 AgentLobby 的登录标志, 不能靠 addon 反推: 片头动画里选角与标题 addon 都不在,
    ///     反推会把动画误判成在游戏里
    /// </summary>
    public LobbyWhere ReadWhere()
    {
        var lobby = Lobby();

        if (lobby != null && (lobby->IsLoggedIn || lobby->IsLoggedIntoZone))
            return LobbyWhere.InGame;

        if (AtkStage.Instance() == null)
            return LobbyWhere.Unknown;

        if (Addon("_CharaSelectListMenu") != null)
            return LobbyWhere.CharaSelect;

        return Addon("_TitleMenu") != null ? LobbyWhere.Title : LobbyWhere.Busy;
    }

    public LobbySnapshot? ReadLobbyState()
    {
        var lobby = Lobby();

        if (lobby == null)
            return null;

        var where = ReadWhere();

        if (where == LobbyWhere.Unknown)
            return null;

        var loading = Addon("NowLoading");

        return new LobbySnapshot
        (
            where,
            lobby->WorldId,
            lobby->WorldIndex,
            lobby->DisplayedCharacterIndex,
            lobby->HoveredCharacterContentId,
            lobby->TemporaryLocked,
            lobby->LobbyUpdateStage,
            lobby->LobbyUIStage,
            lobby->QueuePosition,
            lobby->DialogAddonId,
            loading != null && loading->IsVisible,
            ListWorld(lobby),
            ReadDialog("SelectYesno", true),
            ReadDialog("SelectOk", true),
            ReadDialog("Dialogue", false)
        );
    }

    private static ushort ListWorld(AgentLobby* lobby) =>
        *(ushort*)((byte*)&lobby->LobbyData + CHARA_SELECT_ENTRIES_WORLD_OFFSET);

    private static DialogSnapshot ReadDialog(string name, bool hasPromptNode)
    {
        var addon = Addon(name);

        if (addon == null)
            return DialogSnapshot.None;

        return new DialogSnapshot(true, addon->Id, addon->IsReady, addon->IsVisible, ReadDialogText(addon, hasPromptNode));
    }

    /// <summary>
    ///     对话框的提示文字。文字只是给编排判断用的附加信息, 读不到或读歪了留空, 不让整条命令失败。
    ///     SelectYesno / SelectOk 先取提示文字节点, 取不到再看 AtkValues 里第一个字符串; Dialogue 先看 AtkValues, 没有再取最长的文字节点
    /// </summary>
    private static string ReadDialogText(AtkUnitBase* addon, bool hasPromptNode)
    {
        try
        {
            var text = string.Empty;

            if (hasPromptNode)
            {
                // SelectYesno 与 SelectOk 的提示文字节点在各自结构的同一位置 (+0x238)
                var node = ((AddonSelectYesno*)addon)->PromptText;

                if (Plausible(node))
                    text = NodeText(node);
            }

            if (text.Length == 0)
                text = FirstStringValue(addon);

            if (text.Length == 0 && !hasPromptNode)
                text = LongestTextNode(addon);

            return LobbyTextRules.Sanitize(text, 600);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NodeText(AtkTextNode* node) =>
        node->NodeText.ExtractText();

    private static string FirstStringValue(AtkUnitBase* addon)
    {
        var count = Math.Min((int)addon->AtkValuesCount, 8);

        if (!Plausible(addon->AtkValues))
            return string.Empty;

        for (var i = 0; i < count; i++)
        {
            var value = addon->AtkValues[i];

            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
                continue;

            if (value.String.Value == null)
                continue;

            var text = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;

            if (text.Length > 0)
                return text;
        }

        return string.Empty;
    }

    private static string LongestTextNode(AtkUnitBase* addon)
    {
        var best  = string.Empty;
        var count = addon->UldManager.NodeListCount;
        var list  = addon->UldManager.NodeList;

        if (!Plausible(list) || count == 0 || count > 256)
            return best;

        for (var i = 0; i < count; i++)
        {
            var node = list[i];

            if (!Plausible(node) || node->Type != NodeType.Text)
                continue;

            var text = NodeText((AtkTextNode*)node);

            if (text.Length > best.Length && text.Length < 600)
                best = text;
        }

        return best;
    }

    // ---- 回调 ------------------------------------------------------------------

    private static bool FireInts(AtkUnitBase* addon, bool close, params int[] numbers)
    {
        if (numbers.Length > 3)
            return false;

        var values = stackalloc AtkValue[3];

        for (var i = 0; i < numbers.Length; i++)
        {
            values[i].Type = AtkValueType.Int;
            values[i].Int  = numbers[i];
        }

        return addon->FireCallback((uint)numbers.Length, values, close);
    }

    // ---- 标题 ------------------------------------------------------------------

    /// <summary>
    ///     标题菜单是否已可以点「开始游戏」: addon 就绪且可见、画面没在淡入淡出、不是在选数据中心 / 正在连接，
    ///     并且菜单自己的淡入已完成（ECommons AddonMaster._TitleMenu.IsReady 的条件）。未实机
    /// </summary>
    public bool TitleReady()
    {
        var title = Addon("_TitleMenu");

        if (title == null || !title->IsReady || !title->IsVisible || !ScreenReady())
            return false;

        if (Addon("TitleDCWorldMap") != null || Addon("TitleConnect") != null)
            return false;

        if (title->UldManager.NodeListCount <= 3 || !Plausible(title->UldManager.NodeList))
            return false;

        var menu = title->UldManager.NodeList[7];
        var fade = title->GetNodeById(3);

        return Plausible(menu) && menu->IsVisible() && Plausible(fade) && fade->Color.A == 0xFF;
    }

    private static bool ScreenReady()
    {
        foreach (var name in new[] { "NowLoading", "FadeMiddle", "FadeBack" })
        {
            var addon = Addon(name);

            if (addon != null && addon->IsVisible)
                return false;
        }

        return true;
    }

    /// <summary>点「开始游戏」。不在标题界面或按钮不可用返回 false</summary>
    public bool TitleLogin()
    {
        var title = Addon("_TitleMenu");

        if (title == null)
            return false;

        var button = title->GetComponentButtonById(TITLE_START_BUTTON_ID);

        if (!Plausible(button) || !button->IsEnabled)
            return false;

        return ClickButton(title, button);
    }

    // ---- 选角列表 ----------------------------------------------------------------

    private string WorldName(ushort id, string fallback)
    {
        if (id != 0)
        {
            try
            {
                var name = data.GetExcelSheet<World>()?.GetRowOrDefault(id)?.Name.ToString();

                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "查 World 表失败 id={Id}", id);
            }
        }

        return fallback;
    }

    /// <summary>把一个大区角色条目读成一行; 读歪了返回 null</summary>
    private CharaRow? ReadRow(LobbyUIClientCharacterEntry* entry)
    {
        if (entry->ContentId == 0)
            return null;

        var name        = entry->NameString;
        var currentName = entry->CurrentWorldNameString;
        var homeName    = entry->HomeWorldNameString;

        if (!LobbyTextRules.IsPlausibleName(name, false) ||
            !LobbyTextRules.IsPlausibleName(currentName, true) ||
            !LobbyTextRules.IsPlausibleName(homeName, true))
            return null;

        return new CharaRow
        (
            entry->ContentId,
            entry->Index,
            (byte)entry->LoginFlags,
            entry->CurrentWorldId,
            entry->HomeWorldId,
            name,
            WorldName(entry->CurrentWorldId, currentName),
            WorldName(entry->HomeWorldId, homeName),
            *(uint*)((byte*)entry + CHARA_ENTRY_CLICK_FLAGS_OFFSET)
        );
    }

    /// <summary>客户端自己不认的条目（空位、已删除、ContentId 与镜像对不上）</summary>
    private static bool DcEntryUsable(LobbyUIClientCharacterEntry* entry) =>
        entry->ContentId != 0 && entry->DeletedFlag == 0 && entry->ContentIdMirror == entry->ContentId;

    /// <summary>读一个 std::vector 的 (首元素, 元素数); 空向量 (null, 0); 读歪返回 count = -1</summary>
    private static (nint First, int Count) VectorOf(byte* first, byte* last, byte* end, int elementSize, int maxCount)
    {
        if (first == null && last == null)
            return (0, 0);

        if (!Plausible(first) || last < first || end < last)
            return (0, -1);

        var bytes = (long)(last - first);

        if (bytes % elementSize != 0 || bytes / elementSize > maxCount)
            return (0, -1);

        return ((nint)first, (int)(bytes / elementSize));
    }

    private static (nint First, int Count) DcCharacters(AgentLobby* lobby)
    {
        ref var vector = ref lobby->LobbyData.LobbyUIClient.CurrentDataCenterCharacters;
        var (first, count) = VectorOf((byte*)vector.First, (byte*)vector.Last, (byte*)vector.End, sizeof(LobbyUIClientCharacterEntry), MAX_CHARACTERS);
        return (first, count);
    }

    private static (nint First, int Count) DcWorlds(AgentLobby* lobby)
    {
        ref var vector = ref lobby->LobbyData.LobbyUIClient.CurrentDataCenterWorlds;
        var (first, count) = VectorOf((byte*)vector.First, (byte*)vector.Last, (byte*)vector.End, sizeof(LobbyDataCenterWorldEntry), MAX_WORLDS);
        return (first, count);
    }

    /// <summary>当前显示的角色列表（元素是指向大区角色列表条目的指针）</summary>
    private static (nint First, int Count) DisplayedList(AgentLobby* lobby)
    {
        ref var vector = ref lobby->LobbyData.CharaSelectEntries;
        var (first, count) = VectorOf((byte*)vector.First, (byte*)vector.Last, (byte*)vector.End, sizeof(void*), MAX_CHARACTERS);
        return (first, count);
    }

    public CharaListSnapshot? ReadCharas(out string? failure)
    {
        failure = null;

        var lobby = Lobby();

        if (lobby == null)
        {
            failure = LobbyReplies.POINTERS_UNAVAILABLE;
            return null;
        }

        var where = ReadWhere();
        var (firstPtr, total) = DcCharacters(lobby);
        var first = (LobbyUIClientCharacterEntry*)firstPtr;

        if (total < 0)
        {
            failure = "FAIL bad-list";
            return null;
        }

        var rows    = new List<CharaRow>(total);
        var skipped = 0;
        var invalid = 0;

        for (var i = 0; i < total; i++)
        {
            var entry = first + i;

            if (!DcEntryUsable(entry))
            {
                skipped++;

                if (entry->ContentId != 0)
                    invalid++;

                continue;
            }

            var row = ReadRow(entry);

            if (row == null)
            {
                failure = $"FAIL bad-entry index={i}";
                return null;
            }

            rows.Add(row);
        }

        return new CharaListSnapshot
        (
            where,
            total,
            skipped,
            invalid,
            lobby->DisplayedCharacterIndex,
            -1,
            lobby->SelectedCharacterContentId,
            lobby->HoveredCharacterContentId,
            rows
        );
    }

    /// <summary>在整个大区的角色列表里找角色</summary>
    private PickOutcome FindDcCharacter(AgentLobby* lobby, string who, ulong contentId)
    {
        var (firstPtr, count) = DcCharacters(lobby);
        var first = (LobbyUIClientCharacterEntry*)firstPtr;

        if (count < 0)
            return PickOutcome.Fail(CharaActionStatus.BadList);

        var rows = new List<(int, CharaRow)>();

        for (var i = 0; i < count; i++)
        {
            var entry = first + i;

            if (!DcEntryUsable(entry))
                continue;

            var row = ReadRow(entry);

            if (row == null)
                return PickOutcome.Fail(CharaActionStatus.BadList);

            rows.Add((i, row));
        }

        return PickOutcome.From(CharaLogic.Pick(rows, who, contentId));
    }

    /// <summary>
    ///     在当前显示的角色列表里找角色, position = 它在列表里的位置（回调里的序号就是这个位置, 不看条目自己的 Index 字段）。
    ///     列表里放的是大区角色列表条目的地址: 指针不落在那个向量的元素边界上, 说明这份列表是旧的或者读歪了, 一律不用
    /// </summary>
    private PickOutcome FindListCharacter(AgentLobby* lobby, string who, ulong contentId)
    {
        var (listPtr, count)     = DisplayedList(lobby);
        var (dcFirstPtr, dcCount) = DcCharacters(lobby);
        var list    = (Pointer<CharaSelectCharacterEntry>*)listPtr;
        var dcFirst = (LobbyUIClientCharacterEntry*)dcFirstPtr;

        if (count < 0 || dcCount < 0)
            return PickOutcome.Fail(CharaActionStatus.BadList);

        var rows    = new List<(int, CharaRow)>();
        var usable  = new Dictionary<int, bool>();

        for (var i = 0; i < count; i++)
        {
            var entry = (LobbyUIClientCharacterEntry*)list[i].Value;

            if (entry == null)
                continue;

            if (!PointsIntoDc(entry, dcFirst, dcCount))
                return PickOutcome.Fail(CharaActionStatus.BadList);

            var row = ReadRow(entry);

            if (row == null)
                return PickOutcome.Fail(CharaActionStatus.BadList);

            rows.Add((i, row));
            usable[i] = DcEntryUsable(entry);
        }

        var pick = CharaLogic.Pick(rows, who, contentId);

        // 客户端自己不认的条目按序号取不出来, 发了回调也选不中
        if (pick.Status == PickStatus.Found && !usable[pick.Position])
            return PickOutcome.Fail(CharaActionStatus.BadList);

        return PickOutcome.From(pick);
    }

    private static bool PointsIntoDc(LobbyUIClientCharacterEntry* entry, LobbyUIClientCharacterEntry* dcFirst, int dcCount)
    {
        if (dcFirst == null || entry < dcFirst)
            return false;

        var offset = (byte*)entry - (byte*)dcFirst;
        return offset < (long)dcCount * sizeof(LobbyUIClientCharacterEntry) && offset % sizeof(LobbyUIClientCharacterEntry) == 0;
    }

    /// <summary>
    ///     客户端处理选角回调时先按 WorldIndex 在大区服务器表里取服务器, 再取那个服务器的角色列表。
    ///     要求三处记的是同一个服务器: 服务器表[WorldIndex]、WorldId、存着的角色列表所属的服务器
    /// </summary>
    private static bool ListBelongsToCurrentWorld(AgentLobby* lobby, out ushort world, out ushort listWorld)
    {
        world     = lobby->WorldId;
        listWorld = ListWorld(lobby);

        if (world == 0 || listWorld != world)
            return false;

        var (worldsPtr, count) = DcWorlds(lobby);
        var worlds = (LobbyDataCenterWorldEntry*)worldsPtr;
        var index           = lobby->WorldIndex;

        return count > 0 && index >= 0 && index < count && worlds[index].Id == world;
    }

    /// <summary>
    ///     登录确认框答「是」时客户端登录的是「当前服务器角色列表[选中序号]」——答的那一刻按 WorldIndex 和序号现取。
    ///     这里按同样的办法取一遍, 返回它的 ContentId; 取不出来返回 0
    /// </summary>
    private static ulong ContentIdAboutToLogin(AgentLobby* lobby)
    {
        if (!ListBelongsToCurrentWorld(lobby, out _, out _))
            return 0;

        var (listPtr, count)      = DisplayedList(lobby);
        var (dcFirstPtr, dcCount) = DcCharacters(lobby);
        var list                  = (Pointer<CharaSelectCharacterEntry>*)listPtr;
        var dcFirst               = (LobbyUIClientCharacterEntry*)dcFirstPtr;
        int index              = lobby->DisplayedCharacterIndex;

        if (count <= 0 || dcCount <= 0 || index < 0 || index >= count)
            return 0;

        var entry = (LobbyUIClientCharacterEntry*)list[index].Value;

        return PointsIntoDc(entry, dcFirst, dcCount) && DcEntryUsable(entry) ? entry->ContentId : 0;
    }

    /// <summary>选角界面上开着是/否框、确定框或错误框: 有人（或客户端自己）正在处理别的事, 切服务器、选角色、点角色都不该插进去</summary>
    private static bool AnyLobbyDialog() =>
        Addon("SelectYesno") != null || Addon("SelectOk") != null || Addon("Dialogue") != null;

    // ---- FOCUSCHARA / SELECTCHARA / ENTERCHARA -----------------------------------

    public FocusResult Focus(string who, ulong contentId)
    {
        var lobby = Lobby();

        if (lobby == null)
            return new FocusResult(FocusStatus.PointersUnavailable);

        if (ReadWhere() != LobbyWhere.CharaSelect)
            return new FocusResult(FocusStatus.NotCharaSelect);

        var found = FindDcCharacter(lobby, who, contentId);

        if (found.Failure != null)
            return new FocusResult(found.Failure == CharaActionStatus.BadList ? FocusStatus.BadList : FocusStatus.Exception);

        if (found.Status == PickStatus.None)
            return new FocusResult(FocusStatus.NotInList);

        if (found.Status == PickStatus.Ambiguous)
            return new FocusResult(FocusStatus.Ambiguous);

        var target = CharaLogic.TargetWorld(found.Row!);
        var before = lobby->WorldId;

        if (before == target)
            return new FocusResult(FocusStatus.Already, target, before);

        var (worldsPtr, count) = DcWorlds(lobby);
        var worlds = (LobbyDataCenterWorldEntry*)worldsPtr;

        if (count < 0)
            return new FocusResult(FocusStatus.BadList, target);

        var slot = -1;

        for (var i = 0; i < count && slot < 0; i++)
        {
            if (worlds[i].Id == target)
                slot = i;
        }

        if (slot < 0)
            return new FocusResult(FocusStatus.WorldNotListed, target);

        var addon = Addon("_CharaSelectWorldServer");

        if (addon == null)
            return new FocusResult(FocusStatus.NoWorldList, target);

        if (AnyLobbyDialog())
            return new FocusResult(FocusStatus.DialogOpen, target);

        FireInts(addon, true, CALLBACK_SELECT_WORLD, 0, slot);

        var after = lobby->WorldId;
        return new FocusResult(after == target ? FocusStatus.Switched : FocusStatus.NotApplied, target, after, slot);
    }

    /// <summary>
    ///     enter=false: 只高亮(21, 序号)并回读确认; enter=true: 先高亮并回读确认, 再左键点击(29, 0, 序号)并再回读一次, 之后客户端弹登录确认框。
    ///     全部检查、两条回调、两次回读都在这同一次框架线程执行里, 中间没有别的帧, 列表不会在检查和点击之间变掉。
    ///     回读: 客户端取到 列表[序号] 后把它的 ContentId 写进 HoveredCharacterContentId, 读回来等于要找的角色, 就证明客户端选中的确实是它;
    ///     不等就绝不往下点
    /// </summary>
    public CharaActionResult CharaAction(string who, ulong contentId, bool enter)
    {
        var lobby = Lobby();

        if (lobby == null)
            return new CharaActionResult(CharaActionStatus.PointersUnavailable, enter);

        var addon = ReadWhere() == LobbyWhere.CharaSelect ? Addon("_CharaSelectListMenu") : null;

        if (addon == null)
            return new CharaActionResult(CharaActionStatus.NotCharaSelect, enter);

        if (AnyLobbyDialog())
            return new CharaActionResult(CharaActionStatus.DialogOpen, enter);

        // 角色列表是按服务器现建现存的一份; 存着的不是当前服务器那份时, 里面的位置对回调没有意义
        if (!ListBelongsToCurrentWorld(lobby, out var world, out var listWorld))
            return new CharaActionResult(CharaActionStatus.ListWorldMismatch, enter, World: world, ListWorld: listWorld);

        var found = FindListCharacter(lobby, who, contentId);

        if (found.Failure != null)
            return new CharaActionResult(found.Failure.Value, enter);

        if (found.Status == PickStatus.None)
            return new CharaActionResult(CharaActionStatus.NotInList, enter);

        if (found.Status == PickStatus.Ambiguous)
            return new CharaActionResult(CharaActionStatus.Ambiguous, enter);

        var row      = found.Row!;
        var position = found.Position;

        if (enter && CharaLogic.CheckEnter(row, lobby->TemporaryLocked) is { } blocked)
            return new CharaActionResult(blocked, enter, position, row.ContentId, row.LoginFlags, row.ClickFlags);

        FireInts(addon, true, CALLBACK_SELECT_CHARA, position);

        int selected = lobby->DisplayedCharacterIndex;
        var hovered  = lobby->HoveredCharacterContentId;

        if (selected != position || hovered != row.ContentId)
            return new CharaActionResult(CharaActionStatus.NotApplied, enter, position, row.ContentId, row.LoginFlags, row.ClickFlags, selected, hovered);

        if (!enter)
            return new CharaActionResult(CharaActionStatus.Ok, enter, position, row.ContentId, row.LoginFlags, row.ClickFlags, selected, hovered);

        FireInts(addon, false, CALLBACK_CLICK_CHARA, 0, position);

        selected = lobby->DisplayedCharacterIndex;
        hovered  = lobby->HoveredCharacterContentId;

        var status = selected == position && hovered == row.ContentId ? CharaActionStatus.Ok : CharaActionStatus.ClickedOther;
        return new CharaActionResult(status, enter, position, row.ContentId, row.LoginFlags, row.ClickFlags, selected, hovered);
    }

    // ---- DIALOG -----------------------------------------------------------------

    public string Dialog(LobbyCommandKind kind, ulong expectedContentId)
    {
        var lobby = Lobby();
        var where = ReadWhere();

        if (lobby == null)
            return LobbyReplies.POINTERS_UNAVAILABLE;

        switch (kind)
        {
            case LobbyCommandKind.DialogYes:
            case LobbyCommandKind.DialogNo:
            {
                var yesno = Addon("SelectYesno");

                var verdict = kind == LobbyCommandKind.DialogYes
                                  ? DialogGate.DecideYes
                                  (
                                      new YesGateFacts
                                      (
                                          where,
                                          yesno != null,
                                          yesno != null && yesno->IsReady,
                                          lobby->DialogAddonId,
                                          yesno != null ? yesno->Id : 0u,
                                          lobby->TemporaryLocked,
                                          ContentIdAboutToLogin(lobby),
                                          expectedContentId
                                      )
                                  )
                                  : DialogGate.DecideNo(where, yesno != null, yesno != null && yesno->IsReady);

                if (verdict == DialogVerdict.Click)
                    yesno->FireCallbackInt(kind == LobbyCommandKind.DialogYes ? CALLBACK_YES : CALLBACK_NO);

                return LobbyReplies.Dialog(verdict, "SelectYesno");
            }

            default:
                return DialogOk(lobby, where, kind == LobbyCommandKind.DialogNotice);
        }
    }

    private string DialogOk(AgentLobby* lobby, LobbyWhere where, bool notice)
    {
        // 标题界面的错误码框: DIALOG OK 点它的确定按钮
        var dialogue = notice || where == LobbyWhere.InGame ? null : Addon("Dialogue");

        if (dialogue != null)
        {
            var button = dialogue->GetComponentButtonById(DIALOGUE_OK_BUTTON_ID);
            return LobbyReplies.Dialog(Plausible(button) && ClickButton(dialogue, button) ? DialogVerdict.Click : DialogVerdict.NoButton, "Dialogue");
        }

        var ok = Addon("SelectOk");

        var verdict = DialogGate.DecideOk
        (
            new OkGateFacts
            (
                where,
                ok != null,
                lobby->DialogAddonId,
                ok != null ? ok->Id : 0u,
                lobby->QueuePosition,
                ok != null ? ReadDialogText(ok, true) : string.Empty
            ),
            notice
        );

        if (verdict == DialogVerdict.Click)
            ok->FireCallbackInt(CALLBACK_OK);

        return LobbyReplies.Dialog(verdict, "SelectOk");
    }

    /// <summary>对按钮发一次它自己登记的点击事件（ECommons ClickAddonButton 的做法）</summary>
    private static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        var owner = button->OwnerNode;

        if (!Plausible(owner))
            return false;

        var evt = owner->AtkResNode.AtkEventManager.Event;

        if (!Plausible(evt))
            return false;

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    // ---- WHOAMI -----------------------------------------------------------------

    public WhoAmISnapshot? ReadWhoAmI(out string? failure)
    {
        failure = null;

        var lobby = Lobby();
        var state = PlayerState.Instance();

        if (lobby == null || !Plausible(state))
        {
            failure = LobbyReplies.POINTERS_UNAVAILABLE;
            return null;
        }

        var loggedIn = lobby->IsLoggedIn;
        var inZone   = lobby->IsLoggedIntoZone;

        // 读盘中途 ContentId 可能还是 0, 当作还没载入
        if (!state->IsLoaded || state->ContentId == 0)
            return new WhoAmISnapshot(false, 0, 0, 0, string.Empty, string.Empty, loggedIn, inZone, 0, string.Empty);

        var name = state->CharacterNameString;

        if (!LobbyTextRules.IsPlausibleName(name, false))
        {
            failure = "FAIL bad-name";
            return null;
        }

        // 世界信息在大厅数据里, 只有登录之后才是这个角色的
        ushort current = 0, home = 0;
        ulong lobbyCid = 0;
        string currentName = string.Empty, homeName = string.Empty;

        if (loggedIn)
        {
            lobbyCid    = lobby->LobbyData.ContentId;
            current     = lobby->LobbyData.CurrentWorldId;
            home        = lobby->LobbyData.HomeWorldId;
            currentName = WorldName(current, string.Empty);
            homeName    = WorldName(home, string.Empty);
        }

        return new WhoAmISnapshot(true, state->ContentId, current, home, currentName, homeName, loggedIn, inZone, lobbyCid, name);
    }

    /// <summary>一次查找的结果: 要么失败（Failure）, 要么 Pick 的三种结局</summary>
    private readonly record struct PickOutcome(CharaActionStatus? Failure, PickStatus Status, int Position, CharaRow? Row)
    {
        public static PickOutcome Fail(CharaActionStatus status) => new(status, PickStatus.None, -1, null);

        public static PickOutcome From(PickResult pick) => new(null, pick.Status, pick.Position, pick.Row);
    }
}
