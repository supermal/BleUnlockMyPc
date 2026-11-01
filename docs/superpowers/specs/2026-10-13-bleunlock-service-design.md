# BleUnlockService — 设计规格

- **日期：** 2026-10-13
- **状态：** 待审阅
- **取代：** `2026-04-24-bleunlock-design.md` 中关于 BleUnlockService 的部分（原方案基于 DX-BT401 串口模块读 RSSI，本方案改用 Windows 自带 BLE + IRK）

---

## 1. 背景与目标

在已登录的 Windows 11 上，用一块随身 BLE 设备作为「人在不在」的依据。设备靠 IRK 识别，即使它按 BLE 隐私规范不断轮换随机地址（RPA）也能持续追踪。人走远则自动锁屏，人回来则自动解锁，全程不碰键盘。

仓库中已有的 C++ 凭据提供程序（`BleUnlockCredentialProvider`）负责在锁屏界面提交凭据，本期为它扩展认证结果回传协议。本期新建 `BleUnlockService`（Windows 服务）与 `BleUnlockHelper`（用户会话助手）两个 .NET 进程。

BLE 扫描、IRK 解析等算法从同机的参考项目 `D:\Users\Admin\Desktop\BleRssiMonitor` **复制源码**移植，两个项目此后独立演进。

### 1.1 成功标准

1. 戴着设备走开，约 10 秒内弹出通知，再 15 秒无响应则锁屏
2. 锁屏后原地不动**不会**解锁；走远再回来约 5 秒内自动解锁
3. 通知的「拒绝」与「5 分钟内不锁定」两个按钮行为正确
4. 密码填错时自动解锁停摆并告知用户，改回正确密码后无需重启服务即自动恢复
5. 修改 appsettings.json 的阈值后无需重启服务即生效
6. 日常使用中不出现误锁打断工作，也不出现把人锁在外面

---

## 2. 需求基线

### 2.1 已确认决策

| # | 决策 |
|---|------|
| 1 | 只覆盖**锁屏解锁**（已登录会话），不处理开机登录界面的自动登录 |
| 2 | **单设备、单 IRK** |
| 3 | 锁屏后必须**先观察到离开、再观察到回来**才解锁，Win+L 要真能锁住 |
| 4 | 时间窗内收不到有效读数即走锁屏流程，**不区分原因**（设备走远、手表熄屏、没电、蓝牙关闭、适配器故障一视同仁）。锁屏前**必定先弹通知**，含「拒绝」与「5 分钟内不锁定」两个按钮，超时无响应则锁屏 |
| 5 | 认证失败 → **停摆**，直到配置被修改才恢复 |
| 6 | 形态为 **Windows 服务**（LocalSystem），不先做 Session 0 可行性验证 |
| 7 | 认证结果**双保险**：扩展 C++ CP 回传 NTSTATUS + 服务侧 `LogonUser` 预验证 |
| 8 | 参考项目算法**复制源码**，两项目独立演进 |
| 9 | 配置放 `%ProgramData%\BleUnlock\`，ACL 收紧；PowerShell 安装脚本；`TestCredentialProviderApp` 保留；纯算法 TDD + 实机验收 |
| 10 | 通知采用**方案 A + 双实现**：常驻助手弹真 Toast，同时保留 `WTSSendMessage` 对话框实现作为可切换的备选与自动降级目标 |

### 2.2 已确认假设

1. **账户类型三种都支持**：本地账户、Microsoft 账户（`MicrosoftAccount\邮箱`）、域账户（`域\用户名`）。服务按配置中的用户名格式自动判断
2. **判据只用 dBm**，不做距离估算
3. **阈值默认值沿用参考项目**，全部可配
4. **IRK 字节序默认 `Auto`**（Normal 与 Reversed 都试，命中后记住）
5. **锁屏实现**：`WTSGetActiveConsoleSessionId` + `WTSQueryUserToken` + `CreateProcessAsUser`，只服务当前活动的控制台会话（单用户场景）
6. **热更新覆盖全部配置项**
7. **目标框架 `net10.0-windows10.0.19041.0`**（WinRT 蓝牙 API 要求）。仓库 README 中 .NET 8 的描述同步更新
8. **Serilog** 写文件 + Windows 事件日志，**密码永不入日志**
9. **服务启动类型**：自动（延迟启动），崩溃由 SCM 自动重启

---

## 3. 约束

- 运行环境：Windows 11 x64，需支持 BLE 的蓝牙适配器
- 技术栈：.NET 10，目标框架 `net10.0-windows10.0.19041.0`
- 蓝牙 API：WinRT `Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher`
- C++ 侧：Visual Studio 2022，C++17，Windows SDK，x64
- 第三方依赖：Serilog（含 File 与 EventLog sink）、`CommunityToolkit.WinUI.Notifications`（Toast）、测试用 xUnit
- 文档与界面语言：中文；代码标识符：英文；代码注释：西班牙语

---

## 4. 架构

### 4.1 进程职责

| 进程 | 身份 | 职责 |
|------|------|------|
| `BleUnlockService` | LocalSystem / Session 0 | BLE 扫描、IRK 解析、接近状态机、凭据与 `LogonUser` 预验证、两个管道服务端、助手进程守护 |
| `BleUnlockHelper` | 当前活动会话的登录用户 | 弹 Toast 并回传按钮结果、执行 `LockWorkStation` |
| `BleUnlockCP.dll` | winlogon 加载（SYSTEM） | 在锁屏界面提交凭据、回传认证结果 |

### 4.2 数据流

```
┌──────────────── Session 0 ─────────────────────────────────┐
│  BleUnlockService (LocalSystem)                           │
│                                                            │
│  BleScanner ──► RpaResolver ──► RssiHistory                │
│                                      │                     │
│                            ProximityStateMachine           │
│                           ╱          │          ╲          │
│                   INotifier    IScreenLocker   CredentialPipeServer
│                        │             │                │    │
└────────────────────────┼─────────────┼────────────────┼────┘
                         │             │                │
     \\.\pipe\BleUnlockHelper         │      \\.\pipe\BleUnlock
                         │             │                │
