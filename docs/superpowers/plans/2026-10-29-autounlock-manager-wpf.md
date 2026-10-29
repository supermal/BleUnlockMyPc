# AutoUnlockManager WPF 迁移 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把管理端从 WinUI 3 原地迁移到 WPF（程序集仍为 `AutoUnlockManager.exe`），按已确认的设计稿重做全部界面，并新增托盘速览面板、自绘右键菜单、动态悬停提示与批次进度显示。

**Architecture:** `src/AutoUnlockManager` 原地改为 WPF + WPF-UI 项目，主题资源取自 `src/BleUnlockManager`（已按 UiDesign.md 实现 Tokens/Light/Dark/Controls）。平台无关逻辑（遥测视图、信号分区、托盘状态与提示文案、设备匹配、日志级别计数）下沉到 `AutoUnlockManager.Core` 并用 xUnit 覆盖；界面层只做绑定与绘制。全应用只有一个 `ManagementClient`（服务端管道单客户端），由应用级 `TelemetryStore` 持有，概览页、侧栏速览、标题栏状态、托盘共享同一份数据。服务端遥测协议 v1 只增字段 `batch`（批次进度）。

**Tech Stack:** .NET 10（`net10.0-windows10.0.19041.0`，win-x64 自包含）、WPF、WPF-UI 4.2.0（+ WPF-UI.DependencyInjection 4.2.0）、H.NotifyIcon.Wpf 2.4.1、CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.Hosting 10.0.1、System.ServiceProcess.ServiceController 10.0.13、System.Drawing.Common 10.0.13（托盘图标绘制）、xUnit。

**Spec:** 设计稿画布 https://claude.ai/artifact/FviE5nAiduF8DcVmL1FXWa（10 块画板：概览/日志/设置/配置、两种配对对话框、深色概览/日志、托盘图标与速览面板、托盘右键菜单与悬停提示）+ `UiDesign.md`（令牌、组件规格、§8 WPF 落地、§9 评审清单）。WinUI 版行为参照提交 `434e055`（`git show 434e055:src/AutoUnlockManager/<path>`）。

## Global Constraints

- 程序集名/RootNamespace 保持 `AutoUnlockManager`；发布目录保持 `src\AutoUnlockManager\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`（`scripts/install.ps1` 按此探测，不改脚本）
- manifest 保持 `requireAdministrator`；单实例（沿用 `SingleInstanceGuard`）；关闭主窗口 = 隐藏到托盘；只有托盘菜单「退出管理器」真正退出；退出不影响服务
- 管理通道只读；全应用同一时刻只持有一个 `ManagementClient`
- 协议 v1 只增不改：新增 `batch` 字段可空，旧管理端/旧服务互不报错
- 密码与 IRK 明文不写日志、不进遥测、不在界面显示 IRK
- 设计取值以 UiDesign.md 为准：岛 12 / 卡片 8 / 控件 4 圆角；同组卡片间距 4；主按钮方案 A（强调色），每视图最多一个；状态 = 图标形状 + 文字；全部颜色经 `DynamicResource` 令牌引用
- 已确认的偏离与新增（保留）：概览 dBm 数字 40px；侧栏宽 260；侧栏底部设备速览；导航「日志」警告计数；设置页「启动」「文件位置」两组；托盘单击速览面板、右键菜单含「查看日志」
- 界面文案逐字取自设计稿；按钮动词开头；不用全大写标签
- 代码注释：新写文件用西班牙语（全局约定）；从 WinUI 版原样迁移的文件保留原注释
- 构建入口：单项目 `dotnet build`（sln 含 C++ 项目，不可 CLI 整体构建）；每个任务结束时 `dotnet build` 0 警告 0 错误、两个测试项目全绿
- 测试不 `Thread.Sleep`；时间走 `TimeProvider`

## Review Focus

1. **服务未运行/未安装时启动管理端**：所有页面可打开，概览显示「无法连接到服务」横幅，托盘图标为「连接中/服务已停止」，不得抛异常 → Task 4 `TelemetryStoreTests` 断开态用例 + Task 3 `TrayStateTests`
2. **登录早期 shell 未就绪导致托盘创建失败**：需重试且失败时在 UI 线程显式 Dispose（WinUI 版 2026-10-17 FailFast 复盘）→ Task 9 保留重试 6 次 × 5 s 与 Dispose 兜底，Task 10 冒烟单列此项
3. **旧服务（无 `batch` 字段）+ 新管理端**：批次进度条隐藏、其余正常 → Task 2 `Parse_SnapshotWithoutBatch_BatchIsNull`
4. **配置文件 IRK 为全零占位或不在配对库**：设备名显示「未选择设备」/「未知设备」，不崩溃 → Task 3 `TargetDeviceTests`
5. **日志翻看时新行涌入**：视口不跳、计数累加、点「回到最新」后恢复跟随 → Task 3 `LogLevelCountsTests` 覆盖计数，Task 7 手测项

