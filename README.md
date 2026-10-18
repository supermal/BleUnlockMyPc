# AutoUnlockMyPc

基于 BLE RSSI 的 Windows 11 近距离自动锁屏与解锁。服务通过蓝牙扫描已配对设备的信号强度，判断用户远离还是靠近：远离自动锁屏，靠近自动解锁。

四个组件：

- **服务（AutoUnlockService.exe）**：以 Windows 服务运行，负责 BLE 扫描与 RSSI 状态机判定，执行锁屏，并向凭据提供程序推送凭据。
- **助手（AutoUnlockHelper.exe）**：在交互会话中运行，负责弹 Toast 提示、执行锁屏（LockWorkStation），并把用户选择与执行结果回传服务。
- **凭据提供程序（AutoUnlockCredentialProvider.dll，安装后复制为 AutoUnlockCP.dll）**：位于登录界面，接收服务推送的凭据完成登录。
- **管理端（AutoUnlockManager.exe）**：WinUI 3 托盘常驻程序「AutoUnlock 管理器」，提供实时信号概览、配置编辑、服务日志查看与亮/暗主题；由安装脚本注册为计划任务，随登录自动启动（最高权限运行，无 UAC 弹窗）。

## 前提条件

- Windows 11 x64
- 支持 BLE 的蓝牙适配器
- .NET 10 运行时（x64）——服务与助手的 runtimeconfig 都只需 `Microsoft.NETCore.App`，不需要 Desktop Runtime
- 管理端为自包含发布（win-x64），无需单独安装 .NET 运行时或 Windows App SDK
- VC++ 2022 运行库（凭据提供程序工具集 v145）

安装与查看日志需要管理员权限。

安装与卸载脚本的目标环境是 Windows PowerShell 5.1（系统自带）；未在 PowerShell 7 上验证（其默认不提供 New-EventLog）。

## 构建

逐项目构建（**不要用 `AutoUnlockMyPc.sln` 整体构建**——C++ 项目经 dotnet CLI 会报 MSB4278，需用 VS 2022 的 MSBuild）：

```powershell
dotnet build src\AutoUnlockService\AutoUnlockService.csproj -c Release
dotnet build src\AutoUnlockHelper\AutoUnlockHelper.csproj -c Release
msbuild src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj /p:Configuration=Release /p:Platform=x64
```

`msbuild` 指 Visual Studio 2022 自带的 MSBuild。

管理端为自包含发布（**发布前先退出正在运行的管理端**，否则文件被占用）：

```powershell
dotnet publish src\AutoUnlockManager\AutoUnlockManager.csproj -c Release -r win-x64 --self-contained true
```

产物目录 `src\AutoUnlockManager\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`；安装脚本会自动探测该目录（多个命中取最新）。

可选，运行单元测试：

```powershell
dotnet test tests\AutoUnlockService.Tests\AutoUnlockService.Tests.csproj
dotnet test tests\AutoUnlockManager.Tests\AutoUnlockManager.Tests.csproj
```

## 安装

用管理员 PowerShell 运行：

```powershell
.\scripts\install.ps1
```

脚本是幂等的，可重复运行用于升级；升级只覆盖同名文件，上一版本已删除或改名的文件会残留在安装目录（通常不影响运行；管理端子目录 `Manager` 例外——每次整体替换）。参数：

- `-Configuration`（默认 `Release`）：构建配置，决定从哪个产物目录部署。
- `-SourceDir`（默认空）：手动指定服务输出目录，跳过产物自动探测。
- `-ManagerSourceDir`（默认空）：手动指定管理端发布目录（含 `AutoUnlockManager.exe`），跳过自动探测。
- `-InstallDir`（默认 `C:\Program Files\AutoUnlock`）：程序安装目录；管理端部署在其 `Manager` 子目录。
- `-DataDir`（默认 `%ProgramData%\AutoUnlock`）：配置与日志目录。
- `-ManagerUser`（默认空）：管理端计划任务的运行账户（`域\用户名` 或本地账户）；默认=执行本安装的账户。

