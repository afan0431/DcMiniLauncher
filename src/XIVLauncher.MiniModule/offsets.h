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

    // LobbyData.LobbyUIClient 在 LobbyData+0x8 (AgentLobby.cs:110), 而 DCTraveler 的
    // LobbyUIClientExposed 把 Context 定在 +0x18、State 定在 +0x158 —— 折算到 AgentLobby 基址:
    inline constexpr uintptr_t AGENT_LOBBY_UI_CLIENT         = AGENT_LOBBY_LOBBY_DATA + 0x8;   // 0x48
    inline constexpr uintptr_t AGENT_LOBBY_UI_CLIENT_CONTEXT = AGENT_LOBBY_UI_CLIENT + 0x18;   // 0x60
    inline constexpr uintptr_t AGENT_LOBBY_UI_CLIENT_STATE   = AGENT_LOBBY_UI_CLIENT + 0x158;  // 0x1A0

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
}