## File Structure

| 路径 | 职责 |
| --- | --- |
| `src/AutoUnlockService/Core/ProximityStateMachine.cs` | 新增 `BatchProgress? Progress`（`BatchProgressText` 改为由它派生） |
| `src/AutoUnlockService/Management/{TelemetryHub,ManagementProtocol}.cs` | 快照新增 `batch` |
| `src/AutoUnlockManager.Core/Management/ManagementFrames.cs`、`ManagementFrameParser.cs` | 解析 `batch` |
| `src/AutoUnlockManager.Core/Telemetry/SignalZone.cs` | 信号分区（靠近/保持/离开） |
| `src/AutoUnlockManager.Core/Telemetry/TrayState.cs` | 托盘状态判定 + 两行悬停提示 |
| `src/AutoUnlockManager.Core/Bonds/TargetDevice.cs` | 按配置 IRK 在配对库中找设备名 |
| `src/AutoUnlockManager.Core/Logs/LogLevelCounts.cs` | 各级别计数（筛选按钮上的数字） |
| `src/AutoUnlockManager/AutoUnlockManager.csproj`、`App.xaml(.cs)`、`app.manifest` | WPF 宿主、DI、单实例、主题 |
| `src/AutoUnlockManager/Themes/{Tokens,Light,Dark,Controls}.xaml` | 取自 BleUnlockManager，补设计稿所需样式 |
| `src/AutoUnlockManager/Helpers/{UiTheme,UiClip}.cs` | 取自 BleUnlockManager |
| `src/AutoUnlockManager/MainWindow.xaml(.cs)` | 外壳：标题栏、侧栏、主岛、对话框宿主 |
| `src/AutoUnlockManager/Services/TelemetryStore.cs` | 应用级遥测（唯一 ManagementClient） |
| `src/AutoUnlockManager/Services/TargetDeviceService.cs` | 当前目标设备名（配对库 + 配置） |
| `src/AutoUnlockManager/Services/Tray/{TrayHost,TrayIconRenderer}.cs`、`Views/Tray/{TrayFlyout,TrayMenu}.xaml` | 托盘 |
| `src/AutoUnlockManager/Views/Controls/{SignalChart,LockCycle}.cs/.xaml` | 曲线（含分区带）、锁定周期步进条 |
| `src/AutoUnlockManager/Views/{Overview,Config,Logs,Settings}Page.xaml(.cs)`、`Views/PairDeviceDialog.xaml(.cs)` | 页面 |
| `src/AutoUnlockManager/ViewModels/*.cs` | 迁移后的视图模型（去 WinUI 类型） |
| 原样保留 | `Services/{ServiceControlService,SingleInstanceGuard}.cs`、`Services/Ble/*` |

---

### Task 1: WPF 项目骨架与外壳

**Files:**
- Modify: `src/AutoUnlockManager/AutoUnlockManager.csproj`、`app.manifest`
- Delete: WinUI 专属文件 `App.xaml(.cs)`、`MainWindow.xaml(.cs)`、`Views/*`、`ViewModels/*`、`Helpers/{LogRowVisuals,SaveFilePicker}.cs`、`Services/{ThemeService,TrayIconHost,TrayStatusColors}.cs`（后续任务按 `434e055` 版本重写）
- Create: `App.xaml(.cs)`、`MainWindow.xaml(.cs)`、`Themes/*.xaml`（复制自 `src/BleUnlockManager/Themes`）、`Helpers/{UiTheme,UiClip}.cs`、`Views/{Overview,Config,Logs,Settings}Page.xaml(.cs)`（本任务为占位页：仅页面标题）
- Modify: `AutoUnlockMyPc.sln`（移除 BleUnlockManager 项目条目；目录本身不删，见 Task 10）

**Interfaces:**
- Produces: `MainWindow.Navigate(AppPage page)`；`enum AppPage { Overview, Config, Logs, Settings }`；`MainWindow.ShowMainWindow()`（还原+激活）；`MainWindow.TrayAvailable { get; set; }`（为 false 时关闭即退出）；DI 容器 `App.Services`（`IServiceProvider`），页面与视图模型均注册为单例

