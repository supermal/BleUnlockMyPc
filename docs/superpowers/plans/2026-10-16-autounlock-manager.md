# AutoUnlockManager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 AutoUnlockService 提供一个 Windows 桌面管理端（WinUI 3 中文界面）：实时查看 dBm 与状态、编辑配置（热重载生效）、查看服务日志、托盘常驻；并给服务新增一条只读遥测命名管道作为数据来源。

**Architecture:** 服务端新增 `\\.\pipe\AutoUnlockManagement`（push-only，SYSTEM+Administrators，单客户端，新连接顶替旧连接）：旁路订阅现有 BLE 采样、状态机、助手/凭据管道连接态，250ms 推送 JSON 快照（连接时补发 5 分钟回溯）。管理端（unpackaged 自包含 WinUI 3，管理员运行）只读消费：曲线/状态/健康检查在概览页；配置经「appsettings.json 原子替换 + 服务热重载 + 日志确认」；服务控制经 SCM；日志经文件增量跟随（Serilog `shared: true`）。托盘常驻，登录经计划任务免 UAC 自启。

**Tech Stack:** .NET 10（`net10.0-windows10.0.19041.0`）、WinUI 3 / Windows App SDK **2.5.1**、H.NotifyIcon.WinUI **2.4.1**、Microsoft.Windows.SDK.BuildTools 10.0.28000.2705、CommunityToolkit.Mvvm、Serilog 文本日志（只读）、xUnit。以上版本已通过最小工程构建 + 自包含发布 + 启动验证（见 Task 1）。

**Spec:** `docs/superpowers/specs/2026-10-16-autounlock-manager-design.md`

## Global Constraints

- 管理通道**只读**：管理端不得向服务下达任何指令；配置=文件编辑 + 服务既有热重载；服务控制=SCM；管理端崩溃/重连不得影响服务
- 遥测为纯旁路订阅：服务现有 BLE/状态机/凭据链路行为零变更；现有 277 测试基线必须保持全绿
- 管道 ACL 仅 `SYSTEM` + `Administrators`；单活动客户端；服务端只写不读
- 密码绝不写入日志、临时文件或遥测帧；管理端界面默认掩码
- 管理端：unpackaged、自包含 win-x64、单实例、关闭=隐藏到托盘、退出仅经托盘菜单
- 提权：手动启动弹 UAC（manifest `requireAdministrator`）；登录自启经计划任务（最高权限）免 UAC
- 协议 v1：字段只增不改；管理端忽略未知字段
- 代码注释中文（与近期提交一致）、标识符英文；时间走 `TimeProvider`；测试不 `Thread.Sleep`
- 构建入口：单项目 `dotnet build`（`AutoUnlockMyPc.sln` 含 C++ vcxproj，不可用 CLI 整体构建）
- **Task 19 关联**：服务实机验收暂停中；本功能完成后，Task 19 恢复验收必须使用含管理通道的新构建重跑关键项（台账：`.superpowers/sdd/2026-10-13-autounlock-service/progress.md`）

## Review Focus

1. **管道安全与健壮性**：ACL、新连接顶替旧连接、断连清理、遥测异常隔离（不得冒泡到服务主流程）
2. **遥测节流与内存**：250ms 节拍、环形缓冲 5 分钟/4096 条上限、无客户端时零序列化开销
3. **配置保存**：原子替换（临时文件 + `File.Replace`、保留 `.bak`）、校验规则镜像、热重载日志确认与超时路径
4. **提权与部署**：计划任务免 UAC 自启、快捷方式、卸载残留清理、发布产物完整性
5. **日志处理**：大文件增量跟随、轮转/换日切换、过滤与高亮正确性、列表虚拟化

## File Structure

