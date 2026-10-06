// 国服客户端的结构体偏移与特征码
//
// 全部抄自 ottercorp 的 FFXIVClientStructs 国服分支（commit 10e32486, 2026-08-15,
// "Merge upstream FFXIVClientStructs for Dalamud 15.0.3.2"), 每条都注明出处文件。
// 换服逻辑本身的规格 = Dalamud-DailyRoutines/DCTraveler 的 Helpers/GameFunctions.cs。
//
// ⚠ 这是 F4 唯一的自维护脆弱面: 游戏大版本更新后这里全部要跟着 CS 重新对一遍。
//   所以模块启动时会跑一次 PROBE 自检（读出当前大厅主机名等), 对不上就别往下写。
//   「自动选角」一节的出处与核对状态单独写在那一节开头。
#pragma once

#include <cstdint>

namespace offsets
{
    // ---- 静态实例 ----------------------------------------------------------
    // Client/System/Framework/Framework.cs:23
    //   [StaticAddress("48 8B 1D ?? ?? ?? ?? 8B 7C 24", 3, isPointer: true)]
    inline constexpr const char* FRAMEWORK_INSTANCE_SIG        = "48 8B 1D ?? ?? ?? ?? 8B 7C 24";
    inline constexpr int         FRAMEWORK_INSTANCE_SIG_OFFSET = 3;

    // ---- Framework ---------------------------------------------------------
    // Framework.cs:149 [VirtualFunction(4)] bool Tick() —— 我们换掉这一项来取得每帧的主线程执行点
    inline constexpr int FRAMEWORK_TICK_VF = 4;

    inline constexpr uintptr_t FRAMEWORK_DEV_CONFIG           = 0x0460; // Framework.cs:38
    inline constexpr uintptr_t FRAMEWORK_NETWORK_MODULE_PROXY = 0x1678; // Framework.cs:53
    inline constexpr uintptr_t FRAMEWORK_UI_MODULE            = 0x2B68; // Framework.cs:103

    // ---- UIModule → AgentModule → AgentLobby -------------------------------
    // Client/UI/UIModuleInterface.cs:48 [VirtualFunction(37)] AgentModule* GetAgentModule()
    inline constexpr int       UI_MODULE_GET_AGENT_MODULE_VF = 37;
    inline constexpr uintptr_t AGENT_MODULE_AGENTS           = 0x20; // AgentModule.cs:17 _agents[509]
    inline constexpr int       AGENT_ID_LOBBY                = 0;    // AgentModule.cs:38 AgentId.Lobby = 0

    // ---- AgentLobby (Client/UI/Agent/AgentLobby.cs) ------------------------
    inline constexpr uintptr_t AGENT_LOBBY_LOBBY_DATA   = 0x40;   // :22
    inline constexpr uintptr_t AGENT_LOBBY_GAME_SESSION = 0xDC8;  // :34  Utf8String, 即 DEV.TestSID
    inline constexpr uintptr_t AGENT_LOBBY_IDLE_TIME    = 0x12A8; // :65  long

    // :74/:75 —— 判断「是否真在游戏里」的可靠信号。
    // ⚠ 别用 addon 猜: 片头动画播放时 _TitleMenu 和 _CharaSelectListMenu 都不在,
    //   光看 addon 会把「动画中」误判成「在游戏里」, 那样编排会在动画里去调登出。
    inline constexpr uintptr_t AGENT_LOBBY_IS_LOGGED_IN        = 0x12D8;
    inline constexpr uintptr_t AGENT_LOBBY_IS_LOGGED_INTO_ZONE = 0x12D9;

    // LobbyData.LobbyUIClient 在 LobbyData+0x8 (AgentLobby.cs:110), 而 DCTraveler 的
    // LobbyUIClientExposed 把 Context 定在 +0x18、State 定在 +0x158 —— 折算到 AgentLobby 基址:
    inline constexpr uintptr_t AGENT_LOBBY_UI_CLIENT         = AGENT_LOBBY_LOBBY_DATA + 0x8;   // 0x48
    inline constexpr uintptr_t AGENT_LOBBY_UI_CLIENT_CONTEXT = AGENT_LOBBY_UI_CLIENT + 0x18;   // 0x60
    inline constexpr uintptr_t AGENT_LOBBY_UI_CLIENT_STATE   = AGENT_LOBBY_UI_CLIENT + 0x158;  // 0x1A0

