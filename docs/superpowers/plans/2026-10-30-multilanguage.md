# 多语言（中文 / English / Español）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理器、提示助手、服务（通知与运行日志）、安装/卸载脚本与安装说明的全部用户可见文字支持中 / 英 / 西三种语言，在管理器设置中即时切换。

**Architecture:** 每个程序各有一组 .resx 资源（英文为中性资源，`zh`、`es` 为卫星资源），由 MSBuild 生成强类型类，通过设置生成类的静态 `Culture` 实现进程内即时切换。语言设置整机一份，存放在服务配置 `BleUnlock:Ui:Language`。服务结合设置与助手上报的用户 UI 语言确定生效语言，并通过助手管道下发。管理器与服务之间原先靠中文原文判断的两处，改为与语言无关的英文代码。

**Tech Stack:** .NET 10、WPF（WPF-UI、CommunityToolkit.Mvvm）、Serilog、xUnit、PowerShell 5.1。

**Spec:** `docs/superpowers/specs/2026-10-30-multilanguage-design.md`

## Global Constraints

- 语言取值：配置为 `system | zh | en | es`，缺省 `system`；非法值按 `system` 处理（不使整份配置校验失败）。
- 「跟随系统」判定：`zh*` → 中文（含繁体，显示简体）；`en*` → 英文；`es*` → 西班牙语；其他 → 英文。
- 资源中性语言为英文；卫星资源文化名为 `zh`、`es`。
- 凡用户可见的文字一律走资源；程序间传递的状态码和命令一律用英文，与语言无关。
- 服务显示名与描述固定为英文：`BleUnlock Proximity Unlock` / `Locks and unlocks Windows based on the Bluetooth signal of your phone.`
- 品牌名「BleUnlock」在三种语言中都不翻译。
- 代码注释用西班牙语，标识符用英语。
- 切换后：管理器界面立即刷新；服务与助手不重启，之后产生的通知与日志用新语言；已显示的横幅、对话框、锁屏提示保持原文。
- 日志文件时间戳格式不变；Serilog 消息模板在三种语言中的命名占位符必须完全一致。
- 测试不得依赖运行环境的系统语言。
- 每个任务的「提交」步骤须先得到用户同意（用户规则：只在用户要求时提交）。

**术语表（翻译必须一致）：**

| 中文 | English | Español |
|---|---|---|
| 管理器 | Manager | Gestor |
| 服务 | service | servicio |
| 提示助手 | helper | asistente |
| 锁屏 / 锁定 | lock | bloquear |
| 解锁 | unlock | desbloquear |
| 靠近 / 离开 | near / away | cerca / lejos |
| 阈值 | threshold | umbral |
| 配对 | pair | emparejar |
| 凭据 | credentials | credenciales |
| 托盘 | tray | bandeja |
| 跟随系统 | Use system language | Usar el idioma del sistema |
| 信号 | signal | señal |

## Review Focus

1. **测试之间互相污染语言**：资源类的 `Culture` 是进程级静态值；一个测试切换语言后，并行运行的其他测试会拿到错误语言的文字。期望：既有测试固定为中文，切换语言的测试串行运行并恢复原值（Task 2）。
2. **译文占位符与原文不一致**：西语或英文漏了 `{Count}`、多了 `{0}`，会导致运行时 `FormatException`，或日志属性丢失。期望：资源一致性测试拦截（Task 2）。
3. **助手上报的文化名为空、乱码或旧格式（`HELLO 1` 不带文化）**：期望按整机默认语言处理，不抛异常（Task 5）。
4. **开始菜单快捷方式名随安装语言变化**：用另一种系统语言卸载时会找不到旧快捷方式。期望：卸载时三种名称都尝试删除（Task 12）。
5. **配置文件中语言值大小写、空格或非法**（`"EN"`、`" es "`、`"fr"`）：期望前两者识别为对应语言，`"fr"` 按跟随系统处理，且不会导致整份配置被拒绝（Task 1、Task 3）。

---

### Task 1: 共享语言判定

**Files:**
- Create: `src/Shared/AppLanguage.cs`
- Modify: `src/BleUnlockService/BleUnlockService.csproj`、`src/BleUnlockHelper/BleUnlockHelper.csproj`、`src/BleUnlockManager.Core/BleUnlockManager.Core.csproj`：各加 `<Compile Include="..\Shared\AppLanguage.cs" Link="Shared\AppLanguage.cs" />`（与助手链接 `HelperProtocol.cs` 的方式相同）
- Test: `tests/BleUnlockService.Tests/AppLanguageTests.cs`

