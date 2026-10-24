// 所有角色共用一套游戏配置 · 不依赖游戏的部分
//
// 路径匹配、文件合法性、可比较字节与哈希、本机共享目录的读写、按配置套的命名互斥体。
// 挂钩 FileInterface::OpenFile 的代码只调这里; 离线测试 test/charconfig-test.cpp 也只测这里。
// 本文件不引用任何游戏结构, 不写模块日志: 每个结果都带原因字符串, 由调用方决定怎么记。
//
// 本机目录（根 = %LOCALAPPDATA%\Cat\char-config\, 工作台外壳与本模块共用）:
//   machine.json                       外壳写, 模块只读
//   sets\<setId>\current\<NAME>.DAT    这一套每个文件的最新版
//   sets\<setId>\state.json            每个文件的服务器版本号、哈希、大小、是否待上传、来源
//   sets\<setId>\outbox\<seq>.json     本机发布待上传的说明（先写 .dat 再写 .json: 有 .json 就说明 .dat 完整）
//   sets\<setId>\outbox\<seq>.dat      对应内容
//   modules\<pid>.json                 模块心跳
//
// 所有对 sets\<setId>\ 的写都持有命名互斥体 Local\Cat-CharConfig-<setId>, 写文件一律
// 「同目录临时文件 → MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH)」。state.json 最后写:
// current 里的文件哈希与 state.json 对不上时当作这一套没有这个文件。
//
// JSON 格式（UTF-8, 无 BOM）:
//   machine.json:
//     {"schema":1,"enabled":true,"blockedReason":null,"setId":"…","setName":"…",
//      "categories":["hotbar","macro",…],"updatedAt":"2026-10-24T03:31:00Z"}
//   state.json:
//     {"schema":1,"files":{"HOTBAR.DAT":{"version":3,"sha256":"<64 位小写十六进制>","size":204832,"pending":false,
//       "source":{"workstation":"…","cid":"00400012DC10855C","name":"…","at":"2026-10-24T03:31:00Z"}}}}
//     version = 服务器版本号（未上传过为 0）; pending = 本机发布了、还没上传; 本机发布时 version 不变。
//     读写时保留不认识的字段（顶层与每个文件内）, 外壳加字段不会被模块抹掉。
//   outbox\<seq>.json（seq 为 20 位十进制, 文件名按字典序即按发布顺序）:
//     {"schema":1,"seq":1761276660000,"name":"HOTBAR.DAT","sha256":"…","size":204832,"baseVersion":3,
//      "cid":"00400012DC10855C","charName":"…","at":"…","gamePid":67408}
//     baseVersion = 发布时 state.json 里这个文件的 version。
//   modules\<pid>.json:
//     {"schema":1,"pid":67408,"moduleVersion":"0.9.1","hooked":true,"lastCid":"…","lastName":"…","lastError":"…","updatedAt":"…"}
//     lastName = lastCid 那个号的角色名（查不到为 null）; 旧版模块没有这个字段, 读时缺失当作 null。
//   cid 一律是 16 位大写十六进制字符串（与角色目录名 FFXIV_CHR<cid> 相同）, 不用数字: 超出 double 的精确范围。
//   可空的字符串字段（source 各项、cid、charName、lastCid、lastName、lastError 等）为空时写 null, 读时 null 当作空串。
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

namespace charconfig
{
    using Bytes = std::vector<uint8_t>;

    // ---- 类别与文件 -------------------------------------------------------------
    enum class Category
    {
        Hotbar,     // HOTBAR.DAT
        Macro,      // MACRO.DAT
        Keybind,    // KEYBIND.DAT
        LogFilter,  // LOGFLTR.DAT
        Character,  // COMMON.DAT
        Keyboard,   // CONTROL0.DAT
        Gamepad,    // CONTROL1.DAT
        Hud,        // ADDON.DAT
        Cards,      // GS.DAT
    };

    struct FileSpec
    {
        Category       category;
        const char*    key;       // machine.json categories 里的名字, 如 "hotbar"
        const char*    fileName;  // 规范文件名（大写）, 如 "HOTBAR.DAT"
        const wchar_t* fileNameW;
    };

    constexpr size_t kFileCount = 9;