    // ---- 选角列表（WHOLIST）----------------------------------------------------
    // 出处: ottercorp/FFXIVClientStructs 国服分支 7bbe59afc（2026-09-18）AgentLobby.cs。
    // 上面那几个老字段在该版本里偏移没变, 说明 AgentLobby 这段布局近期稳定。
    // LobbyData.CharaSelectEntries (:112) = StdVector<CharaSelectCharacterEntry*>, 在 LobbyData+0x8D8
    inline constexpr uintptr_t AGENT_LOBBY_CHARA_SELECT_ENTRIES   = AGENT_LOBBY_LOBBY_DATA + 0x8D8; // 0x918
    inline constexpr uintptr_t STD_VECTOR_FIRST                   = 0x00;
    inline constexpr uintptr_t STD_VECTOR_LAST                    = 0x08;
    inline constexpr uintptr_t AGENT_LOBBY_SELECTED_CHARA_INDEX   = 0x1241; // :38 byte
    inline constexpr uintptr_t AGENT_LOBBY_HOVERED_CONTENT_ID     = 0x1248; // :40 ulong
    inline constexpr uintptr_t AGENT_LOBBY_HOVERED_CHARA_INDEX    = 0x12CD; // :70 sbyte
    inline constexpr uintptr_t AGENT_LOBBY_SELECTED_CONTENT_ID    = 0x12D0; // :72 ulong
    inline constexpr uintptr_t AGENT_LOBBY_WORLD_ID               = 0x1254; // :44 ushort, 选角界面当前选中的服务器
    // CharaSelectEntries 是按服务器现建现存的一份, 这里记着它是哪个服务器的（World 行号）。CS 没有这个字段。
    // 静态: 取列表的函数（本机 exe RVA 0x4AE770）开头 cmp dx,[LobbyData+0x8F0], 相等直接返回 LobbyData+0x8D8,
    //       不等就清空重建并把新的服务器写到这里: 先收原始服务器是它的角色, 再收当前服务器是它的角色。实机核对: 否
    inline constexpr uintptr_t AGENT_LOBBY_CHARA_SELECT_ENTRIES_WORLD = AGENT_LOBBY_LOBBY_DATA + 0x8F0; // 0x930 ushort

    // AtkUnitBase::FireCallback(uint valueCount, AtkValue* values, bool close) —— AtkUnitBase.cs:217
    // 本机 exe 静态核对: 唯一命中, 目标 RVA 0x6740E0。不在必中集合里, 用到的命令各自按需解析。
    inline constexpr const char* FIRE_CALLBACK_SIG           = "E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20";

    // CharaSelectCharacterEntry (:131, Size 0x6F8)
    inline constexpr uintptr_t CHARA_ENTRY_CONTENT_ID      = 0x08;  // ulong = SDO 的 roleId
    inline constexpr uintptr_t CHARA_ENTRY_INDEX           = 0x10;  // byte
    inline constexpr uintptr_t CHARA_ENTRY_LOGIN_FLAGS     = 0x11;  // byte, 见下
    inline constexpr uintptr_t CHARA_ENTRY_CURRENT_WORLD   = 0x18;  // ushort
    inline constexpr uintptr_t CHARA_ENTRY_HOME_WORLD      = 0x1A;  // ushort
    inline constexpr uintptr_t CHARA_ENTRY_NAME            = 0x2C;  // char[32] UTF-8
    inline constexpr uintptr_t CHARA_ENTRY_CURRENT_WORLD_NAME = 0x4C; // char[32]
    inline constexpr uintptr_t CHARA_ENTRY_HOME_WORLD_NAME = 0x6C;  // char[32]
    inline constexpr size_t    CHARA_ENTRY_NAME_LEN        = 32;

