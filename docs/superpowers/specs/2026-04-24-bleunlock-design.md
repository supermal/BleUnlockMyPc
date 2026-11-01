# BleUnlockMyPc — 设计文档

**日期：** 2026-04-24  
**状态：** 已批准  
**目标：** 基于 DX-BT401 蓝牙模块 RSSI 实现 Windows 11 自动近距离解锁/锁定

---

## 1. 系统概述

当用户携带配对的蓝牙设备靠近电脑时自动解锁 Windows，远离时自动锁定。系统分为两个独立进程：

- **BleUnlockService**：C# Windows Service，运行于 Session 0，负责 RSSI 采集、状态判断、触发锁定/解锁
- **BleUnlockCredentialProvider**：C++ COM DLL，由 WinLogon 加载，负责在锁屏界面完成凭据提交

两者通过 Named Pipe `\\.\pipe\BleUnlock` 通信。

---

## 2. 项目结构

```
BleUnlockMyPc/
├── BleUnlockMyPc.sln
├── README.md
└── src/
    ├── BleUnlockService/
    │   ├── BleUnlockService.csproj
    │   ├── Program.cs
    │   ├── Worker.cs
    │   ├── RssiMonitor.cs
    │   ├── RssiSmoother.cs
    │   ├── ProximityStateMachine.cs
    │   ├── PipeServer.cs
    │   ├── CredentialStore.cs
    │   └── appsettings.json
    └── BleUnlockCredentialProvider/
        ├── BleUnlockCredentialProvider.vcxproj
        ├── dllmain.cpp
        ├── ClassFactory.cpp / ClassFactory.h
        ├── CredentialProvider.cpp / CredentialProvider.h
        ├── Credential.cpp / Credential.h
        ├── PipeClient.cpp / PipeClient.h
        ├── Serialization.cpp / Serialization.h
        ├── guids.h
        └── register.reg
```

---

## 3. 架构与数据流

```
┌─────────────────────────────────────────────────────────┐
│                    Windows Session 0                     │
│                                                         │
│  ┌─────────────────────────────────────────────────┐   │
│  │           BleUnlock Windows Service (C#)        │   │
│  │                                                  │   │
│  │  SerialPort → RssiParser → SmoothingBuffer      │   │
│  │       ↓                                          │   │
│  │  StateMachine (Locked/Unlocked)                 │   │
│  │       ↓ 远离            ↓ 靠近                   │   │
│  │  LockWorkStation()   PipeServer.SendCreds()     │   │
│  │                           ↓                      │   │
│  │       ┌────[Named Pipe: \\.\\pipe\\BleUnlock]───┤   │
│  └───────┼──────────────────────────────────────────┘   │
└──────────┼──────────────────────────────────────────────┘
           │
┌──────────┼──────────────────────────────────────────────┐
│          │         WinLogon / Credential UI              │
│  ┌───────┴────────────────────────────────────────┐    │
│  │      BleUnlockCredentialProvider (C++ COM)     │    │
│  │  PipeClient (后台线程) → 收到 user\npass        │    │
│  │  → CredentialsChanged() → GetSerialization()   │    │
│  │  → LSA 验证 → 解锁桌面                          │    │
│  └────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

---

## 4. RSSI 状态机与防抖

### 4.1 滑动平均平滑

采集到的原始 RSSI 值进入大小为 N 的循环缓冲区，输出移动平均值。

### 4.2 滞后阈值（Hysteresis）

| 条件 | 判定 |
|------|------|
| 平滑值 > NearThreshold (-60) | NEAR |
| 平滑值 < FarThreshold (-80) | FAR |
| -80 ≤ 平滑值 ≤ -60 | 保持当前状态（死区） |

### 4.3 状态计数防抖

- 连续 `UnlockConfirmCount`（默认 3）次 NEAR → 触发解锁
- 连续 `LockConfirmCount`（默认 5）次 FAR → 触发锁定
- 解锁方向更激进（3次），锁定方向更保守（5次），防止误锁

### 4.4 状态机转换

```
         连续NEAR≥3