    const FileSpec* AllFiles();                              // kFileCount 项, 顺序同 Category
    const FileSpec* FindFile(const wchar_t* name, size_t length); // 文件名不分大小写; 不是共用的文件返回 nullptr
    const FileSpec* FindFile(const char* name);
    const FileSpec* FindCategory(const char* key);           // 按类别名找; 区分大小写

    // ---- 路径匹配 ---------------------------------------------------------------
    // 只做尾部字符串比较, 分隔符 '\' 与 '/' 都认, 文件名与十六进制不分大小写。
    struct PathMatch
    {
        const FileSpec* file = nullptr;
        uint64_t        cid  = 0;
        std::wstring    cidHex;        // 16 位大写十六进制
        std::wstring    directory;     // 角色目录（原样, 不含结尾分隔符）
        std::wstring    characterFile; // 角色目录里的 <NAME>.DAT（原样大小写）
    };

    // 读: …\FFXIV_CHR<16 位十六进制>\<NAME>.DAT, NAME 是 9 个共用文件之一
    bool MatchReadPath(const wchar_t* path, PathMatch* out);

    // 写的临时文件: …\FFXIV_CHR<16 位十六进制>\<NAME>.DAT.new.<十六进制>.<进程号十六进制>, 只认进程号等于 pid 的
    // （游戏保存时 OpenFile 打开的是 <NAME>.DAT 本身, 这个临时文件在函数内部生成; 写模式下两种都要认）
    bool MatchWritePath(const wchar_t* path, DWORD pid, PathMatch* out);

    // ---- 合法性 -----------------------------------------------------------------
    // 大小表来自 Cat 父任务 research/live-facts.md 的本机角色目录统计（10-24）:
    //   HOTBAR 204832、MACRO 286752、KEYBIND 24608、LOGFLTR 2080、CONTROL0/1 4201–4204、GS 681–682、ADDON 106528。
    // COMMON 是 UTF-8 文本: 每行「键\t值」「<分节>」或空行, 以 \r\n 结尾, 实测文件末尾还跟一个 0 字节（128 份全是）。
    bool ValidateFile(const FileSpec& spec, const uint8_t* data, size_t length, std::string* reason);

    // ADDON.DAT 结构校验, 规格见 Cat 父任务 research/addon-format.md §4
    bool AddonValid(const uint8_t* data, size_t length, std::string* reason = nullptr);

    // ---- 可比较字节与哈希 -------------------------------------------------------
    // ADDON: 默认表与每套 HUD 的记录排序, 掩码随角色自动变化的窗口（固定的三个, 加上带自动伸缩标记的窗口的高）;
    // 不合法时返回空
    Bytes AddonComparable(const uint8_t* data, size_t length);

    // GS: 去掉末尾的 0
    Bytes GsComparable(const uint8_t* data, size_t length);

    // ADDON 用 AddonComparable、GS 用 GsComparable、其余原样; ADDON 不合法时原样
    Bytes ComparableBytes(const FileSpec& spec, const uint8_t* data, size_t length);

    std::string Sha256Hex(const uint8_t* data, size_t length); // 64 位小写十六进制; 失败返回空
    std::string ComparableHash(const FileSpec& spec, const uint8_t* data, size_t length);

    // 两份合法 ADDON 按可比较字节逐段比较, 列出不同之处:
    //   "header"、"hudHeader"、"tail"、默认表记录 "<键>"、HUD 套 "hud<i>:<键>" / "hud<i>:tail"（键为文件中 4 字节的十六进制）
    std::vector<std::string> AddonDiffKeys(const uint8_t* a, size_t aLength, const uint8_t* b, size_t bLength);

    // 两份合法 ADDON 除去可比较字节已忽略的部分, 不同之处只在写回一侧被重置到正中默认位置（x、y 都是 50.0）的窗口
    // 时为 true: 游戏会把这个号没打开过的窗口重置, 不是人改的
    bool AddonSameIgnoringResets(const uint8_t* baseline, size_t baselineLength, const uint8_t* written,
                                 size_t writtenLength);

    // ---- 文件与目录 -------------------------------------------------------------
    std::string  ToUtf8(const std::wstring& text);
    std::wstring FromUtf8(const std::string& text);
    std::string  UtcNowIso();      // 2026-10-24T03:31:00Z
    ULONGLONG    UnixNowMs();