    // LoginFlags (:156): DCTraveling=16 / Unk32=32 —— DCTraveler 把这两位都当「超域中」
    // (ContextMenuManager.cs: 有其一就只给「返回至原始大区」)
    inline constexpr unsigned char LOGIN_FLAG_DC_TRAVELING = 16;
    inline constexpr unsigned char LOGIN_FLAG_UNK32        = 32;

    // =========================================================================
    // 自动选角（LOBBYSTATE / CHARAS / WHOAMI / FOCUSCHARA / SELECTCHARA / ENTERCHARA / DIALOG）
    //
    // 出处: ottercorp/FFXIVClientStructs 国服分支 6e404c60（2026-09-30, 对应国服 2026.09.15.0000.0000）。
    // 「静态」= 在本机磁盘上的 ffxiv_dx11.exe（同版本）里只读反汇编, 看到代码按这个偏移读写;
    //           只说明代码里有这样的访问, 不说明运行时取值。
    // ⚠ 这一节的每一项「是否已在实机核对」都是: 否。实机只读采样（test/probe-readonly.ps1）对上之前,
    //   依赖它们的操作命令不得用于正式流程。
    // =========================================================================

    // MSVC std::vector: first / last / end 三个指针; end 只拿来做合理性校验
    inline constexpr uintptr_t STD_VECTOR_END = 0x10;

    // 读向量时的元素数上限, 超过就当读歪了。一个大区 8 个服务器 × 每服 8 个角色 = 64
    inline constexpr int LOBBY_MAX_CHARACTERS = 64;
    inline constexpr int LOBBY_MAX_WORLDS     = 64;

    // ---- LobbyUIClient（LobbyUIClient.cs; 结构在 AgentLobby+0x48）----------
    // CurrentDataCenterWorlds (:18) = StdVector<LobbyDataCenterWorldEntry>, 元素内联。
    // 静态: ReceiveEvent case 21 里 lea rcx,[agent+0x78] 后按 0x54 做除法取数量。实机核对: 否
    inline constexpr uintptr_t AGENT_LOBBY_DC_WORLDS  = AGENT_LOBBY_UI_CLIENT + 0x30;  // 0x78
    inline constexpr size_t    DC_WORLD_ENTRY_SIZE    = 0x54;  // :27
    // :28 ushort, World 表的行号。静态: 按下标取服务器的函数（RVA 0x4AF390）读的就是 元素[下标]+0 的 word。实机: 否
    inline constexpr uintptr_t DC_WORLD_ENTRY_ID      = 0x00;

    // CurrentDataCenterCharacters (:22) = StdVector<LobbyUIClientCharacterEntry>, 元素内联（整个大区的角色）。
    // 前部字段偏移与下面的 CHARA_ENTRY_* 相同（LobbyUIClient.cs:48-58）。
    // 静态: 重建当前服务器角色列表的函数（RVA 0x4AE770）从 LobbyData+0x100 取这个向量, 按 0x758 一个遍历,
    //       用 +0x8 / +0x18 / +0x1A 筛选; 按序号取条目的函数（RVA 0x4AF110）检查 +0x740 与 +0x750。实机核对: 否
    inline constexpr uintptr_t AGENT_LOBBY_DC_CHARACTERS        = AGENT_LOBBY_UI_CLIENT + 0xF8;  // 0x140
    inline constexpr size_t    DC_CHARA_ENTRY_SIZE              = 0x758;  // :46
    inline constexpr uintptr_t DC_CHARA_ENTRY_CONTENT_ID_MIRROR = 0x740;  // :64 ulong, 客户端自己要求它等于 +0x8
    inline constexpr uintptr_t DC_CHARA_ENTRY_DELETED_FLAG      = 0x750;  // :67 byte, 客户端自己要求它为 0