Locked ──────────────► Unlocked
  ▲                        │
  └──────────────────────── ┘
         连续FAR≥5
```

---

## 5. 可配置参数（appsettings.json）

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `ComPort` | `COM3` | DX-BT401 串口号 |
| `BaudRate` | `9600` | 串口波特率 |
| `NearThreshold` | `-60` | 靠近判定 dBm |
| `FarThreshold` | `-80` | 远离判定 dBm |
| `SmoothingWindow` | `5` | 滑动平均窗口大小 |
| `UnlockConfirmCount` | `3` | 解锁连续确认次数 |
| `LockConfirmCount` | `5` | 锁定连续确认次数 |
| `CredentialTarget` | `BleUnlock` | Credential Manager 目标名 |

---

## 6. IPC 协议

**Pipe 名称：** `\\.\pipe\BleUnlock`  
**方向：** Service（Server）→ Credential Provider（Client）  
**安全描述符：** 仅允许 SYSTEM 身份进程连接（Service 和 WinLogon 均以 SYSTEM 运行）

**消息格式：**
```
username\npassword\n
```

**响应：**
```
OK\n
```

连接在解锁触发后建立，发送完成后保持连接以备复用。CP 重启后重新连接。

---

## 7. Credential Provider 流程

```
CP 加载（WinLogon）
  → 后台线程循环连接 \\.\pipe\BleUnlock
  → 收到 "username\npassword\n"
  → 存入内存（不落盘）
  → _pcpEvents->CredentialsChanged()
  → LogonUI 调用 GetCredentialCount() → 1
  → LogonUI 调用 SetSelected(0) → AutoLogon() → TRUE
  → LogonUI 调用 GetSerialization()
      ├─ 本地账户 → MSV1_0_INTERACTIVE_LOGON
      └─ 域账户   → KERB_INTERACTIVE_UNLOCK_LOGON
  → 提交 LSA → 验证通过 → 解锁
  → 凭据内存清零（SecureZeroMemory）
```

---

## 8. 凭据存储

使用 Windows Credential Manager（`CRED_TYPE_GENERIC`，`CRED_PERSIST_LOCAL_MACHINE`）：

**初始化（一次性）：**
```
cmdkey /add:BleUnlock /user:DOMAIN\username /pass:yourpassword
```

**Service 读取：** 调用 `CredRead("BleUnlock", CRED_TYPE_GENERIC, ...)` 获取用户名和密码，通过 Named Pipe 发送给 CP。

---

## 9. 账户类型支持

| 账户类型 | GetSerialization 使用的认证包 | 判断方式 |
|----------|-------------------------------|----------|
| 本地账户 | `MSV1_0_INTERACTIVE_LOGON` | 用户名不含 `\` 或 `.` |
| 域账户 | `KERB_INTERACTIVE_UNLOCK_LOGON` | 用户名含 `DOMAIN\user` |

---

## 10. 安全考量

- Named Pipe 使用严格 DACL，防止第三方进程读取凭据
- 凭据使用后立即 `SecureZeroMemory` 清零
- Credential Provider UI 完全隐藏（`CPFS_HIDDEN`），不暴露字段
- Windows Credential Manager 由 DPAPI 加密，绑定到本机

---

## 11. 构建要求

| 项目 | 工具链 | 目标 |
|------|--------|------|
| BleUnlockService | .NET 8 SDK | `dotnet publish -c Release -r win-x64` |
| BleUnlockCredentialProvider | Visual Studio 2022, C++17, Windows SDK | x64 Release → `BleUnlockCP.dll` |

---

## 12. 部署步骤（概要，详见 README）

1. `cmdkey` 注册凭据到 Credential Manager
2. `sc create` 安装并启动 Service
3. `regsvr32` 注册 COM DLL
4. 导入 `register.reg` 添加 Credential Provider 注册表项
5. 重启或重新锁屏测试
