# AutoUnlockManager 设计文档

**日期：** 2026-10-16  **状态：** 已确认开工  **关联规划：** `docs/superpowers/plans/2026-10-16-autounlock-manager.md`

## 1. 背景与目标

AutoUnlockService 本体已完成实机验证（BLE 自动锁定/解锁 go）。缺一个管理端，用于：

- 调试校准：调阈值时实时看 dBm，不用靠日志猜
- 日常监控：状态、日志、托盘常驻
- 未来分发：随 `install.ps1` 一并部署，绿色自包含

已锁定的需求决定（两轮问答）：

| 维度 | 决定 |
| --- | --- |
| 使用定位 | 调试校准 + 日常监控 + 未来分发（全选） |
| dBm 来源 | 服务端新增**只读遥测**命名管道（不改现有行为） |
| 配置编辑 | 全字段分组表单；保存 = 写文件 + 服务热重载 + 日志确认 |
| 服务控制 | 状态 + 启动/停止/重启（SCM） |
| 日志 | 跟随/过滤/历史/导出/高亮（全功能；CSV 导出一并提供） |
| 提权 | 手动启动弹 UAC；登录自启走计划任务免 UAC |
| 分发 | 自包含产物进本仓库，随 `install.ps1` 部署 |
| 托盘 | 关闭缩托盘、自启、菜单含显示/服务控制/退出、状态色 |
| 界面 | 中文，三页导航（概览/配置/日志），密码掩码 + 眼睛切换 |
| 方案要点 | 管理端**只读**消费；不给服务下指令 |

## 2. 总体架构

```
┌─ Session 0（系统）─────────────────────────────┐
│ AutoUnlockService (LocalSystem)                │
│  ├─ BLE 扫描 / 状态机 / 凭据+助手管道（现状）   │
│  └─ ManagementPipeServer ──push 遥测──┐         │
└───────────────────────────────────────┼────────┘
        ▲ SCM 启停                      │ \\.\pipe\AutoUnlockManagement
        │                               │（SYSTEM+Administrators，只读）
┌───────┴─ 用户会话（提权）─────────────▼────────┐
│ AutoUnlockManager（WinUI 3，托盘常驻）          │
│  ├─ 概览：dBm 曲线 / 状态 / 健康 / 服务控制 / CSV
│  ├─ 配置：%ProgramData%\AutoUnlock\appsettings.json
│  └─ 日志：%ProgramData%\AutoUnlock\logs\autounlock-*.log
└────────────────────────────────────────────────┘
```

原则：

- 管理通道**只读**：服务端只写不读，管理端只读；服务永远不会因为管理端而改变行为
- 配置变更路径 = 文件 + 服务既有热重载（`OptionsGate` 防抖 + 校验），与手工改文件完全同一条路径
- 服务控制 = SCM 标准接口（服务始终可被系统正常管理）
- 遥测异常隔离：任何管理通道故障不得冒泡到服务主流程

## 3. 服务端管理通道（Management v1）

### 3.1 传输

- 管道名：`\\.\pipe\AutoUnlockManagement`
- ACL：`SYSTEM` + `BUILTIN\Administrators` 完全控制（`PipeSecurity` 实现），其他主体不可连接
- 单活动客户端：同一时间只服务一个客户端；旧连接断开或写失败（写超时 5s + 250ms 节拍）后立即释放并接受新连接。管理端有单实例互斥体，正常场景不存在双客户端，故作此简化（无主动顶替；卡死的旧客户端最迟约 5s 被清理，请求方 2s 重试）
- push-only：服务端只写快照，**不读取**任何客户端数据
- 帧格式：UTF-8 JSON、单行、以 `\n` 结尾；以 `t` 字段判别类型

### 3.2 帧类型

- `hello`：连接后第一帧。`{"t":"hello","v":1,"svc":"<服务版本>"}`
- `snapshot`：数据帧（见 3.3）。连接后首个 snapshot 的 `samples` 为最近 5 分钟回溯，之后为增量

### 3.3 snapshot schema（v1）