- [ ] **Step 1:** csproj 改为 `UseWPF=true`，去掉 `UseWinUI`/`WindowsPackageType`/`WindowsAppSDKSelfContained`/`EnableMsixTooling` 与 WindowsAppSDK、BuildTools、H.NotifyIcon.WinUI 包；保留 TFM、`RuntimeIdentifier=win-x64`、`ApplicationIcon`、`ApplicationManifest`、Core 引用、`Assets\AppIcon.ico` Content；加入 Tech Stack 所列包。manifest 保留 `requireAdministrator`，DPI 改为 `PerMonitorV2`
- [ ] **Step 2:** 外壳按设计稿：`ui:FluentWindow`（`ExtendsContentIntoTitleBar`，背景 `Ui.Bg.Canvas`，MinWidth 800 / MinHeight 500，默认 1280×800）；标题栏 48（Logo 24 + 「AutoUnlock 管理器」+ 右侧服务状态徽章占位）；侧栏 260（概览/配置/日志，底部设备速览占位 + 设置）；主岛 `Ui.Island`，`Margin=0,0,8,8`。去掉模板里的搜索框与收起按钮
- [ ] **Step 3:** `App.OnStartup`：`SingleInstanceGuard.TryCreate(唤醒回调)` 失败即 `Shutdown()`；建 Host（DI）→ `UiTheme.Initialize()` → 显示主窗口。关闭按钮：`TrayAvailable` 为 true 时 `e.Cancel=true` 并 `Hide()`
- [ ] **Step 4:** 构建与启动验证

Run: `dotnet build src\AutoUnlockManager\AutoUnlockManager.csproj -c Debug`
Expected: 0 警告 0 错误
Run: `dotnet publish src\AutoUnlockManager\AutoUnlockManager.csproj -c Release -r win-x64 --self-contained true`，以管理员启动 publish 目录下的 exe，再启动第二次
Expected: 窗口出现、四个导航可切换；第二次启动不出新窗口而是激活第一个
- [ ] **Step 5:** 两个测试项目全绿后提交 `feat(manager): WPF 骨架与岛屿式外壳（替换 WinUI 宿主）`

### Task 2: 遥测协议新增批次进度 `batch`

**Files:**
- Modify: `src/AutoUnlockService/Core/ProximityStateMachine.cs`、`Management/TelemetryHub.cs`、`Management/ManagementProtocol.cs`、`Worker.cs`
- Modify: `src/AutoUnlockManager.Core/Management/ManagementFrames.cs`、`ManagementFrameParser.cs`
- Test: `tests/AutoUnlockService.Tests/ProximityStateMachineLockTests.cs`、`Management/TelemetryHubTests.cs`、`tests/AutoUnlockManager.Tests/ManagementFrameParserTests.cs`

**Interfaces:**
- Produces（服务）：`public enum BatchKind { Leave, LockConfirm, Unlock }`；`public sealed record BatchProgress(BatchKind Kind, int Count, int Target)`；`ProximityStateMachine.Progress : BatchProgress?`；`TelemetryHub.OnBatchProgress(BatchProgress? progress)`
- Produces（Core）：`public sealed record SnapshotBatch(string Kind, int N, int Of)`；`SnapshotFrame` 末尾新增可空参数 `SnapshotBatch? Batch`
- 线格式：`"batch":{"kind":"leave"|"lockConfirm"|"unlock","n":3,"of":5}`，无批次时省略或 `null`

- [ ] **Step 1: 写失败测试**
  - `Progress_DuringLeaveBatch_ReportsCountAndTarget`：Unlocked、宽限期过后，喂 1 条触发读数（-85）+ 3 条 -85 → `Progress == new BatchProgress(BatchKind.Leave, 3, 5)`（LockPromptSampleCount=5）
  - `Progress_NoActiveBatch_IsNull`
  - `BatchProgressText_DerivesFromProgress`：同上场景文本仍为 `离开确认 3/5`
  - `BuildSnapshot_IncludesBatch`：`OnBatchProgress(new(BatchKind.LockConfirm, 2, 10))` 后快照 JSON 含 `"batch":{"kind":"lockConfirm","n":2,"of":10}`
  - `Parse_SnapshotWithBatch_ReadsBatch`、`Parse_SnapshotWithoutBatch_BatchIsNull`