**Interfaces:**
- Produces（命名空间 `BleUnlock.Shared`）：
  - `public enum AppLanguage { Chinese, English, Spanish }`
  - `public static class AppLanguages`
    - `const string SystemSetting = "system"`
    - `bool TryParseCode(string? code, out AppLanguage language)`：大小写不敏感，去首尾空格，接受 `zh`、`en`、`es`
    - `string ToCode(AppLanguage language)`：返回 `"zh"`、`"en"`、`"es"`
    - `AppLanguage FromCulture(CultureInfo culture)`：按 `TwoLetterISOLanguageName` 判断
    - `AppLanguage Resolve(string? setting, CultureInfo systemCulture)`：设置是合法代码就用它，否则用 `FromCulture(systemCulture)`
    - `CultureInfo ToCulture(AppLanguage language)`：返回 `zh`、`en`、`es` 中性文化

- [ ] **Step 1: 写失败测试** `AppLanguageTests`：
  - `Resolve_uses_explicit_code`：`Resolve("es", new CultureInfo("zh-CN")) == Spanish`
  - `Resolve_trims_and_ignores_case`：`Resolve(" EN ", new CultureInfo("es-ES")) == English`
  - `Resolve_system_follows_culture`：`Resolve("system", ...)`，用例 `zh-CN`、`zh-TW`、`zh-HK` → Chinese；`en-GB` → English；`es-MX` → Spanish
  - `Resolve_unsupported_culture_falls_back_to_english`：`Resolve("system", new CultureInfo("fr-FR")) == English`；`Resolve(null, new CultureInfo("de-DE")) == English`
  - `Resolve_invalid_code_follows_system`：`Resolve("fr", new CultureInfo("es-ES")) == Spanish`
  - `ToCode_and_TryParseCode_roundtrip`：三种语言往返一致
- [ ] **Step 2: 运行确认失败**：`dotnet test tests/BleUnlockService.Tests --filter AppLanguageTests`，预期编译失败（类型不存在）
- [ ] **Step 3: 实现** `src/Shared/AppLanguage.cs`，并在三个 csproj 中链接
- [ ] **Step 4: 运行通过**：同上命令，预期全部 PASS；`dotnet build src/BleUnlockHelper` 和 `dotnet build src/BleUnlockManager.Core` 均为 0 警告 0 错误
- [ ] **Step 5: 提交**：`feat(i18n): 共享语言判定 AppLanguage`

### Task 2: 资源基础设施与一致性测试

**Files:**
- Create：每个项目各一组，其中中性资源为英文：
  - `src/BleUnlockService/Resources/ServiceStrings.resx`（加 `.zh.resx`、`.es.resx`）
  - `src/BleUnlockHelper/Resources/HelperStrings.resx`（同上）
  - `src/BleUnlockManager.Core/Resources/CoreStrings.resx`（同上）
  - `src/BleUnlockManager/Resources/UiStrings.resx`（同上）
- 各 csproj 为中性 .resx 加强类型生成元数据，生成 `public` 类 `<项目根命名空间>.Resources.<名称>`：

  ```xml
  <EmbeddedResource Update="Resources\ServiceStrings.resx"
                    StronglyTypedLanguage="CSharp"
                    StronglyTypedNamespace="BleUnlockService.Resources"
                    StronglyTypedClassName="ServiceStrings"
                    StronglyTypedFileName="$(IntermediateOutputPath)ServiceStrings.Designer.cs"
                    PublicClass="true" />
  ```

- Create: `tests/BleUnlockManager.Tests/Localization/ResourceParityTests.cs`
- Create: `tests/BleUnlockManager.Tests/Localization/NoHardcodedChineseTests.cs`
- Create: `tests/BleUnlockManager.Tests/TestCulture.cs`、`tests/BleUnlockService.Tests/TestCulture.cs`：`[ModuleInitializer]` 把本测试程序集可见的所有 `*Strings.Culture` 设为 `zh`，让既有的中文断言保持成立
- Create：两个测试项目各加一个 `[CollectionDefinition("Language", DisableParallelization = true)]`。凡切换语言的测试都放进这个集合，并在 `finally` 中恢复为 `zh`