┌────────────────────────┼─────────────┼────┐  ┌────────┼──────────┐
│  BleUnlockHelper (用户会话)              │  │  winlogon / LogonUI│
│  Toast 通知  ·  LockWorkStation()        │  │  BleUnlockCP.dll  │
└──────────────────────────────────────────┘  └───────────────────┘
```

### 4.3 双实现的落点

`INotifier` 与 `IScreenLocker` 两个抽象都在**服务侧**：

| 抽象 | Toast 模式实现 | Dialog 模式实现 |
|------|---------------|----------------|
| `INotifier` | `HelperNotifier`：把请求经管道转给助手，助手弹 Toast | `WtsMessageNotifier`：服务直接调 `WTSSendMessageW`，不经助手 |
| `IScreenLocker` | `HelperScreenLocker`：指令助手调 `LockWorkStation` | `ProcessScreenLocker`：`CreateProcessAsUser` 跑 `rundll32 user32.dll,LockWorkStation` |

由 `Notification.Mode`（`Toast` / `Dialog`）选择，且在助手连续启动失败时**自动降级**到 Dialog 实现（见 §9）。

---

## 5. 项目结构

```
BleUnlockMyPc/
├─ BleUnlockMyPc.sln
├─ scripts/
│  ├─ install.ps1                      建目录、设 ACL、装服务、注册 CP、建 AUMID 快捷方式
│  └─ uninstall.ps1
├─ src/
│  ├─ BleUnlockService/
│  │  ├─ Program.cs                    Host + Serilog + Windows 服务宿主
│  │  ├─ Worker.cs                     编排：样本流 → 状态机 → 副作用
│  │  ├─ Ble/
│  │  │  ├─ AdvertisementSample.cs     ← 复制自参考项目
│  │  │  ├─ IRssiSource.cs             抽象，便于将来把扫描挪进助手
│  │  │  └─ BleScanner.cs              ← 复制自参考项目
│  │  ├─ Core/
│  │  │  ├─ BleAddress.cs              ← 复制自参考项目
│  │  │  ├─ RpaResolver.cs             ← 复制自参考项目（单 IRK 化）
│  │  │  ├─ DeviceMatcher.cs           判定一条广播是否来自目标设备
│  │  │  ├─ RssiHistory.cs             时间窗样本队列
│  │  │  └─ ProximityStateMachine.cs   ← 由参考项目 LockSimulator 改造
│  │  ├─ Credentials/
│  │  │  ├─ CredentialValidator.cs     LogonUser 预验证
│  │  │  ├─ NtStatusPolicy.cs          哪些 NTSTATUS 该停摆
│  │  │  └─ CredentialPipeServer.cs    \\.\pipe\BleUnlock
│  │  ├─ Notifications/
│  │  │  ├─ INotifier.cs
│  │  │  ├─ HelperNotifier.cs
│  │  │  └─ WtsMessageNotifier.cs
│  │  ├─ Session/
│  │  │  ├─ SessionMonitor.cs          SERVICE_CONTROL_SESSIONCHANGE
│  │  │  ├─ HelperLauncher.cs          CreateProcessAsUser + 守护 + 降级
│  │  │  ├─ HelperPipeServer.cs        \\.\pipe\BleUnlockHelper
│  │  │  ├─ IScreenLocker.cs
│  │  │  ├─ HelperScreenLocker.cs
│  │  │  └─ ProcessScreenLocker.cs
│  │  ├─ Config/
│  │  │  ├─ BleUnlockOptions.cs
│  │  │  └─ OptionsValidator.cs
│  │  └─ appsettings.json              模板，安装时拷到 ProgramData
│  ├─ BleUnlockHelper/                无窗口用户态助手
│  │  ├─ Program.cs                    连管道、收指令、回结果
│  │  ├─ ToastPrompt.cs                Toast 弹出与撤销
│  │  └─ ScreenLock.cs                 LockWorkStation
│  ├─ BleUnlockCredentialProvider/    现有 C++，本期扩展回传协议
│  └─ TestCredentialProviderApp/       保留，不走 BLE 直接测 CP 的排查工具
├─ tests/
│  └─ BleUnlockService.Tests/         xUnit
└─ docs/superpowers/specs/
```

**关于 `DeviceMatcher`**：参考项目里「IRK 解析」与「身份地址直接匹配」两条判定分散在 `RpaResolver` 与 `DeviceRegistry` 中，而 `DeviceRegistry` 本期不移植。因此新增一个很薄的 `DeviceMatcher`，把两条判定收敛到一处：先按 `IdentityAddress` 直接比对，不中则交给 `RpaResolver` 解析 RPA。状态机只需要「这条广播是不是目标设备」这一个布尔结论。

**不移植的参考项目组件**（单设备、只用 dBm、IRK 从配置读，这些在服务里是死重量）：
`DistanceEstimator`、`DeviceRegistry`、`DeviceState`、`IrkStore`、`IrkEntry`、`CompanyNames`、`TrackingEventDetector`、`LogBuffer`、`RssiFilter`。

**关于 `RssiFilter`**：参考项目的锁屏判定 `LockSimulator` 读的是**原始 RSSI**，`RssiFilter`（中位数 + EMA）只服务于界面曲线。服务里没有曲线，而时间窗平均本身已足够平滑，叠加 EMA 只会凭空增加解锁延迟，故不移植。

---

## 6. 接近状态机

### 6.1 判定函数

两者都在**原始 RSSI** 的时间窗上计算：

| 判定 | 条件 |
|------|------|
| `IsFar(now)` | 窗口 `LockWindowSeconds` 内**样本数为 0** → 真；否则样本数 ≥ `MinSamples` 且窗口平均 < `LockThresholdDbm` → 真 |
| `IsNear(now)` | 窗口 `UnlockWindowSeconds` 内样本数 ≥ `MinSamples` **且**窗口平均 > `UnlockThresholdDbm` → 真 |

不对称是刻意的：**无信号算「远」，但无信号永远不算「近」**。解锁这一侧绝不基于推测。

### 6.2 状态

| 状态 | 含义 |
|------|------|
| `Unlocked` | 屏幕解锁中，监控是否该锁 |
| `PendingLock` | 已判定该锁，通知已弹出，等待响应 |
| `Snoozed` | 用户按了「拒绝」或「5 分钟内不锁定」，静默期内不评估锁屏 |
| `LockedWaitingDeparture` | 屏幕已锁，**尚未观察到离开**，禁止解锁 |
| `LockedArmed` | 屏幕已锁且已观察到离开，靠近即解锁 |
| `UnlockPending` | 凭据已推送给 CP，等待认证结果 |
| `Suspended` | 认证失败停摆，等待配置修改 |

### 6.3 转换

> 2026-10-18 起判定改为「批次全数达标」，下表中的 `IsFar` / `IsNear` 时间窗与提示倒计时已不再使用。**当前的完整判定条件以 [`docs/lock-unlock-detailed-conditions.md`](../../lock-unlock-detailed-conditions.md) 为准。**

| 当前状态 | 事件 | 新状态 | 副作用 |
|---------|------|-------|-------|
| `Unlocked` | `IsFar` 且已过启动宽限 | `PendingLock` | 弹通知，启动 `PromptTimeoutSeconds` 倒计时 |
| `PendingLock` | `IsNear` | `Unlocked` | 撤销通知 |
| `PendingLock` | 用户选「拒绝」 | `Snoozed` | 静默 `RejectSnoozeSeconds` |
| `PendingLock` | 用户选「5 分钟内不锁定」 | `Snoozed` | 静默 `SnoozeMinutes` × 60 秒 |
| `PendingLock` | 倒计时超时 / 用户忽略 | `LockedWaitingDeparture` | 执行锁屏 |
| `Snoozed` | 静默期满 | `Unlocked` | — |
| `LockedWaitingDeparture` | `IsFar` | `LockedArmed` | — |
| `LockedArmed` | `IsNear` | `UnlockPending` | 推送凭据给 CP |
| `UnlockPending` | `RESULT OK` 或 `SessionUnlock` | `Unlocked` | — |
| `UnlockPending` | `RESULT FAIL` 且属停摆状态码 | `Suspended` | 弹通知告知原因 |
| `UnlockPending` | `RESULT FAIL` 但不属停摆状态码 | `LockedArmed` | 记日志，允许重试 |
| `UnlockPending` | `UnlockPendingTimeoutSeconds` 超时 | `LockedArmed` | 记 Warning，允许重试 |
| 除 `Suspended` 外的任意状态 | 配置中凭据变更（或服务启动时）`LogonUser` 预验证失败 | `Suspended` | 弹通知告知原因；若当前为 `PendingLock`，先撤销未决通知 |
| `Suspended` | 配置中凭据变更且 `LogonUser` 预验证通过 | 按当前屏幕状态回到 `Unlocked` 或 `LockedWaitingDeparture` | — |
| 除 `Suspended` 外的任意状态 | `SessionLock`（任何来源） | `LockedWaitingDeparture` | 撤销未决通知 |
| 除 `Suspended` 外的任意状态 | `SessionUnlock`（任何来源） | `Unlocked` | — |

**`Suspended` 是唯一不响应会话事件的状态**：停摆必须一直持续到配置被修改为止，否则用户手动输密码解锁（产生 `SessionUnlock`）就会把停摆意外清除，密码仍是错的却又开始自动重试。`Suspended` 内部仍记录屏幕当前是锁是解，供恢复时决定回到哪个状态。

锁屏来源不区分：我们自己触发的锁屏同样会产生 `SessionLock` 事件，与用户按 Win+L 走同一条规则，结果一致。

### 6.4 关键细节

1. **凭据只在 `LockedArmed` + `IsNear` 时推送**。CP 连上管道只是阻塞等待，不会一连接就拿到凭据。这与 `TestCredentialProviderApp` 的「监听式」行为不同，新服务不复现该缺陷。
2. **`UnlockPending` 取代冷却 hack**。推送后在超时窗口内不重发，杜绝 CP 重连导致的连环推送（即 `TestCredentialProviderApp` 中 30 秒冷却所要解决的问题）。
3. **启动宽限**。服务启动时若屏幕已锁，初始状态为 `LockedWaitingDeparture`（保守，要求先离开）；若未锁则为 `Unlocked`，但 `StartupGraceSeconds` 内不评估锁屏，避免样本不足即误判无信号。
4. **锁屏来源不区分**。我们触发的锁屏与用户按 Win+L 都进入 `LockedWaitingDeparture`，均要求先离开再回来。
5. **Dialog 模式的局限**。`PendingLock` 期间信号恢复时，Toast 可调 `Hide()` 撤销；而 `WTSSendMessage` 弹出的对话框**无法从外部关闭**，只能等用户点击或自身超时。切到 Dialog 模式时该能力退化，配置注释与 README 中标明。

### 6.5 参数

| 参数 | 默认值 | 说明 |
|------|-------|------|
| `LockThresholdDbm` | -80 | 低于此值判定为远离 |
| `LockWindowSeconds` | 10 | 远离判定时间窗 |
| `UnlockThresholdDbm` | -45 | 高于此值判定为靠近 |
| `UnlockWindowSeconds` | 5 | 靠近判定时间窗 |
| `MinSamples` | 3 | 时间窗内最少有效样本数 |
| `PromptTimeoutSeconds` | 15 | 通知无响应则锁屏 |
| `RejectSnoozeSeconds` | 60 | 点「拒绝」后的静默期 |
| `SnoozeMinutes` | 5 | 点「5 分钟内不锁定」的静默期 |
| `StartupGraceSeconds` | 15 | 启动后不评估锁屏的宽限期 |
| `UnlockPendingTimeoutSeconds` | 20 | 等待认证结果的超时 |

---

## 7. 配置与热更新

### 7.1 文件

位置 `%ProgramData%\BleUnlock\appsettings.json`。安装脚本用 `icacls` 移除继承的 ACL，只授予 SYSTEM 与 Administrators 完全控制。

```json
{
  "BleUnlock": {
    "Device": {
      "Irk": "",
      "IrkByteOrder": "Auto",
      "IdentityAddress": null
    },
    "Credentials": {
      "UserName": "",
      "Password": ""
    },
    "Proximity": {
      "LockThresholdDbm": -80,
      "LockWindowSeconds": 10,
      "UnlockThresholdDbm": -45,
      "UnlockWindowSeconds": 5,
      "MinSamples": 3,
      "StartupGraceSeconds": 15,
      "UnlockPendingTimeoutSeconds": 20
    },
    "Notification": {
      "Mode": "Toast",
      "PromptTimeoutSeconds": 15,
      "RejectSnoozeSeconds": 60,
      "SnoozeMinutes": 5
    }
  }
}
```

`IrkByteOrder` 取值 `Auto` / `Normal` / `Reversed`。`Mode` 取值 `Toast` / `Dialog`。`IdentityAddress` 为可选的身份地址（`AA:BB:CC:DD:EE:FF`），已配对设备可能被 Windows 直接报告身份地址，此时按地址直接匹配。

### 7.2 热更新机制

通过 `IOptionsMonitor<BleUnlockOptions>.OnChange` 监听。需处理 `reloadOnChange` 的两个已知问题：单次保存触发多次回调、以及读到写了一半的文件。对策是 **250 毫秒防抖 + 先校验再应用**。校验失败时**保留内存中的旧配置**继续运行并记 Error——绝不让一个手误的配置把服务搞瘫。

### 7.3 校验规则

| 规则 |
|------|
| `Irk` 去除空格、冒号、`0x` 前缀后必须正好 32 个十六进制字符 |
| `UnlockThresholdDbm` 必须大于 `LockThresholdDbm`（否则死区为负，状态机会来回抖动） |
| 所有窗口与超时必须为正数 |
| `MinSamples` ≥ 1 |
| `UserName` 非空 |
| `IdentityAddress` 若非空，必须是合法的 6 字节 MAC 格式 |

### 7.4 各项生效方式

| 变更 | 生效方式 |
|------|---------|
| 阈值 / 窗口 / 超时 | 下一次评估直接采用新值 |
| `Irk` / `IrkByteOrder` / `IdentityAddress` | 重建 `RpaResolver`，清空地址解析缓存与 RSSI 历史 |
| `UserName` / `Password` | 重跑 `LogonUser` 预验证；通过则**解除停摆**，不通过则进入停摆并告警 |
| `Notification.Mode` | 下次弹通知时切换实现 |

---

## 8. IPC 协议

两条管道均为 UTF-8 行文本。

### 8.1 服务 ↔ 凭据提供程序

管道 `\\.\pipe\BleUnlock`，服务为服务端。DACL 仅允许 SYSTEM 连接（服务与 winlogon 均以 SYSTEM 运行）。

```
服务 → CP    CREDS\n<username>\n<password>\n
CP  → 服务   ACK\n                              收到即回
CP  → 服务   RESULT OK\n                        ReportResult 被调用后回
             RESULT FAIL <ntstatus十六进制>\n