- [ ] **Step 2:** 运行确认失败：`dotnet test tests\AutoUnlockService.Tests` 与 `dotnet test tests\AutoUnlockManager.Tests`
- [ ] **Step 3:** 实现。`Worker` 在每次 `Evaluate` 之后调用 `telemetry.OnBatchProgress(_machine.Progress)`（值未变不触发 `Changed`）
- [ ] **Step 4:** 两个测试项目全绿
- [ ] **Step 5:** 提交 `feat(protocol): 遥测快照新增批次进度 batch（v1 只增字段）`

### Task 3: Core 展示逻辑（分区、托盘状态与提示、目标设备、日志计数）

**Files:**
- Create: `src/AutoUnlockManager.Core/Telemetry/SignalZone.cs`、`Telemetry/TrayState.cs`、`Bonds/TargetDevice.cs`、`Logs/LogLevelCounts.cs`
- Test: `tests/AutoUnlockManager.Tests/{SignalZoneTests,TrayStateTests,TargetDeviceTests,LogLevelCountsTests}.cs`

**Interfaces:**
- `public enum SignalZone { Near, Hold, Away }`；`SignalZones.Classify(double dbm, double lockDbm, double unlockDbm) -> SignalZone`（`>= unlock` 为 Near，`<= lock` 为 Away，否则 Hold）；`SignalZones.DisplayName(SignalZone) -> "靠近区"|"保持区"|"离开区"`
- `public enum TrayState { Running, Leaving, Suspended, Stopped, Connecting }`；`TrayStates.Resolve(bool? serviceRunning, bool connected, string? stateName, SnapshotBatch? batch) -> TrayState`
- `public sealed record TrayTooltip(string Line1, string Line2)`；`TrayStates.Tooltip(TrayState, string deviceName, double? dbm, double? lockDbm, string? lastMatchText, SnapshotBatch? batch) -> TrayTooltip`；`ToString()` 为两行拼接，总长 ≤ 127
- `TargetDevice.Resolve(IReadOnlyList<BleBondDevice> bonds, string? irkPlain) -> string`
- `public sealed record LogLevelCounts(int Debug, int Info, int Warning, int Error)`；`LogLevelCounts.From(IEnumerable<LogEntry>)`；`Add(LogEntry) -> LogLevelCounts`

- [ ] **Step 1: 写失败测试**（期望值即设计稿文案）
  - `Classify(-51,-80,-45)==Hold`、`Classify(-45,…)==Near`、`Classify(-80,…)==Away`
  - `Resolve(false,…)==Stopped`；`Resolve(null,false,…)==Connecting`；`Resolve(true,false,…)==Connecting`；`Resolve(true,true,"Suspended",null)==Suspended`；`Resolve(true,true,"Unlocked",batch leave 3/5)==Leaving`；`Resolve(true,true,"PendingLock",null)==Leaving`；`Resolve(true,true,"Unlocked",null)==Running`
  - Tooltip：Running → `("AutoUnlock：已解锁", "Apple Watch -51 dBm，最近匹配 0.4 秒前")`；Leaving(3/5, -84, lock -80) → `("AutoUnlock：离开确认 3/5", "Apple Watch -84 dBm，低于锁定阈值 -80")`；Stopped → `("AutoUnlock：服务已停止", "右键选择「启动服务」即可恢复")`；Suspended → 第一行 `AutoUnlock：自动解锁已暂停，请检查密码`；Connecting → `AutoUnlock：正在连接服务…`；200 字设备名时 `ToString().Length <= 127`
  - TargetDevice：IRK 命中返回其 Name；Name 为空返回地址；`ConfigValidator.NoDeviceIrk` 返回 `未选择设备`；未命中返回 `未知设备`；IRK 比较忽略大小写
  - LogLevelCounts：混合 4 级条目计数正确；`Add` 单条累加
- [ ] **Step 2:** `dotnet test tests\AutoUnlockManager.Tests` 确认失败
- [ ] **Step 3:** 实现
- [ ] **Step 4:** 全绿
- [ ] **Step 5:** 提交 `feat(core): 信号分区、托盘状态与提示文案、目标设备名、日志级别计数`

### Task 4: 应用级遥测与外壳实时元素

**Files:**
- Create: `src/AutoUnlockManager/Services/TelemetryStore.cs`、`Services/TargetDeviceService.cs`
- Modify: `MainWindow.xaml(.cs)`（标题栏状态徽章、侧栏设备速览；导航「日志」警告计数在 Task 7 接入）