```json
{
  "t": "snapshot", "v": 1, "seq": 813, "gen": 2, "tsMs": 1760648000123,
  "uptimeSec": 532.4,
  "state": { "name": "LockedArmed", "sinceMs": 1760647911250 },
  "scan":  { "status": "Running", "detail": null },
  "helper": { "connected": true },
  "cred":   { "connected": true },
  "thr": { "lockDbm": -65, "unlockDbm": -45, "lockWinSec": 10, "unlockWinSec": 5, "minSamples": 3 },
  "avg": { "w5": -57.3, "w10": -58.1, "n5": 6, "n10": 14 },
  "lastMatchMs": 420,
  "samples": [ [1760647975000, -61], [1760647976250, -59] ]
}
```

| 字段 | 说明 |
| --- | --- |
| `seq` | 帧序号（每帧 +1）；管理端可检测丢帧 |
| `gen` | 历史代数：服务每次清空 RSSI 历史（休眠恢复、目标设备变更）时 +1（初始 1）；管理端见 `gen` 变化即清空曲线重新累积 |
| `tsMs` | 服务端 UTC 毫秒时间戳 |
| `uptimeSec` | 服务进程运行时长 |
| `state.name` | `ProximityState` 枚举名：Unlocked / PendingLock / Snoozed / LockedWaitingDeparture / LockedArmed / UnlockPending / Suspended（实现直接取枚举 `ToString()`） |
| `state.sinceMs` | 进入当前状态的时间（UTC 毫秒） |
| `scan.status` | BLE 扫描健康状态（沿用现有状态上报语义：运行中/重试中/不可用等；实现时钉死取值） |
| `scan.detail` | 补充说明（可空） |
| `helper.connected` / `cred.connected` | 助手 / 凭据提供程序管道当前连接状态 |
| `thr.*` | **当前生效**阈值（取自 `OptionsGate.Current`，即服务实际采用的校验后值；热重载后自动变化，曲线阈值线随之移动） |
| `avg.w5/w10` | `RssiHistory` 5s / 10s 窗口均值（null = 无样本） |
| `avg.n5/n10` | 窗口内样本数 |
| `lastMatchMs` | 距最近一次匹配到目标设备的时长（null = 本次运行从未匹配） |
| `samples` | `[tsMs, rssi]` 数组；连接后首帧 = 最近 5 分钟回溯，之后 = 上次推送后的增量 |

### 3.4 节拍与缓冲

- 客户端在线时每 250ms 推送一个 snapshot；状态转换、扫描状态变化、助手/凭据连接变化 → 立即补推一帧
- 环形缓冲：样本 5 分钟 / 上限 4096 条（先到者为准），供连接回溯
- 无客户端时：只维护环形缓冲，不序列化（零 JSON 开销）
- 客户端断开：写失败即清理连接与订阅状态，等待下次连接

### 3.5 隔离与安全

- 订阅回调、序列化、写管道全部包裹在 try/catch 内；异常仅记 Debug/Warning 日志，节拍失效时下一轮自愈
- 帧中**不含**凭据（用户名/密码）、IRK 明文等敏感信息
- 服务端不接受任何指令语义；即使有人写入数据也不读取

## 4. 数据路径（固定，无需探测）

| 对象 | 路径 | 依据 |
| --- | --- | --- |
| 服务配置 | `%ProgramData%\AutoUnlock\appsettings.json` | `Program.cs` `DataDir`（CommonApplicationData + "AutoUnlock"） |
| 服务日志 | `%ProgramData%\AutoUnlock\logs\autounlock-*.log` | Serilog File sink：按日滚动、保留 7 个、`shared: true`（允许多进程写入与外部实时读取） |
| 管理端自身设置 | `%LocalAppData%\AutoUnlockManager\settings.json` | 窗口位置等非敏感项 |

- 数据目录不存在 → 管理端显示「未检测到服务数据目录（服务可能未安装）」横幅；配置页禁用保存并给出路径提示
- 配置路径不含环境变量展开歧义（`SHGetKnownFolderPath` 语义，直接用 .NET `Environment.SpecialFolder.CommonApplicationData`）