```

**C++ 侧改动：**

1. `CPipeClient` 由「读一次即关闭」改为**保持长连接**：读 `CREDS` → 回 `ACK` → 等待认证结果 → 写 `RESULT …` → 继续等待下一条指令
2. `CBleUnlockCredential::ReportResult` 的回调签名扩展为携带 `NTSTATUS`，经 `CBleUnlockCredentialProvider` 转发给 `CPipeClient` 写回
3. **删除** `CBleUnlockCredentialProvider::_authFailed` 本地标志——停摆决策上移到服务，CP 只负责如实汇报，职责更干净

**必须兜住的风险：** `ReportResult` 不保证一定被调用（LogonUI 可能直接被销毁）。因此 §6.5 的 `UnlockPendingTimeoutSeconds` 是硬性必需的兜底，超时按「结果未知」处理，回到 `LockedArmed` 重试，**不进入停摆**。没收到回执不等于密码错误。

### 8.2 NTSTATUS 停摆策略

| 状态码 | 值 | 处理 |
|--------|---|------|
| `STATUS_LOGON_FAILURE` | `0xC000006D` | 停摆 |
| `STATUS_PASSWORD_EXPIRED` | `0xC0000071` | 停摆 |
| `STATUS_PASSWORD_MUST_CHANGE` | `0xC0000224` | 停摆 |
| `STATUS_ACCOUNT_LOCKED_OUT` | `0xC0000234` | 停摆 |
| 其他失败 | — | 记日志，回 `LockedArmed` 重试 |

### 8.3 服务 ↔ 助手

管道 `\\.\pipe\BleUnlockHelper`，服务为服务端。DACL 为 SYSTEM + 当前登录用户的 SID。

```
服务 → 助手   PROMPT <id> <超时秒> <延期分钟>     弹出 Toast
             CANCEL <id>                        信号恢复，撤销 Toast
             LOCK                               执行 LockWorkStation
             NOTIFY <级别> <消息>                 纯提示，无按钮（用于停摆告知）