**Interfaces:**
- Consumes: `ManagementClient`、`SampleHistory`、`TelemetryFormat`、Task 3 全部、`ServiceControlService.GetStatusAsync()`
- Produces: `TelemetryStore : ObservableObject`（单例，属性仅 UI 线程写）：`Connected`、`Snapshot : SnapshotFrame?`、`History : SampleHistory`、`CurrentDbm : double?`、`Zone : SignalZone?`、`StateText`、`StateSince`、`Batch : SnapshotBatch?`、`ServiceRunning : bool?`（每 3 s 轮询 SCM）、`TrayState`、`Tooltip : TrayTooltip`；事件 `HistoryReset`、`SamplesAppended`
- Produces: `TargetDeviceService.DeviceName : string`、`RefreshAsync()`（启动时、配置保存后、配对/解除配对后调用）

- [ ] **Step 1:** `TelemetryStore` 在 App 启动时创建并 `Start()`，客户端事件经 `Dispatcher.BeginInvoke` 投递；gen 变化清空 `History` 并引发 `HistoryReset`（沿用 WinUI 版 `OverviewViewModel` 的处理，见 `434e055`）；退出时 `Dispose`
- [ ] **Step 2:** 标题栏徽章（运行中 ✓ / 已停止 ✕ / 连接中 ○，文案「服务运行中」「服务已停止」「正在连接服务」）、侧栏速览（设备名、`CurrentDbm`、`StateText`）绑定
- [ ] **Step 3:** 构建；服务运行时启动管理端，速览 1 s 内出现实时 dBm；停止服务后徽章变「服务已停止」
- [ ] **Step 4:** 提交 `feat(manager): 应用级遥测存储，标题栏状态与侧栏设备速览`

### Task 5: 概览页

**Files:**
- Create: `ViewModels/OverviewViewModel.cs`（迁移）、`ViewModels/BannerSeverity.cs`（`enum BannerSeverity { Informational, Success, Warning, Error }`，替代 `InfoBarSeverity`，映射到 `ui:InfoBar.Severity`）、`Views/Controls/SignalChart.cs`、`Views/Controls/LockCycle.xaml(.cs)`、`Views/OverviewPage.xaml(.cs)`

**Interfaces:**
- Consumes: `TelemetryStore`、`TargetDeviceService`、`ServiceControlService`、`SampleCsv`、`SignalZones`
- Produces: `SignalChart`（`FrameworkElement`，`OnRender` 绘制）：依赖属性 `Points : IReadOnlyList<SamplePoint>`、`LockDbm`、`UnlockDbm`、`WindowSeconds`（默认 300；托盘为 60）、`NowMs`、`ShowAxisLabels`（托盘为 false）；Y 轴固定 -100…-30；画靠近/离开分区带、虚线阈值、折线、末点圆点；颜色取 `Ui.Status.*` 与 `Ui.Accent` 资源，主题切换时重绘
- Produces: `LockCycle`：依赖属性 `StateName : string?`、`Batch : SnapshotBatch?`；五步「已解锁 → 待锁定 → 已锁定 · 等待离开 → 已锁定 · 监控中 → 解锁确认中」，当前步实心强调色；有批次时在当前步下显示 5 段进度条与「离开确认 3/5」

- [ ] **Step 1:** 视图模型：去掉 `ManagementClient` 自持，改读 `TelemetryStore`；服务控制（启动/停止/重启）前用 `ContentDialog` 确认，结果用 `ui:InfoBar` 横幅；断开时显示 Warning 横幅「无法连接到服务」「服务未运行或未安装；正在自动重试（每 2 秒）。」
- [ ] **Step 2:** 页面按设计稿：标题行「概览」+「导出样本 CSV」（`Microsoft.Win32.SaveFileDialog`）；信号卡（dBm 40px、分区徽章、5/10 秒均值、状态徽章 + 持续时长、曲线、锁定周期）；「服务与健康」键值卡（服务 / BLE 扫描 / 提示助手 / 凭据提供程序（未连接时副文本「锁屏时才会连接」）/ 最近匹配）+「停止服务」「重启服务」；「目标设备」卡 +「更换设备」跳配置页
- [ ] **Step 3:** 构建；实机对照设计稿浅/深两种主题检查；断开服务验证横幅
- [ ] **Step 4:** 提交 `feat(manager): 概览页（分区曲线、锁定周期、健康与目标设备）`