    std::wstring DefaultRoot();    // %LOCALAPPDATA%\Cat\char-config
    bool ValidSetId(const std::string& setId); // 1–64 个 [0-9A-Za-z_-]: 会拼进路径与互斥体名

    std::wstring SetDirectory(const std::wstring& root, const std::string& setId);
    std::wstring CurrentFilePath(const std::wstring& root, const std::string& setId, const FileSpec& spec);

    // 读整个文件; 不存在时 *missing = true 并返回 false
    bool ReadWholeFile(const std::wstring& path, Bytes* out, bool* missing, std::string* error);
    bool EnsureDirectory(const std::wstring& path);
    // 同目录临时文件写完刷盘, 再 MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH) 换上
    bool AtomicWriteFile(const std::wstring& path, const void* data, size_t length, std::string* error);

    // ---- 命名互斥体 Local\Cat-CharConfig-<setId> --------------------------------
    // 持有者崩溃留下的废弃互斥体也算拿到（state.json 最后写, 现场总是一致的）
    class SetLock
    {
    public:
        SetLock(const std::string& setId, DWORD timeoutMs);
        ~SetLock();
        SetLock(const SetLock&)            = delete;
        SetLock& operator=(const SetLock&) = delete;

        bool               Acquired() const { return m_acquired; }
        const std::string& Error() const { return m_error; }

    private:
        HANDLE      m_handle   = nullptr;
        bool        m_acquired = false;
        std::string m_error;
    };

    // ---- machine.json -----------------------------------------------------------
    struct MachineConfig
    {
        int                      schema  = 0;
        bool                     enabled = false;
        std::string              blockedReason;
        std::string              setId;
        std::string              setName;
        std::vector<std::string> categories;
        std::string              updatedAt;

        // enabled、setId 合法、categories 含这个类别
        bool FileEnabled(const FileSpec& spec) const;
    };

    bool ParseMachineConfig(const std::string& json, MachineConfig* out, std::string* error);
    bool ReadMachineConfig(const std::wstring& root, MachineConfig* out, bool* missing, std::string* error);

    // 按修改时间缓存 machine.json; 最多每 intervalMs 查一次修改时间, 变了才重读。线程安全
    class MachineConfigCache
    {
    public:
        explicit MachineConfigCache(std::wstring root, ULONGLONG intervalMs = 1000);

        // 没有文件或解析失败时返回的配置 enabled = false, error 写明原因
        MachineConfig Get(ULONGLONG nowMs, std::string* error = nullptr);

    private:
        std::wstring  m_root;
        ULONGLONG     m_intervalMs;
        std::mutex    m_mutex;
        bool          m_checked = false;
        ULONGLONG     m_lastCheck = 0;
        FILETIME      m_writeTime{};
        ULONGLONG     m_size = 0;
        bool          m_present = false;
        MachineConfig m_config;
        std::string   m_error;
    };

    // ---- state.json -------------------------------------------------------------
    struct FileSource
    {
        std::string workstation;
        std::string cid;
        std::string name;
        std::string at;
    };

    struct FileState
    {
        std::string name;        // 规范文件名, 如 "HOTBAR.DAT"
        int64_t     version = 0;
        std::string sha256;
        uint64_t    size    = 0;
        bool        pending = false;
        FileSource  source;
        std::string extraJson;   // 不认识的字段, 原样保留（JSON 对象成员列表, 不含花括号）
    };

    struct SetState
    {
        int                    schema = 1;
        std::vector<FileState> files;
        std::string            extraJson; // 顶层不认识的字段

        FileState*       Find(const char* name);
        const FileState* Find(const char* name) const;
    };

    bool        ParseSetState(const std::string& json, SetState* out, std::string* error);
    std::string SerializeSetState(const SetState& state);
    // state.json 不存在时得到空状态并返回 true; 解析失败返回 false
    bool ReadSetState(const std::wstring& root, const std::string& setId, SetState* out, std::string* error);
    bool WriteSetState(const std::wstring& root, const std::string& setId, const SetState& state, std::string* error);

    // ---- outbox -----------------------------------------------------------------
    struct OutboxItem
    {
        uint64_t    seq         = 0;
        std::string name;
        std::string sha256;
        uint64_t    size        = 0;
        int64_t     baseVersion = 0;
        std::string cid;
        std::string charName;
        std::string at;
        DWORD       gamePid     = 0;
    };