## 5. 管理端设计

### 5.1 技术与形态

- WinUI 3 / Windows App SDK **2.5.1** + H.NotifyIcon.WinUI **2.4.1**（构建 + 自包含发布 + 启动均已本机验证）
- TFM `net10.0-windows10.0.19041.0`，RID `win-x64`，unpackaged，**自包含**（`WindowsAppSDKSelfContained=true` + .NET self-contained；实测发布 524 文件 / 228.4 MB）
- `AutoUnlockManager.Core`（net10.0 纯逻辑类库）：配置模型 + 校验镜像、帧解析、日志行解析、路径常量、CSV——可单测、无 UI 依赖
- MVVM：CommunityToolkit.Mvvm

### 5.2 启动、提权与单实例

- manifest `requireAdministrator`：手动双击 → UAC；经计划任务（最高权限）启动 → 免 UAC
- 计划任务由 `install.ps1` 注册（AtLogOn、InteractiveToken、RunLevel Highest；默认 = 安装脚本执行账户，`-ManagerUser` 可覆盖）
- 单实例：`Global` 互斥体 + 命名唤醒事件；二次启动 → 置位事件后退出；首实例后台等待 → 拉出主窗口
- 关闭主窗口 = 隐藏到托盘；真正退出仅经托盘菜单「退出」

### 5.3 页面

**概览页**

- 状态芯片：`state.name` 中文映射 + 已持续时长（`sinceMs`）
- 大号 dBm：最近样本值 + 5s/10s 均值（无样本时显式显示「—」，不显示 NaN）
- 曲线：5 分钟滚动窗口；双阈值线（`thr.*`，热重载后随之移动）；自动量程；降采样 ≤600 点；轻量自绘（Canvas/Polyline），不引重型图表库
- 健康行：BLE 扫描状态、助手/凭据连接、服务运行时长、最近匹配（>5 分钟标黄）
- 服务控制：启动/停止/重启（SCM `ServiceController`），带确认与结果提示
- CSV 导出：样本 `timestamp_iso,rssi_dbm`
- 连接横幅：管道断开显示「服务未运行/无法连接」，2 秒退避自动重连

**配置页**

- 4 组 16 字段：
  - 设备：Irk、IrkByteOrder（Auto/Normal/Reversed）、IdentityAddress（可空）
  - 凭据：UserName、Password（掩码 + 眼睛切换）
  - 接近：LockThresholdDbm、LockWindowSeconds、UnlockThresholdDbm、UnlockWindowSeconds、MinSamples、StartupGraceSeconds、UnlockPendingTimeoutSeconds
  - 通知：Mode（Toast/Dialog）、PromptTimeoutSeconds、RejectSnoozeSeconds、SnoozeMinutes
- 校验（镜像服务端 `OptionsValidator` 规则与提示文案；上限常量镜像 `HelperProtocol`）：
  - Irk 必须 32 位十六进制（容忍空格/冒号/连字符/`0x` 前缀输入，保存前归一化）
  - IdentityAddress 留空，或 12 位 hex（容忍分隔符）
  - UserName 非空；UserName / Password 不含控制字符
  - UnlockThresholdDbm > LockThresholdDbm（死区为正）
  - LockWindowSeconds / UnlockWindowSeconds / UnlockPendingTimeoutSeconds > 0；StartupGraceSeconds ≥ 0；MinSamples ≥ 1
  - PromptTimeoutSeconds ∈ (0, 600]；RejectSnoozeSeconds > 0；SnoozeMinutes ∈ (0, 240]
- 保存流水线：校验 → 差异摘要确认 → 读原 JSON（`JsonNode` 原位改值，保留未知字段与结构）→ 写同目录临时文件 → `File.Replace` 原子替换（保留 `.bak`）→ 监听日志出现 `配置已重载（设备变更=…，凭据变更=…）`（成功）或 `配置校验失败，已保留原配置`（失败）→ 提示结果；5 秒超时 → 提示「未确认重载」+「重启服务」按钮
- 辅助：「重新从磁盘加载」「打开所在文件夹」
- 注意：启动时配置非法会导致服务**拒绝启动**（`Program.cs` 早期校验），因此保存前校验必须与服务端一致