助手 → 服务   HELLO <sessionId>
             RESPONSE <id> REJECT|SNOOZE|TIMEOUT|DISMISSED
             LOCKED
```

`DISMISSED` 指用户主动划掉通知但未点任何按钮，服务按超时同等处理（即锁屏）。

### 8.4 助手生命周期

- 当存在活动会话且用户已登录时，服务用 `WTSQueryUserToken` + `CreateProcessAsUser` 拉起助手
- 助手退出或被杀 → 服务 5 秒后重启
- 会话切换或注销 → 回收旧助手；新会话登录后重新拉起
- 锁屏状态下用户会话仍存在，因此 Toast 与锁屏指令照常可用

---

## 9. 错误处理

| 情况 | 处理 |
|------|------|
| 无蓝牙适配器 / 不支持 LE / 蓝牙被关闭 | 记 Error + 事件日志；服务继续运行，状态机收不到样本即按「无信号」走锁屏流程。适配器恢复后自动继续 |
| Watcher 异常停止 | 2 秒后自动重启，最多 3 次；之后降为每 30 秒重试一次，**不放弃** |
| 启动时配置文件缺失或损坏 | Fatal，**拒绝启动**——没有凭据的服务没有意义 |
| 运行中配置损坏或校验失败 | 保留内存中的旧配置继续运行，记 Error |
| 连续 5 分钟 IRK 匹配不到任何地址 | Warning，提示检查 IRK 或设备是否在范围内 |
| 推送凭据时管道无 CP 连接 | Warning，回 `LockedArmed` 重试 |
| **助手连续 3 次启动失败** | **自动降级**到 `WtsMessageNotifier` + `ProcessScreenLocker`，记 Warning。功能继续可用而非瘫痪 |
| `LogonUser` 预验证失败 | 进入停摆 + Error + 事件日志 + **弹通知告知用户** |
| 认证失败 | 按 §8.2 决定停摆或重试 |
| 服务进程崩溃 | `sc failure` 配置 SCM 自动重启 |

**停摆必须主动告知。** 进入 `Suspended` 时弹出一条纯提示通知（如「密码验证失败，自动解锁已暂停，请修改配置」），否则用户只会感觉功能突然失灵，还需翻日志才知原因。

---

## 10. 日志

Serilog 三个 sink：

| Sink | 配置 |
|------|------|
| File | `%ProgramData%\BleUnlock\logs\bleunlock-.log`，按天滚动，保留 7 天 |
| EventLog | 源名 `BleUnlock`，仅 Warning 及以上 |
| Console | 仅 Debug 构建 |

**密码绝不入日志。** 配置重载日志只打印发生变更的**字段名**而非值；凭据相关日志只记用户名。此条列为代码审查要点。

| 级别 | 内容 |
|------|------|
| Information | 状态机转换（附人话原因，如「10 秒内平均 -83.2 dBm（14 个读数）< -80」）、锁屏与解锁、通知弹出与用户选择、配置重载、**每 30 秒一次的运行摘要** |
| Warning | 适配器问题、助手降级、推送时无 CP 连接、各类重试 |
| Error | 配置校验失败、认证失败、预验证失败 |
| Fatal | 启动致命错误 |

**周期性摘要**每 30 秒输出一次「当前窗口平均 dBm / 样本数 / 状态机状态」。调阈值时无需开 Debug 刷屏即可判断该如何调整 `UnlockThresholdDbm` 与 `LockThresholdDbm`。

---

## 11. 测试

`tests/BleUnlockService.Tests/`，xUnit。时间全部通过 .NET 内置的 `TimeProvider` 注入，测试使用 `FakeTimeProvider`，**不睡真实时间**。

### 11.1 自动化测试

**`RpaResolverTests`**（移植并扩充）
- 蓝牙核心规范 Vol 3 Part H 附录 D.7 官方测试向量：IRK `ec0234a357c8ad05341010a60a397d9b`，prand `708194`，hash 应为 `0dfbaa`
- 同一 IRK 构造的 RPA，`Normal` 与 `Reversed` 两种字节序都应能匹配
- IRK 错一位即不匹配
- 非 RPA 地址（公有地址、静态随机地址）返回 null

**`DeviceMatcherTests`**
- 配置了 `IdentityAddress` 时按地址直接命中，不经 RPA 解析
- 未配置 `IdentityAddress` 时回落到 `RpaResolver`
- 两者都不中则判定为非目标设备

**`ProximityStateMachineTests`**（本项目最核心的测试）
- 无信号满窗 → `PendingLock`
- `PendingLock` 期间信号恢复 → 撤销通知回到 `Unlocked`
- 选「拒绝」→ `Snoozed` 60 秒 → 期满回 `Unlocked`
- 选「5 分钟内不锁定」→ `Snoozed` 300 秒
- 通知超时及 `DISMISSED` → 锁屏 → `LockedWaitingDeparture`
- **`LockedWaitingDeparture` 下即使 `IsNear` 也不推送凭据**（「先离开再回来」的核心保证）
- `IsFar` → `LockedArmed`，再 `IsNear` → 推送凭据恰好一次
- `UnlockPending` 期间不重复推送；超时回 `LockedArmed`
- `RESULT FAIL 0xC000006D` → `Suspended`；其他失败码 → 回 `LockedArmed` 重试
- `Suspended` 下配置变更且预验证通过 → 恢复
- 启动宽限期内不锁屏
- 启动时屏幕已锁 → 初始为 `LockedWaitingDeparture`

**`OptionsValidatorTests`**：IRK 格式、阈值反转、边界值、非法 MAC

**`NtStatusPolicyTests`**：各状态码是否触发停摆

### 11.2 实机验收清单

BLE、管道与 Win32 调用不写自动化测试，由以下清单人工验收：

1. 戴设备走开 → 通知弹出 → 不动 → 15 秒后锁屏
2. 通知弹出后点「拒绝」→ 不锁屏，且 60 秒内不再弹
3. 通知弹出后点「5 分钟内不锁定」→ 5 分钟内不锁屏
4. 通知弹出后走回来 → 通知自动消失，不锁屏
5. 锁屏后原地不动 → **不解锁**
6. 锁屏后走远再回来 → 自动解锁
7. 故意填错密码 → 解锁失败 → 停摆并弹通知 → 改回正确密码 → 自动恢复
8. 关闭蓝牙 → 按无信号走锁屏流程
9. 任务管理器杀助手 → 自动重启；连杀 3 次 → 降级到 Dialog 模式
10. 修改 appsettings 阈值 → 日志显示重载，无需重启即生效

---

## 12. 部署

`scripts/install.ps1`（需管理员权限，**幂等**，重复执行安全）：

1. 创建 `%ProgramData%\BleUnlock\` 与 `logs\` 子目录
2. 拷贝 `BleUnlockService`、`BleUnlockHelper`、`BleUnlockCP.dll` 到 `C:\Program Files\BleUnlock\`
3. 若 appsettings.json 不存在则生成模板
4. `icacls` 移除继承，只授予 SYSTEM 与 Administrators 完全控制
5. 创建指向助手的开始菜单快捷方式并写入 AppUserModelID（**Toast 的硬性前提**）
6. `regsvr32` 注册 CP DLL
7. `sc.exe create` 建立服务（自动延迟启动）+ `sc failure` 配置崩溃自动重启 + `sc start`
8. 注册 Windows 事件日志源 `BleUnlock`

`scripts/uninstall.ps1` 逆序执行，并询问是否保留配置与日志。

---

## 13. 安全考量

- 配置文件含**明文密码**，靠 ACL（SYSTEM + Administrators）防护。这是本期明确接受的折中，加密留待功能完全可用后处理
- 两条管道均设置严格 DACL：CP 管道仅 SYSTEM；助手管道为 SYSTEM + 当前登录用户 SID
- 凭据在 CP 侧使用后立即 `SecureZeroMemory` 清零（现有实现已具备，长连接改造后需确认清零时机仍正确）
- 密码永不写入任何日志
- 自动解锁本质上把「持有设备」等同于「本人在场」。任何人拿到该设备并靠近电脑即可解锁，这是本功能的固有性质

---

## 14. 已知风险

1. **BLE 在 Session 0 可能收不到广播。** `BluetoothLEAdvertisementWatcher` 在 LocalSystem 服务中的可用性未经本机验证，业界报告不一致。本期决定不先做可行性验证直接实现。缓解措施：扫描封装在 `IRssiSource` 接口之后，若验证失败，把实现挪进已存在的助手进程即可，状态机与解锁链路无需改动
2. **蓝牙故障会造成持续骚扰。** 用户明确选择「完全一视同仁」，故蓝牙适配器损坏时会反复「弹通知 → 锁屏 → 手动解锁 → 静默期满 → 再弹再锁」。可用「5 分钟内不锁定」缓解，或临时停止服务
3. **专注助手（勿扰模式）会静默吞掉 Toast。** 届时电脑会在无可见提示的情况下锁屏。缓解措施：把 `Notification.Mode` 切为 `Dialog`，`WTSSendMessage` 不受勿扰模式影响
4. **Apple Watch 熄屏后广播频率下降**，可能导致窗口内样本不足而误判无信号。调整 `MinSamples` 与 `LockWindowSeconds` 应对
5. **已配对设备可能被 Windows 直接报告身份地址**，导致 IRK 匹配失效。对策：支持 `IdentityAddress` 直接匹配
6. **RSSI 受遮挡与姿势影响很大**，阈值需按实际环境调整，参考项目可用于观察实时数值

---

## 15. 本期不做

- 开机登录界面的自动登录（仅做已登录会话的锁屏解锁）
- 多设备、多 IRK
- 凭据加密存储（本期明文 + ACL）
- 距离估算与 RSSI 曲线显示（参考项目已具备，需要时直接用它）
- 从注册表或配对信息中自动提取 IRK
- 多用户同时登录场景（只服务当前活动的控制台会话）