**Interfaces:**
- Produces：`ServiceStrings`、`HelperStrings`、`CoreStrings`、`UiStrings` 四个生成类。每个类都有静态 `Culture` 属性和 `ResourceManager` 属性；每个资源键对应一个同名静态属性。

- [ ] **Step 1: 写 `ResourceParityTests`**。从测试目录向上找到 `BleUnlockMyPc.sln` 定位仓库根，枚举 `src/**/Resources/*.resx` 中的中性文件，对每个文件断言：
  - `zh`、`es` 文件存在，且键集合与中性文件完全相同
  - 每个键在三种语言中的占位符集合相同。用正则 `\{([A-Za-z_]\w*|\d+)(?:[,:][^}]*)?\}` 提取，忽略格式说明
  - 没有空值
- [ ] **Step 2: 写 `NoHardcodedChineseTests`**。扫描 `src/BleUnlockManager`、`src/BleUnlockManager.Core`、`src/BleUnlockService`、`src/BleUnlockHelper` 下的 `*.cs`、`*.xaml`（排除 `obj/`、`bin/`、`Resources/`）。先去掉注释（`//…`、`/* … */`、`<!-- … -->`），再断言 C# 字符串字面量与 XAML 属性值、元素文本中不含 `[一-鿿]`；失败时列出文件和行号。
  - **此测试在 Task 3～11 完成前预期失败**，仅作为最终验收门槛。执行时给它加 `Skip = "Task 13 启用"`，在 Task 13 去掉 Skip。
- [ ] **Step 3: 创建四组空资源**：每组加一个键 `LanguageProbe`，英文、中文、西语值分别为 `English`、`中文`、`Español`，便于验证生成与切换。加入 `TestCulture` 模块初始化器和 `Language` 集合。
- [ ] **Step 4: 写测试 `Culture_switch_changes_probe`**（放在 `Language` 集合）：把 `CoreStrings.Culture` 设为 `es` 后，`CoreStrings.LanguageProbe == "Español"`；设为 `en` 后为 `"English"`。
- [ ] **Step 5: 构建与测试**：`dotnet build BleUnlockMyPc.sln` 预期 0 警告 0 错误；`dotnet test` 两个测试项目预期全部 PASS（`NoHardcodedChineseTests` 为 Skipped）。
  - 确认 `bin/.../zh/`、`bin/.../es/` 下生成了卫星程序集。
- [ ] **Step 6: 提交**：`feat(i18n): 资源基础设施与一致性测试`

### Task 3: 服务语言设置与生效语言

**Files:**
- Modify: `src/BleUnlockService/Config/BleUnlockOptions.cs`：新增 `UiOptions { string Language { get; set; } = "system"; }`，并在 `BleUnlockOptions` 中加 `public UiOptions Ui { get; set; } = new();`
- Modify: `src/BleUnlockService/Config/OptionsGate.cs`：`OtherDiffers` 加入语言比较，比较前先用 `AppLanguages.TryParseCode` 归一化
- Modify: `src/BleUnlockService/appsettings.json`：在 `BleUnlock` 下加 `"Ui": { "Language": "system" }`
- Create: `src/BleUnlockService/Localization/ServiceLanguage.cs`
- Modify: `src/BleUnlockService/Program.cs`：注册单例；在建主机之前，先用配置中的值和 `CultureInfo.InstalledUICulture` 设置 `ServiceStrings.Culture`，让启动日志也用正确语言
- Test: `tests/BleUnlockService.Tests/ServiceLanguageTests.cs`（放在 `Language` 集合）

**Interfaces:**
- Consumes：`AppLanguages.Resolve`、`ToCulture`（Task 1）；`ServiceStrings.Culture`（Task 2）
- Produces：`public sealed class ServiceLanguage`
  - 构造函数：`ServiceLanguage(OptionsGate options, CultureInfo machineCulture)`；DI 注册时传入 `CultureInfo.InstalledUICulture`
  - `AppLanguage Current { get; }`
  - `event Action<AppLanguage>? Changed`：仅在生效语言真的变化时触发
  - `void SetUserCulture(string? cultureName)`：空值或无效值按 `machineCulture` 处理
  - 每次重新计算都会设置 `ServiceStrings.Culture = AppLanguages.ToCulture(Current)`
  - 订阅 `options.Changed`