### Task 6: 配置页与配对对话框

**Files:**
- Create: `ViewModels/ConfigViewModel.cs`（迁移 `434e055` 版，含配对/解除配对/采样间隔）、`Views/ConfigPage.xaml(.cs)`、`Views/PairDeviceDialog.xaml(.cs)`（迁移为 `ui:ContentDialog`，宽 640）

**Interfaces:**
- Consumes: Core `Config/*`、`BleBondClient`、`BondDiff`、`Services/Ble/*`、`TargetDeviceService.RefreshAsync()`、`BannerSeverity`
- Produces: `ConfigViewModel.PendingChanges : IReadOnlyList<ConfigChange>`（由当前表单构造配置后 `ConfigDiff.Compute(loaded, form)`，字段变化即重算）；`HasPendingChanges`；`PendingSummary`（单项：「锁屏确认个数 10 → 12」；多项：「锁屏确认个数等 N 项」）；`IsFieldModified(string field)` 供卡片显示「已修改」

- [ ] **Step 1:** 视图模型迁移：`InfoBarSeverity` → `BannerSeverity`；`DispatcherQueue` → `Dispatcher`；保存/差异确认/热重载等待/DPAPI/解除配对写全零占位的流程不变
- [ ] **Step 2:** 页面按设计稿四组：解锁设备（设备下拉 + 「已配对 N 台设备」徽章 + 刷新列表 / 解除配对 / 配对新设备… + 可展开「IRK 字节序」「设备地址（可选）」）；登录凭据（用户名、密码 + 眼睛）；接近判定（信号阈值色带卡 + 六张数字卡，单位后缀）；锁屏提示（提示方式、两个暂停时长）。底部保存栏固定在岛底：有未保存更改时显示「N 项未保存」徽章、摘要、「从磁盘重新加载」「放弃更改」「保存更改」（主按钮）；无更改时主按钮禁用
- [ ] **Step 3:** 配对对话框：两张单选模式卡；手机模式 4 步编号（完成 ✓ / 当前强调色 / 未到灰色）+ 「正在广播，等待设备连接…」与已等待计时；扫描模式设备列表（名称 / 地址等宽 / 信号）+「配对所选设备」主按钮；PIN 输入与确认沿用原逻辑；页脚说明「配对确认与 PIN 会在本窗口弹出，不会打开 Windows 设置。」
- [ ] **Step 4:** 构建；手测：改一个数字 → 保存栏出现摘要；放弃更改恢复；保存后确认横幅与热重载结果；打开配对对话框两种模式切换
- [ ] **Step 5:** 提交 `feat(manager): 配置页（卡片分组、阈值色带、未保存更改栏）与配对对话框`

### Task 7: 日志页

**Files:**
- Create: `ViewModels/LogsViewModel.cs`（迁移）、`Views/LogsPage.xaml(.cs)`

**Interfaces:**
- Consumes: Core `Logs/*`、`LogLevelCounts`
- Produces: `LogsViewModel.Counts : LogLevelCounts`（当前文件全部条目，供筛选按钮与侧栏「日志」警告计数）；`IsFollowing`；`PendingNewLines : int`（翻看期间新增并通过过滤的行数）；`JumpToLatestCommand`

- [ ] **Step 1:** 视图模型迁移（250 ms 轮询、20 万行窗口、过滤、换日切换不变）；导出改用 `SaveFileDialog`
- [ ] **Step 2:** 页面：标题行（文件下拉等宽字体、刷新、打开文件夹、「导出当前视图」）；过滤带（调试/信息/警告/错误 带计数的切换按钮、关键字框、「自动跟随」开关）；虚拟化 `ListView`（时间等宽 / 级别 / 消息，WRN、ERR 整行状态底色）；状态栏「显示 X 行，共 Y 行」「最多保留 20 万行」+ 跟随状态
- [ ] **Step 3:** 跟随逻辑：`ScrollChanged` 判断是否在底部（容差 2 px）；在底部且开关开 → 新行后滚到底；不在底部 → 保持位置、`PendingNewLines` 累加、右下角浮出「有 N 条新日志，回到最新」，点击滚到底并清零；状态栏切换「正在跟随最新」/「正在翻看历史，新日志在下方追加」
- [ ] **Step 4:** 侧栏导航「日志」右侧绑定 `Counts.Warning`（为 0 时隐藏，警告色）
- [ ] **Step 5:** 构建；手测翻看时视口不跳、计数增长、回到最新恢复
- [ ] **Step 6:** 提交 `feat(manager): 日志页（级别计数、叠加式跟随与回到最新）`