| 路径 | 职责 |
| --- | --- |
| `src/AutoUnlockService/Management/ManagementProtocol.cs` | v1 帧 DTO + 序列化 |
| `src/AutoUnlockService/Management/TelemetryHub.cs` | 旁路订阅 + 环形缓冲 + 快照组装 |
| `src/AutoUnlockService/Management/ManagementPipeServer.cs` | 命名管道服务端（ACL/单客户端/推送节拍） |
| `src/AutoUnlockService/Worker.cs`、`Program.cs` | 接线与 DI（仅新增行，不改现有逻辑） |
| `src/AutoUnlockManager.Core/` | 纯逻辑类库（net10.0）：配置模型+校验镜像、帧解析、日志行解析、路径常量、CSV |
| `src/AutoUnlockManager/` | WinUI 3 应用：导航、概览/配置/日志三页、托盘、单实例、管道客户端、SCM 控制 |
| `tests/AutoUnlockService.Tests/Management/…` | 服务端通道单测（协议/缓冲/节流/映射/顶替） |
| `tests/AutoUnlockManager.Tests/` | Core 单测（校验镜像/JSON 往返/帧解析/日志解析/CSV） |
| `scripts/install.ps1`、`scripts/uninstall.ps1` | 管理端部署、快捷方式、计划任务、卸载清理 |
| `README.md` | 管理端构建/发布/部署说明 |

（具体文件在任务简报中细化；简报与评审记录存 `.superpowers/sdd/2026-10-16-autounlock-manager/`。）

## Task 1: 规划与设计落档 + 版本锁定

- [x] 设计文档 `docs/superpowers/specs/2026-10-16-autounlock-manager-design.md`
- [x] 本规划文档
- [x] 版本锁定试验（临时工程，不入库）：WinAppSDK 2.5.1 + H.NotifyIcon.WinUI 2.4.1 + BuildTools 10.0.28000.2705，TFM `net10.0-windows10.0.19041.0`，unpackaged + `WindowsAppSDKSelfContained` + .NET 自包含
- 证据：`dotnet build -c Release` 0 警告 0 错误；`dotnet publish -r win-x64 --self-contained` 成功（524 文件 / 228.4 MB，含 `Microsoft.ui.xaml.dll`、`Microsoft.WindowsAppRuntime.dll`、`H.NotifyIcon(.WinUI).dll`、`coreclr.dll`）；发布产物启动存活 5 秒无崩溃（无 PRI/XBF 缺失问题）

## Task 2: 服务端管理通道（Management v1）

- [x] 协议：JSON 单行帧（`\n`），`t`=hello/snapshot；seq/gen/tsMs；schema 按设计文档 §3.3
- [x] `TelemetryHub`：旁路订阅样本/状态转换/扫描状态/助手与凭据连接态；5 分钟环形缓冲（4096 上限）；快照组装（两窗口均值、`OptionsGate` 生效阈值、状态、健康、增量样本；历史重置 gen 递增）
- [x] `ManagementPipeServer`：`PipeSecurity`（SYSTEM+Administrators）、单客户端（断连/写超时即释放，无主动顶替）、250ms 节拍 + 变化即推、连接补发、推送异常隔离（不影响服务主流程）
- [x] `Worker`/`Program` 接线（不改现有逻辑；无客户端零序列化开销）
- [x] 单测：帧序列化往返、环形缓冲裁剪、节流合并、状态映射、单客户端行为、gen 重置、敏感信息哨兵
- 验证：基线 + 新增测试全绿（实跑 277 → 306，0 失败）；`dotnet build src\AutoUnlockService -c Release` 0 警告 0 错误；评审 Approved with findings（全部落实，见简报评审记录）

## Task 3: 管理端骨架（Core + WinUI 项目 + 托盘 + 导航）

- [x] `AutoUnlockManager.Core`（net10.0 类库）+ `AutoUnlockManager`（WinUI 项目，引用 Core）+ `tests/AutoUnlockManager.Tests`（引用 Core）
- [x] 单实例（Global 互斥体 + 唤醒事件）、manifest `requireAdministrator`、窗口图标/标题
- [x] NavigationView 三页骨架（概览/配置/日志）+ 系统主题跟随
- [x] 托盘（H.NotifyIcon）：图标状态色、菜单（显示/服务状态/启动/停止/重启/退出）、关闭=隐藏、双击显示
- 验证：`dotnet build src\AutoUnlockManager -c Release`；本机启动：托盘常驻、关闭不退出、二次启动唤出已有窗口
- [x] 后补（用户要求）：WinUI 模板风格（Mica/自定义标题栏/卡片页）+ 亮暗主题切换（设置页）→ 提交 `424a7a1`