- [ ] **Step 1: 写失败测试**：
  - `Uses_machine_culture_before_helper_reports`：设置为 `system`，machine 为 `es-ES` → Spanish
  - `Helper_culture_wins_in_system_mode`：`SetUserCulture("zh-CN")` → Chinese，并触发一次 `Changed`
  - `Explicit_setting_ignores_helper_culture`：设置为 `en`，`SetUserCulture("zh-CN")` → 仍为 English，且不触发 `Changed`
  - `Option_change_switches_language`：通过 `OptionsGate.Submit` 把语言改为 `es`，等防抖到期后 `Current == Spanish`，且 `ServiceStrings.Culture.Name == "es"`
  - `Invalid_helper_culture_is_ignored`：`SetUserCulture("x!")` 不抛异常，按 machine 语言处理
  - `Language_only_change_is_applied`：只改语言时 `OptionsGate` 也会产生 `OptionsChange`
- [ ] **Step 2: 运行确认失败**：`dotnet test tests/BleUnlockService.Tests --filter "ServiceLanguageTests"`
- [ ] **Step 3: 实现**上述文件
- [ ] **Step 4: 运行通过**，并跑全部服务测试
- [ ] **Step 5: 提交**：`feat(i18n): 服务语言设置与生效语言`

### Task 4: 配置重载结果改用状态文件（去掉对中文日志的依赖）

**Files:**
- Create: `src/BleUnlockService/Config/ReloadStatusFile.cs`
- Modify: `src/BleUnlockService/Config/OptionsGate.cs`：应用、拒绝、无变化时都写状态文件。服务启动初次加载不写。原有日志照常写（文字在 Task 6 本地化）
- Modify: `src/BleUnlockManager.Core/AppPaths.cs`：加 `ReloadStatusFile = Path.Combine(DataDir, "reload-status.json")`
- Modify: `src/BleUnlockManager.Core/Config/ConfigReloadWatcher.cs`：改为读状态文件；删除日志解析、中文标记常量和正则
- Modify: `src/BleUnlockManager/ViewModels/ConfigViewModel.cs:741,774`：改用新的检查点和等待方法
- Test: `tests/BleUnlockService.Tests/ReloadStatusFileTests.cs`；改写 `tests/BleUnlockManager.Tests` 中 `ConfigReloadWatcher` 的现有测试

**Interfaces:**
- Produces（服务）：`ReloadStatusFile.Write(string path, ReloadStatus status)`，先写临时文件再替换。
  - 记录类型：`record ReloadStatus(string Id, string Result, bool DeviceChanged, bool CredentialsChanged, string? Message)`
  - `Result` 取值为 `"applied"`、`"rejected"`、`"unchanged"`
  - `Id` 每次写入都是新的 `Guid.ToString("N")`
  - `Message` 是被拒绝的原因，已按当前语言生成
- Produces（管理端）：
  - `ConfigReloadWatcher.CaptureCheckpoint(string statusFile) -> ConfigReloadCheckpoint(string? Id)`
  - `ConfigReloadWatcher.WaitAsync(string statusFile, ConfigReloadCheckpoint checkpoint, TimeSpan timeout, CancellationToken ct = default) -> ConfigReloadOutcome`：轮询间隔沿用 200 ms；读到与检查点不同的 `Id` 时返回；`applied` 和 `unchanged` 都映射为 `Reloaded`，`rejected` 映射为 `Rejected`
  - `ConfigReloadOutcome`、`ConfigReloadStatus` 的形状不变

- [ ] **Step 1: 写失败测试**：
  - 服务端 `OptionsGate_writes_rejected_status_with_message`、`OptionsGate_writes_applied_status_with_flags`
  - 管理端 `WaitAsync_returns_on_new_id`、`WaitAsync_ignores_checkpoint_id`、`WaitAsync_times_out_when_file_missing`、`WaitAsync_tolerates_partial_or_corrupt_json`（损坏内容视为未就绪，继续轮询直至超时）
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行两个测试项目，全部通过**
- [ ] **Step 5: 提交**：`refactor(config): 重载结果改用状态文件，不再解析日志文字`

### Task 5: 助手协议、扫描状态码与助手本地化