### Task 8: 设置页

**Files:**
- Create: `ViewModels/SettingsViewModel.cs`、`Views/SettingsPage.xaml(.cs)`、`Services/AppSettingsStore.cs`

**Interfaces:**
- Produces: `AppSettingsStore.Load()/Save(AppSettings)`，文件 `AppPaths.ManagerSettingsFile`，字段 `Theme : "System"|"Light"|"Dark"`（与 WinUI 版 `ThemeService` 的 JSON 兼容，读旧文件不丢设置）；`AutoStartProbe.IsRegistered() -> bool?`（`schtasks /Query /TN AutoUnlockManager` 退出码 0 为 true，异常为 null）

- [ ] **Step 1:** 外观：三张主题单选卡（迷你预览），选择即 `UiTheme.SetMode` 并保存；启动卡：已注册显示 ✓「已启用」，未注册显示 ○「未注册（请运行安装脚本）」；文件位置三张卡（配置文件 / 服务日志 / 安装目录，路径等宽，「打开文件夹」用 `explorer.exe /select,` 或打开目录）
- [ ] **Step 2:** 构建；切换主题后重启管理端保持选择
- [ ] **Step 3:** 提交 `feat(manager): 设置页（主题卡片、自启状态、文件位置）`

### Task 9: 托盘

**Files:**
- Create: `Services/Tray/TrayHost.cs`、`Services/Tray/TrayIconRenderer.cs`、`Views/Tray/TrayFlyout.xaml(.cs)`、`Views/Tray/TrayMenu.xaml`

**Interfaces:**
- Consumes: `TelemetryStore`（`TrayState`、`Tooltip`、`History`、`CurrentDbm`、`Zone`）、`TargetDeviceService`、`ServiceControlService`、`MainWindow.ShowMainWindow()`、`MainWindow.Navigate(AppPage.Logs)`
- Produces: `TrayIconRenderer.Render(TrayState state, bool lightTaskbar, int size) -> System.Drawing.Icon`（缓存 5×2 种）；`TrayHost : IDisposable`，构造失败抛出

- [ ] **Step 1:** 图标：线条「锁 + 信号」主体（浅色任务栏用 #1A1A1A、深色用 #FFFFFF，读 `HKCU\...\Themes\Personalize\SystemUsesLightTheme`，`SystemEvents.UserPreferenceChanged` 时重绘）；右下徽记（16px 坐标系：圆心 12.5,12.5，外圈挖空 r=4）——Running 实心圆（#0F7B0F / #6CCB5F）、Leaving 三角（#9D5D00 / #FCE100）、Suspended 三角（#C42B1C / #FF99A4）、Stopped 圆+白叉（同 Suspended 色）、Connecting 空心圆（#7A7A7A / #9E9EA6）；按 DPI 渲染 16/20/24/32
- [ ] **Step 2:** 交互：`TaskbarIcon`（H.NotifyIcon.Wpf）`PopupActivation=LeftClick` → `TrayFlyout`；`DoubleClickCommand` → 显示主窗口；右键 → `TrayMenu`（WPF `ContextMenu`，Win11 样式：状态区（徽记 + 「服务运行中」+ 状态 + 设备 dBm）/ 打开管理器（加粗）/ 查看日志 / 启动服务 / 停止服务 / 重启服务（不可用项置灰）/ 退出管理器 + 副文本「服务会继续在后台运行」）；`ToolTipText = Tooltip.ToString()`，随 `TelemetryStore` 变化更新（节流 1 s）
- [ ] **Step 3:** 速览面板 360 宽：标题行（Logo、AutoUnlock、服务徽章、打开管理器图标按钮）、设备行（设备名 + 最近匹配）、dBm 28px + 分区徽章 + 5 秒均值、`SignalChart`（WindowSeconds=60、ShowAxisLabels=false、高 64）、状态行、页脚「重启服务」「打开管理器」（主按钮）；点击面板外关闭
- [ ] **Step 4:** 健壮性：创建失败时在 UI 线程 `Dispose` 并以 5 s 间隔重试最多 6 次（沿用 WinUI 版 App 的重试与注释要点）；全部失败则 `TrayAvailable=false`（关闭窗口即退出）
- [ ] **Step 5:** 构建；手测五种状态图标（停/启服务、远离设备）、单击/双击/右键、提示两行文案
- [ ] **Step 6:** 提交 `feat(manager): 托盘（状态图标、速览面板、右键菜单、动态提示）`