脚本动作概述：停止并删除旧服务 → 结束正在运行的管理端 → 部署服务、助手与凭据提供程序（服务与助手必须同目录：服务按安装目录查找 `AutoUnlockHelper.exe`）→ 生成或保留配置 → 用 `icacls` 收紧配置目录权限（仅 SYSTEM 与 Administrators；含对既有文件的修复与回读断言）→ 用 `regsvr32` 注册凭据提供程序 → 注册事件日志源 `AutoUnlock` → 创建服务（延迟自动启动、失败自动重启）→ 部署管理端到 `Manager` 子目录 → 创建开始菜单快捷方式 → 注册计划任务 `AutoUnlockManager`（登录自启、最高权限）→ 立即启动管理端。

**配置未填写完整时脚本不会启动服务**。按提示编辑 `%ProgramData%\AutoUnlock\appsettings.json`（填入 `Device.Irk`、`Credentials.UserName` 与 `Password`），然后执行：

```powershell
Start-Service AutoUnlockService
```

日志目录：`%ProgramData%\AutoUnlock\logs\`（权限收紧后普通用户读不了，需管理员）。

## 配置

配置文件：`%ProgramData%\AutoUnlock\appsettings.json`（安装时若不存在则由脚本从模板生成，已存在则保留）。

| 参数 | 默认值 | 说明 |
|------|-------|------|
| `LockThresholdDbm` | -80 | 低于此值视为离开（触发读数不计入批次） |
| `UnlockThresholdDbm` | -45 | 高于此值视为靠近（触发读数不计入批次） |
| `UnlockSampleCount` | 3 | 解锁确认个数：触发后再收 N 个高于解锁阈值的读数 |
| `LockPromptSampleCount` | 5 | 弹窗触发个数：触发后再收 N 个低于锁定阈值的读数（静默每秒按 1 条计入） |
| `LockConfirmSampleCount` | 10 | 锁屏确认个数：弹窗后新收 N 个读数低于锁定阈值 |
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
      "UnlockThresholdDbm": -45,
      "UnlockSampleCount": 3,
      "LockPromptSampleCount": 5,
      "LockConfirmSampleCount": 10,
      "StartupGraceSeconds": 15,
      "UnlockPendingTimeoutSeconds": 20
    },
    "Notification": {
      "Mode": "Toast",
      "RejectSnoozeSeconds": 60,
      "SnoozeMinutes": 5
    }
  }
}
```

生效方式：

- 阈值 / 批次个数 / 超时：下一次评估直接采用新值。旧键（`LockWindowSeconds`、`UnlockWindowSeconds`、`MinSamples`、`PromptTimeoutSeconds`）留在文件里但不再生效；管理端保存时既不写回也不删除它们。
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

## 管理端

「AutoUnlock 管理器」（开始菜单快捷方式；托盘常驻）页面：

- **概览**：实时 dBm 与曲线、服务健康、启动/停止/重启服务、样本 CSV 导出
- **配置**：`appsettings.json` 四组字段的表单编辑（校验、原子保存），保存后确认服务热重载
- **日志**：服务日志实时跟随（暂停/继续、级别与关键字过滤、WRN/ERR 高亮、导出、打开日志目录）
- **设置**：主题（跟随系统 / 浅色 / 深色）

管理端由计划任务以最高权限启动（读取日志目录需要管理员权限）；手动双击 exe 会弹 UAC 提权，属正常现象。

## 卸载

用管理员 PowerShell 运行：

```powershell
.\scripts\uninstall.ps1
```

默认会交互询问是否删除配置与日志（输入 `yes` 确认）；加 `-KeepData` 跳过删除、只移除服务与程序文件。管理端会被一并结束，计划任务与开始菜单快捷方式随之注销；其界面设置（`%LocalAppData%\AutoUnlockManager`）保留。卸载后建议重启一次，让 winlogon 释放凭据提供程序 DLL。

## 排障

- 服务起不来：先看 `%ProgramData%\AutoUnlock\logs\` 的日志（找 `[FTL]` 行），以及事件查看器「Windows 日志 → 应用程序」中源为 `AutoUnlock` 的事件。
- 配置错误：服务拒绝启动（退出码 1），日志给出具体校验错误（`[FTL] 配置无效，服务拒绝启动：…`）。