**日志页**

- 文件下拉：`%ProgramData%\AutoUnlock\logs\autounlock-*.log`（按日期），默认最新文件
- 实时跟随：增量读（FileShare.ReadWrite）、自动滚动、暂停/继续；换日自动跟随新文件
- 过滤：级别（DBG/INF/WRN/ERR）、关键字；WRN/ERR 行高亮；导出当前过滤视图
- 性能：虚拟化列表；内存窗口上限（如 20 万行）

### 5.4 托盘

- 图标状态色：绿（正常）、黄（降级/告警：如通知降级、>5 分钟无匹配、重试暂停）、红（服务停止/断开）、灰（未知）
- 菜单：显示主窗口｜服务状态（只读）｜启动服务｜停止服务｜重启服务｜退出
- 关闭按钮 → 隐藏；双击托盘图标 → 显示主窗口

### 5.5 连接与容错

- 管道客户端：连接失败/断开 → 2 秒退避重连；重连后清空曲线并用首帧回溯重新回填
- 服务重启（控制操作或自动恢复）→ 客户端自动重连
- 管理端不得因缺帧、乱序、未知字段而崩溃（忽略未知字段）

## 6. 部署与安装

- 发布：`dotnet publish src\AutoUnlockManager -c Release -r win-x64 --self-contained true`（csproj 已设 `WindowsAppSDKSelfContained`）
- `install.ps1` 追加：
  - 探测发布目录（候选含 `src\AutoUnlockManager\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`）
  - 结束运行中的 AutoUnlockManager 进程（避免文件占用）→ 拷贝到 `$env:ProgramFiles\AutoUnlock\Manager`
  - 开始菜单快捷方式 `%ProgramData%\Microsoft\Windows\Start Menu\Programs\AutoUnlockManager.lnk`
  - 计划任务 `AutoUnlockManager`（AtLogOn、最高权限、InteractiveToken；默认 = 安装脚本执行账户，`-ManagerUser` 覆盖）
- `uninstall.ps1` 追加：注销任务、删快捷方式、删 Manager 目录
- 要求：幂等（重复安装/卸载不报错）

## 7. 影响与兼容

- 服务：仅新增 `Management` 命名空间 + `Worker`/`Program` 尾部接线；现有行为零变更；277 测试基线保持全绿
- Task 19（实机验收）恢复时：必须用**含管理通道的新构建**重跑关键项（已记台账）
- 协议演进：v1 字段只增不改；管理端忽略未知字段

## 8. 验收要点（管理端）

1. 概览：真实设备靠近/远离时曲线与状态实时变化；阈值线与当前配置一致；CSV 可导出
2. 配置：改阈值保存 → 服务日志出现「配置已重载」→ 行为按新阈值生效；非法值被表单拦截；`.bak` 生成
3. 日志：跟随延迟 < 1 秒；过滤/高亮/导出可用
4. 托盘：关闭缩托盘、菜单控制服务、状态色正确、退出可用
5. 提权与自启：手动双击弹 UAC；注销重登无 UAC 自启；卸载无残留
6. 容错：服务停止时管理端不崩溃、自动重连；重复启动管理端唤出已有实例

## 9. 风险与回退

| 风险 | 处置 |
| --- | --- |
| H.NotifyIcon 运行时异常 | 托盘逻辑收敛在 TrayIconHost 内，可回退 `Shell_NotifyIcon` 互操作 |
| 228 MB 发布体积 | 接受（绿色分发导向）；如需缩小再评估裁剪 |
| 配置保存竞态（服务读到半文件） | 临时文件 + `File.Replace` 原子替换；`OptionsGate` 防抖 |
| 管理端改配置后服务拒绝启动 | 表单校验镜像服务规则；保留 `.bak` 可回滚 |
| 日志文件被服务占用 | Serilog `shared: true`；读取用 FileShare.ReadWrite |
| 遥测帧偏大（250ms 节拍） | 帧只含增量样本；无客户端零开销；管理端解析纯内存 |
