# AutoUnlockMyPc

基于 BLE RSSI 的 Windows 11 近距离自动锁屏与解锁。服务通过蓝牙扫描已配对设备的信号强度，判断用户远离还是靠近：远离自动锁屏，靠近自动解锁。

三个组件：

- **服务（AutoUnlockService.exe）**：以 Windows 服务运行，负责 BLE 扫描与 RSSI 状态机判定，执行锁屏，并向凭据提供程序推送凭据。
- **助手（AutoUnlockHelper.exe）**：在交互会话中运行，负责 Toast / 对话框提示与兜底锁屏。
- **凭据提供程序（AutoUnlockCredentialProvider.dll，安装后复制为 AutoUnlockCP.dll）**：位于登录界面，接收服务推送的凭据完成登录。

## 前提条件

- Windows 11 x64
- 支持 BLE 的蓝牙适配器
- .NET 10 运行时（x64）——服务与助手的 runtimeconfig 都只需 `Microsoft.NETCore.App`，不需要 Desktop Runtime
- VC++ 2022 运行库（凭据提供程序工具集 v145）

安装与查看日志需要管理员权限。

## 构建

逐项目构建（**不要用 `AutoUnlockMyPc.sln` 整体构建**——C++ 项目经 dotnet CLI 会报 MSB4278，需用 VS 2022 的 MSBuild）：

```powershell
dotnet build src\AutoUnlockService\AutoUnlockService.csproj -c Release
dotnet build src\AutoUnlockHelper\AutoUnlockHelper.csproj -c Release
msbuild src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj /p:Configuration=Release /p:Platform=x64
```

`msbuild` 指 Visual Studio 2022 自带的 MSBuild。

可选，运行单元测试：

```powershell
dotnet test tests\AutoUnlockService.Tests\AutoUnlockService.Tests.csproj
```

## 安装

用管理员 PowerShell 运行：

```powershell
.\scripts\install.ps1
```

脚本是幂等的，可重复运行用于升级。参数：

- `-Configuration`（默认 `Release`）：构建配置，决定从哪个产物目录部署。
- `-SourceDir`（默认空）：手动指定服务输出目录，跳过产物自动探测。
- `-InstallDir`（默认 `C:\Program Files\AutoUnlock`）：程序安装目录。
- `-DataDir`（默认 `%ProgramData%\AutoUnlock`）：配置与日志目录。

脚本动作概述：停止并删除旧服务 → 部署服务、助手与凭据提供程序（服务与助手必须同目录：服务按安装目录查找 `AutoUnlockHelper.exe`）→ 生成或保留配置 → 用 `icacls` 收紧配置目录权限（仅 SYSTEM 与 Administrators）→ 用 `regsvr32` 注册凭据提供程序 → 注册事件日志源 `AutoUnlock` → 创建服务（延迟自动启动、失败自动重启）。

**配置未填写完整时脚本不会启动服务**。按提示编辑 `%ProgramData%\AutoUnlock\appsettings.json`（填入 `Device.Irk`、`Credentials.UserName` 与 `Password`），然后执行：

```powershell
Start-Service AutoUnlockService
```

日志目录：`%ProgramData%\AutoUnlock\logs\`（权限收紧后普通用户读不了，需管理员）。

## 配置

配置文件：`%ProgramData%\AutoUnlock\appsettings.json`（安装时若不存在则由脚本从模板生成，已存在则保留）。

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

`IrkByteOrder` 取 `Auto` / `Normal` / `Reversed`；`Mode` 取 `Toast` / `Dialog`；`IdentityAddress` 可选——已配对设备可能被 Windows 直接报告身份地址，此时按地址匹配。

完整示例（与设计文档 §7.1 一致）：

```json
{
  "AutoUnlock": {
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

生效方式：

- 阈值 / 窗口 / 超时：下一次评估直接采用新值。
- `Irk` / `IrkByteOrder` / `IdentityAddress`：变更会重建解析缓存并清空 RSSI 历史。
- 凭据（`UserName` / `Password`）：变更会重新预验证，通过则解除停摆。
- `Notification.Mode`：在服务启动时读取，修改后需重启服务生效。
- 配置无效时服务拒绝启动（退出码 1，日志记 `[FTL] 配置无效，服务拒绝启动：…`，事件源 `AutoUnlock`）。

## 获取 IRK 与调阈值

设备 IRK 需自行从目标设备获取（本项目不做自动提取）。推荐使用参考项目 `BleRssiMonitor`——与本案同机开发的独立 WinForms 工具：扫描并列出 BLE 设备、观察实时 RSSI、编辑与验证 IRK、阈值校准；具体操作按其界面与自带文档进行。

调阈值时也可看服务日志的 30 秒「摘要」行：状态 / 读数 / 平均 dBm / 距上次匹配秒数。

## 通知模式

默认使用 Toast。专注助手（勿扰）可能静默吞掉 Toast，导致无提示直接锁屏；此场景把 `Mode` 切为 `Dialog`（`WTSSendMessage` 不受勿扰影响）。

助手首次弹出 Toast 时会自动注册通知身份（注册表绑定 exe 路径）。**不要随意更改安装目录**，否则通知身份会变化。

## 卸载

用管理员 PowerShell 运行：

```powershell
.\scripts\uninstall.ps1
```

默认会交互询问是否删除配置与日志（输入 `yes` 确认）；加 `-KeepData` 跳过删除、只移除服务与程序文件。卸载后建议重启一次，让 winlogon 释放凭据提供程序 DLL。

## 排障

- 服务起不来：先看 `%ProgramData%\AutoUnlock\logs\` 的日志（找 `[FTL]` 行），以及事件查看器「Windows 日志 → 应用程序」中源为 `AutoUnlock` 的事件。
- 配置错误：服务拒绝启动（退出码 1），日志给出具体校验错误（`[FTL] 配置无效，服务拒绝启动：…`）。
