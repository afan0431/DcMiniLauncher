# DcMiniLauncher

基于 [AtmoOmen/FFXIVQuickLauncher](https://github.com/AtmoOmen/FFXIVQuickLauncher)（XIVLauncherCN · 橙月版，`CN` 分支）的 fork，面向国服。
在原版功能之上增加：

- **启动时选注入**：启动页直接勾选本次注入 Dalamud / Minion / 都注 / 都不注，不用进设置。
- **挂载 Minion**：游戏起来后按所选分组与账号自动挂载，时机排在 Dalamud 之后；与 MINIONAPP 同时运行不冲突。
- **游戏内超域旅行（不依赖 Dalamud）**：启动器向游戏注入一个极小的 native 模块，在游戏内完成超域旅行 / 超域返回 / 换登录大区，客户端全程不重启。
  启动器的超域传送页面和本机 HTTP 接口都能触发。

原版的登录、超域传送等功能保持不变。

## 构建

需要 .NET 10 SDK；native 模块另需 VS Build Tools 的 C++ 工作负载。

```bash
dotnet build src/XIVLauncher/XIVLauncher.csproj -c Debug /p:SkipApkalluCaller=true
powershell -ExecutionPolicy Bypass -File src/XIVLauncher.MiniModule/build.ps1
```

`SkipApkalluCaller=true` 跳过需要 Rust 的 `ApkalluCaller`，代价是 WeGame 登录不可用，SDO 登录不受影响。

## 注意

- Minion 的密码按 MinionLauncher 的要求以**明文**存在启动器配置里。
- 使用第三方工具可能违反游戏服务条款，风险自负。

## 上游同步

每天由 GitHub Actions（`.github/workflows/sync-upstream.yml`）自动合并上游 `CN` 分支；
有冲突时会开一个 `upstream-sync` Issue，需要手工合并。

## 许可证

原版为 GPL-3.0；游戏内超域旅行参照了 [DCTraveler](https://github.com/Dalamud-DailyRoutines/DCTraveler)（AGPL-3.0）的实现，组合后按 AGPL-3.0 发布。