    std::wstring OutboxDirectory(const std::wstring& root, const std::string& setId);
    // 按 seq 升序; 只列出 .json 能解析的项
    bool ListOutbox(const std::wstring& root, const std::string& setId, std::vector<OutboxItem>* out, std::string* error);
    bool ReadOutboxContent(const std::wstring& root, const std::string& setId, uint64_t seq, Bytes* out, std::string* error);

    // ---- 发布与放入 -------------------------------------------------------------
    struct PublishSource
    {
        std::string cid;       // 16 位大写十六进制
        std::string charName;  // UTF-8, 可空
        DWORD       gamePid = 0;
    };

    // 持互斥体: 写 current\<NAME>.DAT → outbox\<seq>.dat → outbox\<seq>.json → state.json（pending = true）。
    // 不校验内容, 调用方先 ValidateFile
    bool PublishFile(const std::wstring& root, const std::string& setId, const FileSpec& spec,
                     const uint8_t* data, size_t length, const PublishSource& source,
                     uint64_t* seq, std::string* error);

    enum class PrepareResult
    {
        Placed,        // 角色目录换成了这一套的最新版
        AlreadyLatest, // 角色目录里的已经是最新版
        NoLatest,      // 这一套还没有这个文件（或 current 与 state.json 对不上）, 没动角色目录
        LatestInvalid, // 这一套的最新版不合法, 没动角色目录
        Failed,        // 读状态或写角色目录失败, 没动角色目录
    };

    struct PrepareOutcome
    {
        PrepareResult result = PrepareResult::Failed;
        bool          hasBaseline = false; // false = 读不出角色目录现有文件, 这次登录不能发布
        std::string   baseline;          // 可比较哈希; 空 = 角色目录没有这个文件
        int64_t       version = 0;       // 放入的那一版在 state.json 里的 version
        bool          pending = false;   // 放入的那一版是否尚未上传
        std::string   reason;
        Bytes         baselineBytes;     // 基线对应的原始内容（ADDON 发布时要列出不同的记录键）
    };

    // 游戏读 <NAME>.DAT 之前调: 这一套有最新版且与角色目录现有文件不同就原子换上（目录不存在先建）。
    // 基线 = 放入后（或没放入时角色目录现有）文件的可比较哈希
    PrepareOutcome PrepareRead(const std::wstring& root, const std::string& setId, const FileSpec& spec,
                               const std::wstring& characterFile, DWORD lockTimeoutMs = 5000);

    enum class WrittenResult
    {
        Published,  // 与基线不同, 已发布
        Unchanged,  // 与基线相同, 丢弃
        NoBaseline, // 这次登录没有基线（模块晚于登录接入）, 不发布
        Invalid,    // 写回的文件不合法, 不发布
        ReadFailed, // 读不到写回的文件
        Failed,     // 发布失败
    };

    struct WrittenOutcome
    {
        WrittenResult result = WrittenResult::Failed;
        std::string   hash;     // 写回文件的可比较哈希（读到且合法时）
        uint64_t      seq = 0;  // Published 时的 outbox 序号
        std::string   reason;
        Bytes         content;  // 写回文件的原始内容（读到时）
    };

    // 游戏写完 <NAME>.DAT（.new 已改名）后调。hasBaseline = 这次登录读之前记下过基线;
    // baselineContent = 基线的原始内容（ADDON 用来认出只是窗口被重置, 可为空）
    WrittenOutcome HandleWritten(const std::wstring& root, const std::string& setId, const FileSpec& spec,
                                 const std::wstring& characterFile, bool hasBaseline, const std::string& baseline,
                                 const PublishSource& source, const Bytes* baselineContent = nullptr);

    // ---- 心跳 modules\<pid>.json ------------------------------------------------
    struct ModuleHeartbeat
    {
        std::string moduleVersion;
        bool        hooked = false;
        std::string lastCid;
        std::string lastName; // lastCid 那个号的角色名（UTF-8）, 查不到为空
        std::string lastError;
    };

    bool WriteHeartbeat(const std::wstring& root, DWORD pid, const ModuleHeartbeat& heartbeat, std::string* error);
}