**Files:**
- Modify: `src/BleUnlockService/Session/HelperProtocol.cs`（助手也链接这个文件）
- Modify: `src/BleUnlockService/Session/HelperPipeServer.cs`：解析新的 HELLO；`Connected` 事件签名改为 `Action<int, string?>`（会话 ID、文化名）；同步更新 `Worker` 等订阅者
- Modify: `src/BleUnlockService/Program.cs` 或 `Worker.cs`：助手连上时调用 `ServiceLanguage.SetUserCulture`，然后发送 `LANG`；`ServiceLanguage.Changed` 触发时，向已连接的助手发送 `LANG`
- Modify: `src/BleUnlockHelper/Program.cs`：发送 `BuildHello(sessionId, CultureInfo.CurrentUICulture.Name)`；收到 `Language` 命令后设置 `HelperStrings.Culture`
- Modify: `src/BleUnlockHelper/ToastPrompt.cs`：标题、正文、两个按钮改用 `HelperStrings`。XML 中插入的文字要做转义
- Modify：扫描状态改为代码
  - `src/BleUnlockService/Ble/IRssiSource.cs`、`BleScanner.cs`：`StatusChanged` 改为 `Action<ScanStatus>`，记录类型为 `record ScanStatus(string Code, string? Detail)`。代码取值：`running`、`noAdapter`、`radioOff`、`startFailed`、`watcherStopped`；`Detail` 是原始异常信息，可为空
  - `TelemetryHub.OnScanStatus(ScanStatus)`
  - `ManagementProtocol.SnapshotScan(string? Status, string? Detail, string? Code)`：`Status` 保留为服务语言的文字，`Code` 为新增字段
  - 管理端 `src/BleUnlockManager.Core/Management/ManagementFrames.cs` 的 `SnapshotScan` 加 `string? Code`
  - `TelemetryFormat.ClassifyScan(string? code)`：`null` 或空串 → Unknown；`"running"` → Running；其他 → Degraded。删除 `ScanRunningStatus`
  - 概览页的异常说明改由管理端按 `Code` 生成本地化文字（Task 9），后面附上 `Detail`
- Test: `tests/BleUnlockService.Tests/HelperProtocolTests.cs`（扩充）、`TelemetryHubTests`、`ManagementPipeServerTests`（扩充）；`tests/BleUnlockManager.Tests` 中 `ClassifyScan` 的测试

**Interfaces:**
- Produces：
  - `HelperProtocol.BuildHello(int sessionId, string? uiCulture)` → `"HELLO 1 zh-CN\n"`；`uiCulture` 为空时省略
  - `HelperProtocol.TryParseHello(string line, out int sessionId, out string? uiCulture)`：兼容 `"HELLO 1"`
  - `HelperProtocol.BuildLanguage(AppLanguage language)` → `"LANG es\n"`
  - `HelperCommand.Language(AppLanguage Language)`：可被 `TryParseCommand` 解析
  - `ScanStatus` 及上述代码取值

- [ ] **Step 1: 写失败测试**：
  - `Hello_roundtrip_with_culture`、`Hello_without_culture_parses`、`Hello_with_garbage_culture_parses_sessionId`
  - `Lang_roundtrip`、`Lang_unknown_code_rejected`
  - `Snapshot_contains_scan_code`
  - `ClassifyScan_by_code`：`"running"` → Running；`"radioOff"` → Degraded；`null` → Unknown
  - 服务端 `Helper_connect_sends_language`：模拟助手连接后，收到的第一条命令之后紧跟 `LANG <code>`
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**；在 `HelperStrings` 三份资源中加入锁屏提示的标题、正文、拒绝按钮、「{0} 分钟内不锁定」按钮的文字
- [ ] **Step 4: 两个测试项目全部通过**；`dotnet build src/BleUnlockHelper` 0 警告 0 错误
- [ ] **Step 5: 提交**：`feat(i18n): 助手语言下发、提示本地化、扫描状态码`

### Task 6: 服务日志与通知本地化

**Files:**
- Modify：`src/BleUnlockService` 下所有用户可见文字，共约 90 处 `Log*` 调用、`Log.Information` 和 `Log.Fatal`。还包括：
  - `Worker.cs` 中通知正文（例如「…，请修改 appsettings.json 后自动恢复」）
  - `WtsMessageNotifier.cs` 对话框模式的标题和正文
  - `OptionsValidator.cs`、`OptionsSecrets.cs` 的错误和警告文字（会写进日志和状态文件）
  - `HelperLauncher.cs` 等处的降级原因
  - `BleScanner.cs` 中的状态说明
- Modify: `src/BleUnlockService/Resources/ServiceStrings*.resx`