### Task 10: 收尾与交付

**Files:**
- Modify: `README.md`（管理端章节：WPF、托盘交互）、`docs/superpowers/plans/2026-10-16-autounlock-manager.md` 与 `.superpowers/sdd/2026-10-16-autounlock-manager/progress.md`（记录迁移）
- Delete（需用户确认后）：`src/BleUnlockManager/`

- [ ] **Step 1:** 全仓搜索 `Microsoft.UI`、`WinUI`、`InfoBarSeverity`、`DispatcherQueue`，Manager 项目内应无残留
- [ ] **Step 2:** 全量验证

Run: `dotnet build src\AutoUnlockService\AutoUnlockService.csproj -c Release`、`dotnet build src\AutoUnlockManager\AutoUnlockManager.csproj -c Release`、`dotnet test tests\AutoUnlockService.Tests`、`dotnet test tests\AutoUnlockManager.Tests`、`dotnet publish src\AutoUnlockManager\AutoUnlockManager.csproj -c Release -r win-x64 --self-contained true`
Expected: 构建 0 警告 0 错误；测试全绿（记录数量）；publish 目录含 `AutoUnlockManager.exe`
- [ ] **Step 3:** 按 UiDesign.md §9 评审清单逐项自查；整理一份交给用户的冒烟清单（含 Review Focus 第 2 项：注销后重新登录，托盘出现）
- [ ] **Step 4:** 向用户确认后删除 `src/BleUnlockManager/`
- [ ] **Step 5:** 提交 `docs: WPF 迁移完成记录` 并请求全分支审查

---

## 执行记录与交付（2026-10-29）

- 分支 `feat/wpf-manager`（自检查点 `434e055` 切出），提交 `890ef19`…（Task 1–10），执行台账见 `.superpowers/sdd/2026-10-29-autounlock-manager-wpf/progress.md`（含全部 Ruling）。
- 全量验证：服务 / 管理端 Release 构建 0 警告 0 错误；测试 服务 377 + 管理端 278 = **655 全绿**；自包含发布 271 文件 / 174 MB（WinUI 版为 532 文件 / 232.9 MB），`scripts/install.ps1` 无需改动。
- 本机已验证（非提升 + 假遥测管道）：外壳与导航、单实例唤醒、概览分区曲线/锁定周期/批次进度（深浅两色）、配置页渲染（只读态）、配对对话框两种模式、日志页真实日志/警告计数/翻看状态、设置页主题切换与持久化、托盘 10 枚状态图标、速览面板、右键菜单、关闭隐藏到托盘。
- 已知：`BleBondClientTests` 存在既有的间歇失败（约 1/7，Pipe is broken），非本次引入。

### 冒烟清单（重装后以管理员环境执行）

1. 管理员运行 `.\scripts\install.ps1` → 管理端随计划任务启动，托盘出现锁形图标（绿色圆点 = 服务运行中）。
2. **注销后重新登录**：托盘图标仍出现（验证登录早期 shell 未就绪时的重试）。
3. 概览：dBm 实时跳动，曲线分区正确；把设备拿远 → 状态徽章变琥珀色「正在确认是否离开」、锁定周期出现「离开确认 n/5」；拿回 → 恢复。
4. 托盘：单击出现速览面板（60 秒曲线在动）；双击打开主窗口；悬停两行提示；右键菜单「停止服务」→ 图标变红色圆叉、提示「服务已停止」→「启动服务」恢复。
5. 配置：改「锁屏确认个数」→ 底部出现「1 项未保存：锁屏确认个数 10 → 12」与卡片「已修改」；放弃更改恢复；再改并保存 → 确认对话框 → 「配置已重载」横幅。
6. 配置：拖动阈值数值时色带实时变化；「配对新设备…」两种模式可切换、关闭；「解除配对」确认文案正确（可不实际执行）。
7. 日志：向上翻看时等待新行 → 视口不跳、右下出现「有 N 条新日志，回到最新」，点击回到底部并恢复「正在跟随最新」；侧栏「日志」警告数与「警告」筛选计数一致。
8. 设置：切换浅色/深色立即生效，重启管理端后保持；「登录时自动启动」显示「已启用」。
9. 关闭主窗口 → 隐藏到托盘；托盘「退出管理器」→ 进程退出，服务仍在运行。