    // ---- AgentLobby 其它字段（AgentLobby.cs）--------------------------------
    // WorldIndex (:43) short = 在 CurrentDataCenterWorlds 里的下标。静态: case 24 / 25 写入, case 21 读取。实机: 否
    inline constexpr uintptr_t AGENT_LOBBY_WORLD_INDEX      = 0x1252;
    // DialogAddonId (:46) uint。静态: OpenLoginWaitDialog 把新开的对话框 id 写到这里。实机: 否
    inline constexpr uintptr_t AGENT_LOBBY_DIALOG_ADDON_ID  = 0x1258;
    // LobbyUpdateStage (:61) byte。静态: 排队分支里 mov word [agent+0x129C], 0x11F。实机: 否
    inline constexpr uintptr_t AGENT_LOBBY_UPDATE_STAGE     = 0x129C;
    // LobbyUIStage (:63) byte。静态: 多处写入。实机: 否
    inline constexpr uintptr_t AGENT_LOBBY_UI_STAGE         = 0x129F;
    // QueuePosition (:68) int。静态: UpdateLoginPosition 里 mov [agent+0x12C8], r15d。实机: 否（不排队时的取值未知）
    inline constexpr uintptr_t AGENT_LOBBY_QUEUE_POSITION   = 0x12C8;
    // TemporaryLocked (:79) bool。静态: eventKind=3 确认登录时置 1, 点击登录的处理函数开头检查它。实机: 否
    inline constexpr uintptr_t AGENT_LOBBY_TEMPORARY_LOCKED = 0x13D8;

    // LobbyData 里「已登录角色」的那几项（AgentLobby.cs:115-121, LobbyData 在 AgentLobby+0x40）。
    // Dalamud 只在 IsLoggedIn 为真时采信。静态: 相对偏移在大厅代码里有引用, 只能算旁证。实机: 否
    inline constexpr uintptr_t AGENT_LOBBY_LOGGED_CONTENT_ID  = AGENT_LOBBY_LOBBY_DATA + 0x8F8;  // 0x938 ulong
    inline constexpr uintptr_t AGENT_LOBBY_HOME_WORLD_NAME    = AGENT_LOBBY_LOBBY_DATA + 0x900;  // 0x940 Utf8String
    inline constexpr uintptr_t AGENT_LOBBY_CURRENT_WORLD_NAME = AGENT_LOBBY_LOBBY_DATA + 0x9D0;  // 0xA10 Utf8String
    inline constexpr uintptr_t AGENT_LOBBY_CURRENT_WORLD_ID   = AGENT_LOBBY_LOBBY_DATA + 0xA3C;  // 0xA7C ushort
    inline constexpr uintptr_t AGENT_LOBBY_HOME_WORLD_ID      = AGENT_LOBBY_LOBBY_DATA + 0xA3E;  // 0xA7E ushort

    // ---- 选角界面的回调编号（7.0 起; 6.x 的 9 / 10 / 17 / 18 已失效）--------
    // 对 addon 发 FireCallback, 以 eventKind=0 落到 AgentLobby::ReceiveEvent（虚表第 0 项, 本机 exe RVA 0x4E0E30）。
    // 静态: 跳转表里 case 21 / 25 / 29 的行为与下面的注释一致; addon 一侧怎么转给 Agent 没有反汇编。实机核对: 否
    // 出处: ECommons _CharaSelectListMenu.cs / _CharaSelectWorldServer.cs, DailyRoutines AutoLogin.cs, Aida-Enna AutoLogin
    inline constexpr int LOBBY_EVENT_SELECT_CHARA = 21;  // _CharaSelectListMenu ← (21, 序号): 只高亮
    inline constexpr int LOBBY_EVENT_SELECT_WORLD = 25;  // _CharaSelectWorldServer ← (25, 0, 服务器下标)
    inline constexpr int LOBBY_EVENT_CLICK_CHARA  = 29;  // _CharaSelectListMenu ← (29, 0, 序号): 左键点击, 弹登录确认框

    // LoginFlags (AgentLobby.cs:156): Locked=1 / NameChangeRequired=2 / MissingExVersionForLogin=4 / (8 未命名) / DCTraveling=16 / Unk32=32。
    // 静态（本机 exe, 点角色的处理函数 RVA 0x4D4C80 按这几位分流, 掩码取自 .rdata）:
    //   1 → 什么都不做; 8 → RVA 0x4DDB50; 32 → RVA 0x4DEA50; 2 → 改名相关的提示; 只有这些位都没有时才走到登录确认框。
    //   16（超域中, 人就在这个大区）照常走登录确认框。
    // 所以除 16 之外带任何一位的角色, ENTERCHARA 一律拒绝 —— 点下去弹出来的不是登录确认框。实机核对: 否
    inline constexpr unsigned char LOGIN_FLAG_BLOCKING_MASK = 0xFF & ~LOGIN_FLAG_DC_TRAVELING;