键名规范：`Log_<事件>`、`Notify_<事件>`、`Dialog_<事件>`、`Validation_<事件>`。日志调用改为 `log.LogInformation(ServiceStrings.Log_Summary, state, batch, count, average, since)` 这种形式，占位符名称保持原有命名，例如 `{State}`、`{Count}`。

- [ ] **Step 1: 写测试**（放在 `Language` 集合）：
  - `Summary_log_uses_current_language`：用捕获型 `ILogger`，分别把 `ServiceStrings.Culture` 设为 `en` 和 `es`，触发一次摘要，断言渲染后的消息分别包含英文、西语关键词，且结构化属性 `Count` 存在
  - `Validation_errors_follow_language`：`OptionsValidator.Validate` 对非法 IRK 返回的错误文字，在 `es` 下是西语
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 把 `src/BleUnlockService` 的全部中文文字迁移到资源**：中文值照搬原文，英文和西语按术语表翻译。若分析器 CA2254（日志模板非常量）报警，在服务 csproj 中用 `<NoWarn>` 抑制，并写明原因
- [ ] **Step 4: 运行通过**；用 `NoHardcodedChineseTests` 的扫描逻辑临时只扫 `src/BleUnlockService`，结果应为 0 处
- [ ] **Step 5: 提交**：`feat(i18n): 服务日志与通知本地化`

### Task 7: Manager.Core 本地化与管理器当前语言

**Files:**
- Create: `src/BleUnlockManager.Core/Localization/ManagerLanguage.cs`
- Modify：`src/BleUnlockManager.Core` 下所有用户可见文字，包括 `TelemetryFormat.cs`、`ConfigDiff.cs`、`ConfigValidator.cs`、`Bonds/BleBondClient.cs`、`Telemetry/TrayState.cs`、`ConfigSecrets.cs`、`SignalZone.cs`、`ConfigDocument.cs`、`TargetDevice.cs` 等
- Modify: `src/BleUnlockManager.Core/Config/BleUnlockConfig.cs`：加 `UiConfig { string Language = "system" }`。`ConfigDocument.Save` **不写**此节，保持原样
- Modify: `src/BleUnlockManager.Core/Config/ConfigDocument.cs`：新增 `ReadLanguage(string path) -> string`，读取失败返回 `"system"`；新增 `SaveLanguage(string path, string code)`，只改写 `BleUnlock:Ui:Language`，其余内容保持原样，同样原子替换
- Test：`tests/BleUnlockManager.Tests` 中的 `ManagerLanguageTests`、`ConfigDocumentLanguageTests`，以及 Core 各本地化点的西语抽查（放在 `Language` 集合）

**Interfaces:**
- Produces：`public static class ManagerLanguage`
  - `AppLanguage Current { get; }`
  - `event Action? Changed`
  - `void Set(AppLanguage language)`：设置 `CoreStrings.Culture` 并触发 `Changed`；值未变时不触发

- [ ] **Step 1: 写失败测试**：
  - `SaveLanguage_preserves_other_fields`：保存前后，`BleUnlock` 其余字段与未知字段逐字节一致，仅语言变化
  - `Save_does_not_touch_language`：已有 `"Language": "es"` 的文件，执行 `ConfigDocument.Save` 后仍为 `"es"`
  - `ReadLanguage_missing_file_returns_system`
  - `TelemetryFormat_in_spanish`：`ManagerLanguage.Set(Spanish)` 后，`TelemetryFormat.StateName("Unlocked")` 返回西语
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现，并迁移 Core 全部文字**
- [ ] **Step 4: 管理端测试全部通过**：既有中文断言靠模块初始化器固定为 `zh`
- [ ] **Step 5: 提交**：`feat(i18n): Manager.Core 本地化与当前语言`

### Task 8: 管理器即时切换基础设施与设置页语言选项

**Files:**
- Create: `src/BleUnlockManager/Localization/LocalizationSource.cs`
- Create: `src/BleUnlockManager/Localization/LocExtension.cs`
- Modify: `src/BleUnlockManager/App.xaml.cs`：创建窗口之前，执行 `ManagerLanguage.Set(AppLanguages.Resolve(ConfigDocument.ReadLanguage(AppPaths.ConfigFile), CultureInfo.CurrentUICulture))`
- Modify: `src/BleUnlockManager/ViewModels/SettingsViewModel.cs`、`Views/SettingsPage.xaml`：新增「语言」一节，用下拉框列出四项：跟随系统、中文、English、Español
  - 后三项用各自语言的原名，资源中三种语言的值相同
  - `AppPaths.ConfigFile` 不存在时，下拉框禁用，并显示「需先安装服务」的提示
  - 选择后执行 `ConfigDocument.SaveLanguage`，然后立即 `ManagerLanguage.Set(...)`