## Task 4: 概览页（实时 dBm + 状态 + 服务控制）

- [x] `ManagementClient`（管道连接/2s 退避重连/帧分发/断线横幅/未知字段容错）
- [x] 大号当前 dBm + 5s/10s 均值 + 状态芯片（中文映射 + 已持续时长）+ 健康行（扫描/助手/凭据/运行时长/最近匹配）
- [x] 自绘曲线：5 分钟滚动、双阈值线（随 `thr.*` 变化）、自动量程、降采样 ≤600 点
- [x] 服务控制（SCM 启动/停止/重启，带确认与错误呈现）
- [x] 样本 CSV 导出（`timestamp_iso,rssi_dbm`）
- 验证：诊断副本注入样本全场景（4/s 流、断线横幅 + 2s 退避重连、1200 点回溯降采样 587→≤600、坏帧零异常、页面往返重连、SCM 失败横幅、主题专项色值翻转）+ 59 测试全绿 + 用户真机烟测通过 → 提交 `54ddfcb`

## Task 5: 配置页（全字段表单 + 热重载确认）

- [ ] 4 组 16 字段表单 + 枚举下拉（IrkByteOrder、NotifierMode）；密码掩码+眼睛
- [ ] Core 校验镜像 `OptionsValidator` 规则与文案；上限常量镜像 `HelperProtocol`（600s / 240min）
- [ ] 路径：`%ProgramData%\AutoUnlock\appsettings.json`；目录缺失 → 横幅提示（服务可能未安装）
- [ ] 保存：JsonNode 原位改值（保留未知字段）→ 临时文件 + `File.Replace`（`.bak`）→ 监听日志 `配置已重载（…）` 成功 / `配置校验失败，已保留原配置` 失败；5s 超时 → 提示 + 重启服务按钮；「重新从磁盘加载」「打开所在文件夹」
- [ ] 单测：校验规则（含边界）、JSON 往返保真、备份/替换
- 验证：改阈值 → 保存 → 服务日志确认重载 → 行为生效（用户机）；非法值被拦；`.bak` 生成

## Task 6: 日志页（跟随/过滤/高亮/导出）

- [ ] 目录 `%ProgramData%\AutoUnlock\logs\autounlock-*.log`（Serilog 日滚、保留 7、`shared: true`）
- [ ] 文件下拉（按日期）；增量跟随（FileShare.ReadWrite）+ 自动滚动 + 暂停/继续；换日自动切换
- [ ] 级别/关键字过滤；WRN/ERR 高亮；导出当前过滤视图；打开文件夹；虚拟化列表（行数上限）
- [ ] 单测：日志行解析（时间戳/级别/消息）、过滤组合
- 验证：跟随延迟 <1s；10 万行级滚动不卡

## Task 7: 安装集成与自包含发布

- [ ] `install.ps1`：探测发布目录（候选含 `src\AutoUnlockManager\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`）→ 结束运行中进程 → 拷贝到 `$env:ProgramFiles\AutoUnlock\Manager` → 开始菜单快捷方式 → 计划任务 `AutoUnlockManager`（AtLogOn、最高权限、InteractiveToken、默认=安装执行账户，`-ManagerUser` 覆盖）
- [ ] `uninstall.ps1`：注销任务、删快捷方式、删 Manager 目录
- [ ] README：管理端构建/发布/部署说明
- 验证：完整安装 → 注销重登无 UAC 自启；卸载无残留（任务/快捷方式/目录）；幂等重装

## Task 8: 全量验证与交付

- [ ] 全部构建 + 测试（服务、Core、CP）全绿；发布产物校验（文件数/关键 DLL/启动）
- [ ] 本机端到端联调清单（注入样本 + 真实服务联调）
- [ ] 交付用户实机验收清单（dBm 实时、配置生效、日志、托盘、自启/卸载）
- [ ] 台账收尾：里程碑、已知限制、Task 19 恢复提醒（新构建重跑）
- 验证：清单可复现执行