    // 大区角色条目 +0x74C 的一组标志（CS 没有这个字段, 含义不明）。
    // 静态（RVA 0x4D4F22 / 0x4D5015）: bit0 置位时点角色先弹 Lobby 表第 76 行的是/否框（回调类别 16）;
    //   bit2 置位时先弹第 629 行的是/否框（回调类别 2, 答「是」之后才轮到登录确认框）;
    //   都没有才直接弹登录确认框（Lobby 表第 25 / 95 / 96 行, 回调类别 3, RVA 0x4DD870）。
    // 那两种是/否框在问什么不知道, 所以带这两位的角色 ENTERCHARA 也拒绝, 交给人点。实机核对: 否
    inline constexpr uintptr_t DC_CHARA_ENTRY_CLICK_FLAGS       = 0x74C;  // uint
    inline constexpr unsigned  DC_CHARA_ENTRY_CLICK_PROMPT_MASK = 0x5;

    // ---- PlayerState（Client/Game/UI/PlayerState.cs）-----------------------
    // [StaticAddress(..., 3)] 直接是结构体地址（不是指针）。
    // 静态: 唯一命中, RVA 0x2ACC3B8, 与 UIState.Instance（RVA 0x2ACB980）正好差 UIState.PlayerState 的偏移 0xA38。
    // 不在必中集合里: 命中不了只让 WHOAMI 返回 FAIL sigscan-failed:PlayerState。
    inline constexpr const char* PLAYER_STATE_SIG        = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 06 F6 43 18 02";
    inline constexpr int         PLAYER_STATE_SIG_OFFSET = 3;
    inline constexpr uintptr_t PLAYER_STATE_IS_LOADED  = 0x00;  // :14 bool。静态: 否  实机: 否
    inline constexpr uintptr_t PLAYER_STATE_NAME       = 0x01;  // :15 char[64] UTF-8。静态: 否  实机: 否
    inline constexpr size_t    PLAYER_STATE_NAME_LEN   = 64;
    inline constexpr uintptr_t PLAYER_STATE_CONTENT_ID = 0x68;  // :19 ulong。静态: 否  实机: 否

    // ---- AtkUnitBase / 对话框（Component/GUI/AtkUnitBase.cs）----------------
    // 以下各项静态: 否  实机: 否（0x1E4 紧挨着右键菜单已在用的 BlockedParentId 0x1EA, 仅此旁证）
    inline constexpr uintptr_t ATK_UNIT_BASE_ATK_VALUES       = 0x178;  // :30 AtkValue*
    inline constexpr uintptr_t ATK_UNIT_BASE_FLAGS198         = 0x198;  // :38 uint
    inline constexpr unsigned  ATK_UNIT_BASE_VISIBLE_BIT      = 21;     // :35 VisibilityState 占 bit 20-23, Show = 1<<1
    inline constexpr uintptr_t ATK_UNIT_BASE_FLAGS1A1         = 0x1A1;  // :45 byte
    inline constexpr unsigned  ATK_UNIT_BASE_READY_BIT        = 0;      // :42 IsReady
    inline constexpr uintptr_t ATK_UNIT_BASE_ATK_VALUES_COUNT = 0x1E2;  // :112 ushort
    inline constexpr uintptr_t ATK_UNIT_BASE_ID               = 0x1E4;  // :113 ushort

    // AddonSelectYesno.cs:13 / AddonSelectOk.cs:13 —— 两个对话框的提示文字节点在同一偏移。静态: 否  实机: 否
    inline constexpr uintptr_t ADDON_SELECT_PROMPT_TEXT = 0x238;  // AtkTextNode*
    inline constexpr uintptr_t ATK_TEXT_NODE_NODE_TEXT  = 0xD0;   // AtkTextNode.cs:22 Utf8String。静态: 否  实机: 否