- Test: `tests/BleUnlockManager.Tests/Localization/LocalizationSourceTests.cs`（放在 `Language` 集合，不需要 WPF 窗口）

**Interfaces:**
- Consumes：`ManagerLanguage`（Task 7）、`UiStrings`（Task 2）
- Produces：
  - `LocalizationSource.Instance`：实现 `INotifyPropertyChanged`，索引器 `string this[string key]`。取值为 `UiStrings.ResourceManager.GetString(key, UiStrings.Culture)`，缺键时返回 `"!" + key + "!"`。订阅 `ManagerLanguage.Changed`，在其中设置 `UiStrings.Culture` 并引发 `"Item[]"` 变更
  - XAML 用法 `{loc:Loc Key}`：`LocExtension` 返回绑定到 `LocalizationSource.Instance` 的 `OneWay` 绑定，路径为 `[Key]`。XML 命名空间前缀为 `loc`
  - 代码侧约定：视图模型和服务订阅 `ManagerLanguage.Changed`，在其中重新通知自己的文字属性

- [ ] **Step 1: 写失败测试**：
  - `Indexer_follows_language`：`Set(Spanish)` 后，`Instance["LanguageProbe"] == "Español"`
  - `Changed_raises_item_indexer`：收到 `PropertyChanged`，属性名为 `"Item[]"`
  - `Missing_key_is_visible`：缺键时返回 `"!Nope!"`
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现，并完成设置页语言一节**：本节文字已走资源
- [ ] **Step 4: 测试通过**；启动管理器，在设置里切换为 English，确认设置页中已改用 `loc` 的文字立即变化
- [ ] **Step 5: 提交**：`feat(i18n): 管理器即时切换基础设施与语言设置`

### Task 9: 管理器本地化之一：主窗口、概览、托盘

**Files:**
- Modify：`MainWindow.xaml(.cs)`、`Views/OverviewPage.xaml(.cs)`、`ViewModels/OverviewViewModel.cs`、`Services/TelemetryStore.cs`、`Services/Tray/TrayHost.cs`、`Views/Tray/TrayFlyout.xaml(.cs)`、`Views/Controls/SignalChart.cs`、`Views/Controls/LockCycle.cs`、`Models/NavItem` 的标题来源、`App.xaml.cs` 中的错误提示，以及这些文件中的无障碍名称
- 扫描状态的异常说明按 `Snapshot.Scan.Code` 生成，键为 `Scan_<code>`
- 语言变化时：
  - `TelemetryStore` 调用 `Publish()`
  - `TrayHost` 调用 `Refresh(force: true)`，并重建右键菜单的文字
  - `OverviewViewModel` 刷新各项文字属性和健康行
  - 导航项标题重新取值

- [ ] **Step 1: 迁移文字**，以 `{loc:Loc …}` 和资源属性替换原有中文
- [ ] **Step 2: 构建**，0 警告 0 错误；管理端测试全部通过
- [ ] **Step 3: 手动验证**：设置中依次切到 English、Español、中文。主窗口、概览、托盘提示、右键菜单、速览都应立即切换，无残留其他语言
- [ ] **Step 4: 提交**：`feat(i18n): 主窗口、概览与托盘本地化`

### Task 10: 管理器本地化之二：配置页与配对流程

**Files:**
- Modify：`Views/ConfigPage.xaml(.cs)`、`ViewModels/ConfigViewModel.cs`、`Views/PairDeviceDialog.xaml(.cs)`、`Services/Ble/BlePairingService.cs`、`Services/Ble/*.cs` 中其他用户可见文字、`Services/ServiceControlService.cs` 中的错误文字
- 语言变化时，`ConfigViewModel` 重新生成校验提示和差异摘要等文字属性。已显示的横幅保持原文

- [ ] **Step 1: 迁移文字**
- [ ] **Step 2: 构建与测试通过**
- [ ] **Step 3: 手动验证**：在 English 下保存一个非法阈值，看到英文校验提示；保存合法配置，看到英文的「已应用」，确认 Task 4 的通道可用；在 Español 下打开配对对话框，所有文字为西语
- [ ] **Step 4: 提交**：`feat(i18n): 配置页与配对流程本地化`

