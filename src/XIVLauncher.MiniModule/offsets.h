// 国服客户端的结构体偏移与特征码
//
// 全部抄自 ottercorp 的 FFXIVClientStructs 国服分支（commit 10e32486, 2026-08-15,
// "Merge upstream FFXIVClientStructs for Dalamud 15.0.3.2"), 每条都注明出处文件。
// 换服逻辑本身的规格 = Dalamud-DailyRoutines/DCTraveler 的 Helpers/GameFunctions.cs。
//
// ⚠ 这是 F4 唯一的自维护脆弱面: 游戏大版本更新后这里全部要跟着 CS 重新对一遍。
//   所以模块启动时会跑一次 PROBE 自检（读出当前大厅主机名等), 对不上就别往下写。
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

    // 选角界面切服务器（FOCUSCHARA）—— 做法抄 DailyRoutines Modules/General/AutoLogin.cs SelectWorld:
    //   对 _CharaSelectWorldServer 逐个发 Callback(9, 0, i), 发完看 AgentLobby.WorldId 对上了就再发 Callback(10, 0, i)
    // AtkUnitBase::FireCallback(uint valueCount, AtkValue* values, bool close) —— AtkUnitBase.cs:217
    inline constexpr const char* FIRE_CALLBACK_SIG           = "E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20";
    inline constexpr int         WORLD_SERVER_EVENT_HOVER    = 9;
    inline constexpr int         WORLD_SERVER_EVENT_CONFIRM  = 10;
    inline constexpr int         WORLD_SERVER_MAX_ENTRIES    = 16;

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