    // AtkValue.cs:3-15 —— 类型的低 4 位; 0x20 是 Managed 标志
    inline constexpr unsigned ATK_VALUE_TYPE_MASK         = 0xF;
    inline constexpr unsigned ATK_VALUE_TYPE_STRING       = 0x8;
    inline constexpr unsigned ATK_VALUE_TYPE_CONST_STRING = 0xA;

    inline constexpr int SELECT_YESNO_NO = 1;  // FireCallbackInt(1) = 否（Aida-Enna AutoLogin）。实机: 否
    inline constexpr int SELECT_OK_OK    = 0;  // SelectOk: FireCallbackInt(0) = 确定。实机: 否

    // Dialogue（大厅错误 / 断线提示框）的确定按钮 = 组件按钮 4（DailyRoutines AutoLogin.cs:226, AutoRetainer BailoutManager.cs:126）。
    // 点法同各插件的 ClickAddonButton: 取按钮所属节点事件链上的那条 ButtonClick, 用它自己的 Param 调 addon 的 ReceiveEvent。
    // 以下各项静态: 否  实机: 否
    inline constexpr int       DIALOGUE_OK_BUTTON_ID         = 4;
    inline constexpr uintptr_t ATK_COMPONENT_BASE_OWNER_NODE = 0xA8;  // AtkComponentBase.cs:17 AtkComponentNode*（开头就是 AtkResNode）
    inline constexpr uintptr_t ATK_EVENT_PARAM               = 0x18;  // AtkEvent.cs:118 uint
    inline constexpr uintptr_t ATK_EVENT_NEXT                = 0x20;  // AtkEvent.cs:119 AtkEvent*
    inline constexpr uintptr_t ATK_EVENT_TYPE                = 0x28;  // AtkEvent.cs:120,128 State.EventType byte

    // ---- NetworkModule (Application/Network/NetworkModule.cs) --------------
    inline constexpr uintptr_t NETWORK_MODULE_PROXY_MODULE = 0x08;  // Client/Network/NetworkModuleProxy.cs:10
    inline constexpr uintptr_t NETWORK_MODULE_LOBBY_HOSTS  = 0x068; // :12 FixedSizeArray14<Utf8String>
    inline constexpr uintptr_t NETWORK_MODULE_SAVE_DATA    = 0x628; // :15 SaveDataBankHost
    inline constexpr uintptr_t NETWORK_MODULE_ACTIVE_LOBBY = 0x708; // :20 ActiveLobbyHost

    // ---- Utf8String (Client/System/String/Utf8String.cs) -------------------
    inline constexpr uintptr_t UTF8_STRING_PTR      = 0x00;
    inline constexpr uintptr_t UTF8_STRING_BUF_SIZE = 0x08; // 默认 0x40, 串短时 StringPtr 指向内联缓冲
    inline constexpr uintptr_t UTF8_STRING_BUF_USED = 0x10; // = 字符数 + 1
    inline constexpr uintptr_t UTF8_STRING_LENGTH   = 0x18; // ⚠ 国服客户端上实测恒为 0, 不能拿它当长度
    inline constexpr size_t    UTF8_STRING_SIZE     = 0x68;

    // ---- ConfigBase / ConfigEntry (Common/Configuration/ConfigBase.cs) -----
    inline constexpr uintptr_t CONFIG_BASE_COUNT   = 0x14;
    inline constexpr uintptr_t CONFIG_BASE_ENTRIES = 0x18;
    inline constexpr uintptr_t CONFIG_ENTRY_NAME   = 0x10; // char*
    inline constexpr uintptr_t CONFIG_ENTRY_VALUE  = 0x20; // union, 字符串项存 Utf8String*
    inline constexpr size_t    CONFIG_ENTRY_SIZE   = 0x38;