### Task 11: 管理器本地化之三：日志页与设置页其余部分

**Files:**
- Modify：`Views/LogsPage.xaml(.cs)`、`ViewModels/LogsViewModel.cs`、`Views/SettingsPage.xaml(.cs)` 的其余部分、`ViewModels/SettingsViewModel.cs`、`Services/SingleInstanceGuard.cs` 等剩余文件
- 日志的级别解析和计数不得依赖消息文字（现状已按级别字段解析，保持即可）

- [ ] **Step 1: 迁移文字**
- [ ] **Step 2: 去掉 `NoHardcodedChineseTests` 的 Skip**，运行：
  - `dotnet test tests/BleUnlockManager.Tests --filter NoHardcodedChineseTests`，预期 PASS
  - 若仍有残留，按测试输出的文件和行号逐一迁移
- [ ] **Step 3: 两个测试项目全部通过**
- [ ] **Step 4: 提交**：`feat(i18n): 日志页与设置页本地化，启用无硬编码中文检查`

### Task 12: 安装/卸载脚本与安装说明三语化

**Files:**
- Create: `scripts/i18n.ps1`：被安装和卸载脚本以 dot-source 方式加载
  - 按 `(Get-UICulture).TwoLetterISOLanguageName` 选择 `zh`、`es` 或其他（其他按 `en`）
  - 定义 `$script:Messages` 哈希表，以及函数 `T([string]$Key, [object[]]$Args)`，返回格式化后的文字
- Modify: `scripts/install.ps1`、`scripts/uninstall.ps1`：所有 `Write-Host`、`Write-Warning`、`throw`、`Read-Host` 的提示文字改用 `T`
  - `sc.exe create … DisplayName=` 与 `sc.exe description` 改为 Global Constraints 中固定的英文
  - 开始菜单快捷方式名：中文 `BleUnlock 管理器`、英文 `BleUnlock Manager`、西语 `Gestor de BleUnlock`
  - 快捷方式描述和计划任务描述按语言生成
- 卸载：
  - 三种快捷方式名都尝试删除
  - 确认输入接受 `yes`、`y`、`是`、`sí`、`si`，不区分大小写
- 三个脚本文件都以 **UTF-8 BOM** 保存
- Create：
  - `docs/install/安装说明.txt`：从测试包现有版本迁入，按新的服务显示名更新
  - `docs/install/INSTALL.en.txt`
  - `docs/install/INSTALAR.es.txt`

- [ ] **Step 1: 实现**
- [ ] **Step 2: 语法检查**：对三个脚本执行 `[Management.Automation.Language.Parser]::ParseFile`，预期 0 个错误
- [ ] **Step 3: 在本机以管理员身份跑一遍「卸载（-KeepData）+ 安装」**，确认输出为中文；服务显示名为英文；快捷方式存在；服务运行并识别设备
- [ ] **Step 4: 用 `T` 打印每种语言的全部键**：模拟 `en` 和 `es`，临时覆盖语言判定结果，确认没有缺键
- [ ] **Step 5: 提交**：`feat(i18n): 安装/卸载脚本与安装说明三语化`

### Task 13: 端到端验收

**Files:** 无新增；只修复验收中发现的问题。

- [ ] **Step 1: 全量测试**：`dotnet test` 两个测试项目全部 PASS（含 `NoHardcodedChineseTests`、`ResourceParityTests`）
- [ ] **Step 2: 重新发布管理器，编译服务和助手的 Release 版，按 Task 12 的脚本重装**
- [ ] **Step 3: 对照设计文档第 7 节逐条验收**：
  1. 设置切到 English 和 Español：所有页面、对话框、配对、托盘提示、菜单、速览立即切换
  2. 不重启服务，人离开触发锁屏提示：提示为新语言；之后新写的日志为新语言；概览页 BLE 扫描状态与配置保存结果判断正确
  3. 「跟随系统」：把 Windows 显示语言改为英文并注销，确认显示英文。法语等其他语言由 Task 1 的单元测试覆盖，不做真机切换
  4. 窗口为 800×500 时，英文和西语下逐页截图检查截断；托盘速览同样检查。发现截断就改为换行或省略号加提示
- [ ] **Step 4: 更新测试包**：用新版本替换，三份安装说明一并放入，重新压缩
- [ ] **Step 5: 提交验收中的修复**：`fix(i18n): 验收修正`；请用户审阅西语译文