    // ---- UI: 在标题界面点「开始游戏」 --------------------------------------
    // AtkUnitManager 走 AtkStage 拿, 不走 UIModule 那条长链 ——
    // ⚠ 2026-08-15 实测: UIModule+0xD2660 → +0x13420 那条算出来的指针是错的
    //   (AllLoadedUnitsList.Count 读出来是 0, 一个 addon 都列不出来)。
    //   AtkStage 只有「静态指针 → +0x20」两步, 短且是 CS 自己 AtkStage.Instance() 的走法。
    // Component/GUI/AtkStage.cs:15
    inline constexpr const char* ATK_STAGE_SIG        = "48 8B 05 ?? ?? ?? ?? 4C 8B 40 18 45 8B 40 18";
    inline constexpr int         ATK_STAGE_SIG_OFFSET = 3;
    // AtkStage.cs:20 —— RaptureAtkUnitManager 继承 AtkUnitManager, 可直接当 AtkUnitManager* 用
    inline constexpr uintptr_t ATK_STAGE_UNIT_MANAGER = 0x20;

    inline constexpr uintptr_t ATK_COMPONENT_BASE_RES_NODE = 0xA0; // AtkComponentBase.cs:16
    inline constexpr uintptr_t ATK_RES_NODE_EVENT_MANAGER  = 0x18; // AtkResNode.cs:18, +0 即 AtkEvent*

    // 诊断用: 枚举已加载的 addon —— AtkUnitManager.cs:27 / AtkUnitList.cs:8,9 / AtkUnitBase.cs:16
    inline constexpr uintptr_t ATK_UNIT_MANAGER_ALL_LOADED = 0x6900;
    inline constexpr uintptr_t ATK_UNIT_LIST_ENTRIES       = 0x08;
    inline constexpr uintptr_t ATK_UNIT_LIST_COUNT         = 0x808;
    inline constexpr uintptr_t ATK_UNIT_BASE_NAME          = 0x08;

    inline constexpr int ATK_UNIT_BASE_RECEIVE_EVENT_VF = 2;  // AtkEventListener.cs:15
    inline constexpr int ATK_EVENT_TYPE_BUTTON_CLICK    = 25; // AtkEvent.cs:32
    inline constexpr int TITLE_MENU_LOGIN_BUTTON_ID     = 4;  // DCTraveler GameFunctions.LoginInGame

    // ---- 函数特征码 --------------------------------------------------------
    // 前两条抄自 DCTraveler 的 GameFunctions.cs 静态构造; 都以 E8 开头, 需按调用目标解析
    inline constexpr const char* RETURN_TO_TITLE_SIG        = "E8 ?? ?? ?? ?? C6 87 ?? ?? ?? ?? ?? 33 C0";
    inline constexpr const char* RELEASE_LOBBY_CONTEXT_SIG  = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8B 85 ?? ?? ?? ?? 48 85 C0";

    // Utf8String::SetString —— Utf8String.cs:113
    inline constexpr const char* UTF8_SET_STRING_SIG        = "E8 ?? ?? ?? ?? 4D 39 2E";

    // AtkUnitManager::GetAddonByName —— Component/GUI/AtkUnitManager.cs:95
    inline constexpr const char* GET_ADDON_BY_NAME_SIG      = "E8 ?? ?? ?? ?? 48 8B F8 41 B0 01";

    // AtkUnitBase::GetComponentButtonById —— Component/GUI/AtkUnitBase.cs:198
    inline constexpr const char* GET_COMPONENT_BUTTON_SIG   = "E8 ?? ?? ?? ?? 8D 3C 36";

    // ---- 游戏内登出用 ------------------------------------------------------
    // 角色还在世界里时不能调 returnToTitle（实测必崩), 得走游戏自己的登出流程:
    // 发文本命令 /logout → 确认 Yes/No → 游戏倒数几秒后回到角色选择界面。
    //
    // UIModule::ProcessChatBoxEntry —— Client/UI/UIModule.cs:125
    inline constexpr const char* PROCESS_CHATBOX_ENTRY_SIG  = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9";
    // Utf8String::Ctor / Dtor —— Client/System/String/Utf8String.cs:104,110
    inline constexpr const char* UTF8_CTOR_SIG              = "E8 ?? ?? ?? ?? F7 C3";
    inline constexpr const char* UTF8_DTOR_SIG              = "E8 ?? ?? ?? ?? C7 44 F5";
    // AtkUnitBase::FireCallbackInt —— Component/GUI/AtkUnitBase.cs:214, 用它点 SelectYesno 的「是」
    inline constexpr const char* FIRE_CALLBACK_INT_SIG      = "E9 ?? ?? ?? ?? 83 C3 F9";

    inline constexpr int SELECT_YESNO_YES = 0; // FireCallbackInt(0) = 是

    // 更底层的一条: AgentLobby::HandleLogout(bool isExiting, byte a3) —— Client/UI/Agent/AgentLobby.cs:102
    // 这就是游戏自己在登出时调的处理函数, 不经聊天框、不弹确认框。
    // isExiting=false 表示登出到角色选择（true 是直接退出游戏), a3 是大厅那边按帧算的倒数。
    inline constexpr const char* AGENT_LOBBY_HANDLE_LOGOUT_SIG = "40 56 41 56 41 57 48 83 EC 40 80 B9";

    // =========================================================================
    // 选角界面右键菜单（contextmenu.cpp）
    // 出处: ottercorp/Dalamud a5745232 Game/Gui/ContextMenu/ContextMenu.cs（与 goatcorp 字节一致）,
    //       FFXIVClientStructs 国服分支 7bbe59af, DCTraveler 006cdce7 Managers/ContextMenuManager.cs。
    // =========================================================================

    // UIModuleInterface.cs: [VirtualFunction(7)] RaptureAtkModule* GetRaptureAtkModule()
    inline constexpr int UI_MODULE_GET_RAPTURE_ATK_MODULE_VF = 7;

    // RaptureAtkModule 虚表第 22 项 —— Dalamud 叫它 AtkModuleVf22OpenAddonByAgent, 打开右键菜单走这里。
    // ⚠ CS 没记这一项, 原型只来自 Dalamud 的委托:
    //   ushort (AtkModule*, byte* addonName, int valueCount, AtkValue* values, AgentInterface* agent, nint a7, bool a8)
    inline constexpr int RAPTURE_ATK_MODULE_OPEN_ADDON_BY_AGENT_VF = 22;

    // AddonContextMenu.cs [VirtualFunction(74)] bool OnMenuSelected(int selectedIdx, byte a3)
    inline constexpr int ADDON_CONTEXT_MENU_ON_MENU_SELECTED_VF = 74;

    inline constexpr int AGENT_ID_CONTEXT = 9; // AgentModule.cs AgentId.Context —— 右键菜单 MenuType.Default

    inline constexpr uintptr_t ATK_UNIT_BASE_BLOCKED_PARENT_ID = 0x1EA; // AtkUnitBase.cs, ushort

    // AtkValue: 0x10 字节, +0 u32 Type, +8 值
    inline constexpr size_t   ATK_VALUE_SIZE      = 0x10;
    inline constexpr unsigned ATK_VALUE_TYPE_INT  = 3;
    inline constexpr unsigned ATK_VALUE_TYPE_UINT = 5;

    // ContextMenu 的 AtkValue 头 8 项: [0]=项数 N, [2]=返回箭头掩码, [3]=子菜单掩码;
    // 之后 [8, 8+N) 是各项名字, 若有置灰项再跟 [8+N, 8+2N) 的 Int 0/1
    inline constexpr int CONTEXT_MENU_HEADER_COUNT = 8;

    // AtkUnitManager::GetAddonById(ushort id) —— AtkUnitManager.cs
    inline constexpr const char* GET_ADDON_BY_ID_SIG = "E8 ?? ?? ?? ?? 8B 6B 20";
    // IMemorySpace::GetUISpace() (静态) / IMemorySpace::Free(void*, ulong) (静态) —— IMemorySpace.cs
    inline constexpr const char* GET_UI_SPACE_SIG       = "E8 ?? ?? ?? ?? 48 8B D7 41 B8";
    inline constexpr const char* MEMORY_SPACE_FREE_SIG  = "E8 ?? ?? ?? ?? FF 4B 78";
    inline constexpr int         MEMORY_SPACE_MALLOC_VF = 3; // void* Malloc(ulong size, ulong alignment)
    // AtkValue::SetManagedString(byte*) —— AtkValue.cs; 把字符串拷进游戏自己的内存
    inline constexpr const char* ATK_VALUE_SET_MANAGED_STRING_SIG = "E8 ?? ?? ?? ?? 41 03 ED";
}
