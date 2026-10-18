# AutoUnlockService Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个 Windows 服务，用随身 BLE 设备（靠 IRK 识别其轮换地址）的 RSSI 判断用户是否在场，人走远则弹通知后锁屏，人回来则通过已有的 C++ 凭据提供程序自动解锁。

**Architecture:** 三进程。`AutoUnlockService`（LocalSystem，Session 0）做 BLE 扫描、IRK 解析、接近状态机、凭据管理与两条命名管道的服务端；`AutoUnlockHelper`（用户会话）弹 Toast 通知并执行锁屏；现有 `AutoUnlockCP.dll`（winlogon 加载）改为长连接，提交凭据后回传认证结果 NTSTATUS。通知与锁屏在服务侧抽象为 `INotifier` / `IScreenLocker`，各有「经助手」与「不经助手」两套实现，助手连续启动失败时自动降级。

**Tech Stack:** .NET 10（`net10.0-windows10.0.19041.0`）、WinRT `BluetoothLEAdvertisementWatcher`、Serilog（File + EventLog）、`CommunityToolkit.WinUI.Notifications`、xUnit + `Microsoft.Extensions.TimeProvider.Testing`、C++17 / Windows SDK（CP）、PowerShell（安装脚本）。

**Spec:** `docs/superpowers/specs/2026-10-13-autounlock-service-design.md`

## Global Constraints

- 目标框架 `net10.0-windows10.0.19041.0`（WinRT 蓝牙 API 要求），服务与助手都用这个 TFM
- C++ CP：Visual Studio 2022、C++17、x64、Release
- 代码标识符用英文；**代码注释用西班牙语**；文档与日志面向用户的文案用中文
- **密码绝不写入任何日志。** 配置变更日志只打印发生变更的字段名，不打印值；凭据相关日志只记用户名
- 时间一律通过 .NET 内置 `TimeProvider` 注入，测试用 `FakeTimeProvider`，**测试中不得 `Thread.Sleep` 或 `Task.Delay` 真实时间**
- 配置文件位置 `%ProgramData%\AutoUnlock\appsettings.json`，日志 `%ProgramData%\AutoUnlock\logs\autounlock-.log`
- 两条管道：`\\.\pipe\AutoUnlock`（服务 ↔ CP）、`\\.\pipe\AutoUnlockHelper`（服务 ↔ 助手），均为 UTF-8 行文本
- 阈值默认值：`LockThresholdDbm` -80、`LockWindowSeconds` 10、`UnlockThresholdDbm` -45、`UnlockWindowSeconds` 5、`MinSamples` 3、`PromptTimeoutSeconds` 15、`RejectSnoozeSeconds` 60、`SnoozeMinutes` 5、`StartupGraceSeconds` 15、`UnlockPendingTimeoutSeconds` 20
- 停摆状态码：`0xC000006D`、`0xC0000071`、`0xC0000224`、`0xC0000234`

## Review Focus

规格隐含、但若不专门写测试就会漏掉的五类输入与故障，按「最可能咬到真实用户」排序。每一条都已落到下面具体任务的测试步骤里：

1. **休眠唤醒与系统校时导致的时钟跳变**（Task 6、Task 8）——窗口判定基于时间戳，笔记本合盖再打开后 `now` 可能前跳数小时，全部历史样本瞬间"过期"，`Count` 变 0 即判定无信号，用户一开盖就被弹通知锁屏。唤醒必须清空历史并重新进入启动宽限期。
2. **运行中配置被改坏导致空凭据被推送**（Task 9）——`IOptionsMonitor` 在文件变成非法 JSON 时可能给出一份全默认值的对象（`UserName` 与 `Password` 都是空串）。若不拦住，服务会拿着空凭据去解锁，必定失败且可能触发账户锁定策略。校验失败必须保留内存中的旧配置。
3. **配好 IRK 但设备从未出现**（Task 7）——新装完服务、手表还没戴上时，窗口内样本数为 0 即判定为「远」，宽限期一过就弹通知、15 秒后锁屏。用户对本软件的第一印象就是被锁出去。必须区分「从未见过目标设备」与「见过但信号消失」。
4. **BLE 广播时间戳乱序或重复到达**（Task 6）——WinRT 回调不保证按时间戳顺序触达。若 `RssiHistory` 假设单调递增来做队列裁剪，乱序样本会被错误丢弃或让窗口统计算错，阈值判定随之失准。
5. **助手在 `PendingLock` 倒计时途中崩溃**（Task 15）——Toast 随进程消失，但服务仍在等 `RESPONSE`，直到 `PromptTimeoutSeconds` 耗尽才锁屏，用户全程没看到任何提示就被锁。助手断连必须立刻中断等待、降级到对话框，并**用剩余时间重新提示一次**，而不是静默等到超时。

---

## File Structure

| 文件 | 职责 |
|------|------|
| `src/AutoUnlockService/AutoUnlockService.csproj` | 服务项目，Worker Service + Serilog |
| `src/AutoUnlockService/Program.cs` | 宿主构建、Serilog 配置、DI 注册 |
| `src/AutoUnlockService/Worker.cs` | 编排：样本流 → 状态机 → 副作用分派 |
| `src/AutoUnlockService/Config/AutoUnlockOptions.cs` | 配置模型（Device / Credentials / Proximity / Notification） |
| `src/AutoUnlockService/Config/OptionsValidator.cs` | 配置校验与 IRK 规范化 |
| `src/AutoUnlockService/Config/OptionsGate.cs` | 热更新防抖 + 校验失败保留旧值 |
| `src/AutoUnlockService/Ble/AdvertisementSample.cs` | 一条广播的不可变记录 |
| `src/AutoUnlockService/Ble/IRssiSource.cs` | 样本源抽象（将来可换实现） |
| `src/AutoUnlockService/Ble/BleScanner.cs` | WinRT Watcher 封装 + 适配器检查 + 重启 |
| `src/AutoUnlockService/Core/BleAddress.cs` | 48 位地址解析与类型判断 |
| `src/AutoUnlockService/Core/RpaResolver.cs` | IRK 解析 RPA（纯算法） |
| `src/AutoUnlockService/Core/DeviceMatcher.cs` | 身份地址优先、RPA 回落的目标设备判定 |
| `src/AutoUnlockService/Core/RssiHistory.cs` | 时间窗样本队列与窗口统计 |
| `src/AutoUnlockService/Core/ProximityStateMachine.cs` | 七状态接近状态机 |
| `src/AutoUnlockService/Credentials/NtStatusPolicy.cs` | 哪些 NTSTATUS 触发停摆 |
| `src/AutoUnlockService/Credentials/CredentialValidator.cs` | `LogonUser` 预验证，三种账户格式 |
| `src/AutoUnlockService/Credentials/CredentialPipeServer.cs` | `\\.\pipe\AutoUnlock` 服务端 |
| `src/AutoUnlockService/Notifications/INotifier.cs` | 通知抽象 |
| `src/AutoUnlockService/Notifications/HelperNotifier.cs` | 经助手弹 Toast |
| `src/AutoUnlockService/Notifications/WtsMessageNotifier.cs` | `WTSSendMessageW` 对话框 |
| `src/AutoUnlockService/Session/IScreenLocker.cs` | 锁屏抽象 |
| `src/AutoUnlockService/Session/HelperScreenLocker.cs` | 指令助手锁屏 |
| `src/AutoUnlockService/Session/ProcessScreenLocker.cs` | `CreateProcessAsUser` 跑 rundll32 |
| `src/AutoUnlockService/Session/HelperPipeServer.cs` | `\\.\pipe\AutoUnlockHelper` 服务端 |
| `src/AutoUnlockService/Session/HelperLauncher.cs` | 助手拉起、守护、降级 |
| `src/AutoUnlockService/Session/SessionMonitor.cs` | 会话锁定/解锁与电源恢复事件 |
| `src/AutoUnlockHelper/Program.cs` | 助手主循环：连管道、收指令、回结果 |
| `src/AutoUnlockHelper/ToastPrompt.cs` | Toast 弹出、撤销、按钮回调 |
| `src/AutoUnlockHelper/ScreenLock.cs` | `LockWorkStation` P/Invoke |
| `src/AutoUnlockCredentialProvider/PipeClient.{h,cpp}` | 改长连接 + 回传 RESULT |
| `src/AutoUnlockCredentialProvider/Credential.{h,cpp}` | 回调带 NTSTATUS |
| `src/AutoUnlockCredentialProvider/CredentialProvider.{h,cpp}` | 删 `_authFailed`，转发结果 |
| `scripts/install.ps1` / `uninstall.ps1` | 安装、ACL、AUMID 快捷方式、注册 CP、建服务 |
| `tests/AutoUnlockService.Tests/*` | xUnit 测试 |

---

### Task 1: 项目骨架与可安装的空服务

把一个能装、能启动、能写日志的服务跑起来。这是后续所有实机验证的地基，必须先立住。

**Files:**
- Create: `src/AutoUnlockService/AutoUnlockService.csproj`
- Create: `src/AutoUnlockService/Program.cs`
- Create: `src/AutoUnlockService/Worker.cs`
- Create: `src/AutoUnlockService/appsettings.json`
- Create: `scripts/install.ps1`
- Create: `scripts/uninstall.ps1`
- Modify: `AutoUnlockMyPc.sln`

**Interfaces:**
- Consumes: 无
- Produces: 可运行的服务宿主；`scripts/install.ps1` 与 `uninstall.ps1` 的最小可用版本（Task 16 扩充为完整版）

- [ ] **Step 1: 创建服务项目文件**

`src/AutoUnlockService/AutoUnlockService.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AutoUnlockService</RootNamespace>
    <OutputType>Exe</OutputType>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.0" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="9.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
    <PackageReference Include="Serilog.Sinks.EventLog" Version="4.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.0.0" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 写宿主入口**

`src/AutoUnlockService/Program.cs`：

```csharp
using Serilog;
using Serilog.Events;

namespace AutoUnlockService;

public static class Program
{
    // Carpeta unica para configuracion y registros, compartida con el instalador
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoUnlock");

    public static void Main(string[] args)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(DataDir, "logs", "autounlock-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .WriteTo.EventLog("AutoUnlock", manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateLogger();

        try
        {
            Log.Information("AutoUnlockService 正在启动");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddJsonFile(
                Path.Combine(DataDir, "appsettings.json"), optional: false, reloadOnChange: true);
            builder.Services.AddWindowsService(o => o.ServiceName = "AutoUnlockService");
            builder.Services.AddSerilog();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHostedService<Worker>();

            builder.Build().Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "AutoUnlockService 启动失败");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
```

- [ ] **Step 3: 写心跳 Worker**

`src/AutoUnlockService/Worker.cs`：

```csharp
namespace AutoUnlockService;

// Por ahora solo late; en la Task 15 se convierte en el orquestador real
public sealed class Worker(ILogger<Worker> log, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Worker 已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("心跳：服务运行中");
            await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
        }
    }
}
```

- [ ] **Step 4: 写配置模板**

`src/AutoUnlockService/appsettings.json`：

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

- [ ] **Step 5: 写最小安装脚本**

`scripts/install.ps1`（Task 16 会扩充 ACL、AUMID 快捷方式与 CP 注册）：

```powershell
#Requires -RunAsAdministrator
param(
    [string]$SourceDir = (Join-Path $PSScriptRoot '..\src\AutoUnlockService\bin\x64\Release\net10.0-windows10.0.19041.0'),
    [string]$InstallDir = 'C:\Program Files\AutoUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'AutoUnlock')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AutoUnlockService'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host '停止已有服务…'
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

Write-Host "拷贝文件：$SourceDir -> $InstallDir"
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force

$configPath = Join-Path $DataDir 'appsettings.json'
if (-not (Test-Path $configPath)) {
    Copy-Item -Path (Join-Path $SourceDir 'appsettings.json') -Destination $configPath
    Write-Host "已生成配置模板：$configPath"
} else {
    Write-Host "保留已有配置：$configPath"
}

if (-not [System.Diagnostics.EventLog]::SourceExists('AutoUnlock')) {
    New-EventLog -LogName Application -Source 'AutoUnlock'
    Write-Host '已注册事件日志源 AutoUnlock'
}

$exePath = Join-Path $InstallDir 'AutoUnlockService.exe'
sc.exe create $serviceName binPath= "`"$exePath`"" start= delayed-auto DisplayName= 'AutoUnlock 近距离自动解锁' | Out-Null
sc.exe description $serviceName '基于 BLE RSSI 的近距离自动锁屏与解锁服务' | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe start $serviceName | Out-Null

Write-Host "安装完成。日志目录：$(Join-Path $DataDir 'logs')"
```

`scripts/uninstall.ps1`：

```powershell
#Requires -RunAsAdministrator
param(
    [string]$InstallDir = 'C:\Program Files\AutoUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'AutoUnlock'),
    [switch]$KeepData
)

$ErrorActionPreference = 'Continue'
$serviceName = 'AutoUnlockService'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Write-Host '服务已删除'
}

if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir; Write-Host '程序文件已删除' }

if (-not $KeepData) {
    if ((Read-Host "是否删除配置与日志（$DataDir）？输入 yes 确认") -eq 'yes') {
        Remove-Item -Recurse -Force $DataDir
        Write-Host '配置与日志已删除'
    }
}
```

- [ ] **Step 6: 把项目加入解决方案并编译**

Run:
```
dotnet sln AutoUnlockMyPc.sln add src/AutoUnlockService/AutoUnlockService.csproj
dotnet build src/AutoUnlockService/AutoUnlockService.csproj -c Release
```
Expected: 编译成功，无警告阻断。

- [ ] **Step 7: 实机验证服务可装可跑**

Run（管理员 PowerShell）：
```
.\scripts\install.ps1
Get-Service AutoUnlockService
Get-Content "$env:ProgramData\AutoUnlock\logs\autounlock-*.log" -Tail 20
```
Expected: 服务状态 `Running`；日志中出现「AutoUnlockService 正在启动」「Worker 已启动」「心跳：服务运行中」。

若服务启动后立刻停止，查看事件查看器 → Windows 日志 → 应用程序，筛选来源 `AutoUnlock`。最常见原因是 `appsettings.json` 未生成（`optional: false` 会导致启动失败）。

- [ ] **Step 8: Commit**

```bash
git add src/AutoUnlockService scripts AutoUnlockMyPc.sln
git commit -m "feat(service): 服务骨架、Serilog 日志与最小安装脚本"
```

---

### Task 2: BLE 采集与 Session 0 可行性验证

**这是整个计划的 go/no-go 里程碑。** 规格第 14 节风险 1 在此揭晓：`BluetoothLEAdvertisementWatcher` 在 LocalSystem 服务里到底能不能收到广播。先把它跑通，再投入后面十几个任务。

**Files:**
- Create: `src/AutoUnlockService/Ble/AdvertisementSample.cs`
- Create: `src/AutoUnlockService/Ble/IRssiSource.cs`
- Create: `src/AutoUnlockService/Ble/BleScanner.cs`
- Modify: `src/AutoUnlockService/Worker.cs`
- Modify: `src/AutoUnlockService/Program.cs`

**Interfaces:**
- Consumes: Task 1 的宿主与日志
- Produces:
  - `record AdvertisementSample(ulong Address, bool IsRandomAddress, short Rssi, string? LocalName, DateTimeOffset Timestamp)`
  - `interface IRssiSource { event Action<AdvertisementSample>? SampleReceived; event Action<string>? StatusChanged; bool IsAvailable { get; } Task StartAsync(); Task StopAsync(); }`
  - `sealed class BleScanner : IRssiSource`

- [ ] **Step 1: 写样本记录与源抽象**

`src/AutoUnlockService/Ble/AdvertisementSample.cs`：

```csharp
namespace AutoUnlockService.Ble;

// Una emision BLE recibida; inmutable para poder cruzar hilos sin bloqueo
public sealed record AdvertisementSample(
    ulong Address,
    bool IsRandomAddress,
    short Rssi,
    string? LocalName,
    DateTimeOffset Timestamp);
```

`src/AutoUnlockService/Ble/IRssiSource.cs`：

```csharp
namespace AutoUnlockService.Ble;

// Abstraccion del origen de muestras: permite mover el escaneo a otro proceso sin tocar la maquina de estados
public interface IRssiSource
{
    event Action<AdvertisementSample>? SampleReceived;
    event Action<string>? StatusChanged;

    bool IsAvailable { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
```

- [ ] **Step 2: 写 BLE 扫描器**

`src/AutoUnlockService/Ble/BleScanner.cs`（移植自参考项目 `BleRssiMonitor/src/BleRssiMonitor/Ble/BleScanner.cs`，去掉界面相关部分，加上自动重启）：

```csharp
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace AutoUnlockService.Ble;

public sealed class BleScanner(ILogger<BleScanner> log, TimeProvider time) : IRssiSource
{
    private const int MaxFastRetries = 3;
    private static readonly TimeSpan FastRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowRetryDelay = TimeSpan.FromSeconds(30);

    private BluetoothLEAdvertisementWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private int _consecutiveFailures;

    public event Action<AdvertisementSample>? SampleReceived;
    public event Action<string>? StatusChanged;

    public bool IsAvailable { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = RunAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        StopWatcher();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await TryStartWatcherAsync())
            {
                _consecutiveFailures = 0;
                // El watcher sigue vivo hasta que se detenga o falle; esperamos a la cancelacion
                try { await Task.Delay(Timeout.InfiniteTimeSpan, time, ct); }
                catch (OperationCanceledException) { break; }
            }

            _consecutiveFailures++;
            var delay = _consecutiveFailures <= MaxFastRetries ? FastRetryDelay : SlowRetryDelay;
            log.LogWarning("BLE 扫描不可用（第 {Count} 次），{Delay} 秒后重试", _consecutiveFailures, delay.TotalSeconds);
            try { await Task.Delay(delay, time, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> TryStartWatcherAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported)
            {
                SetUnavailable("未找到支持 BLE 的蓝牙适配器");
                return false;
            }

            var radio = await adapter.GetRadioAsync();
            if (radio is null || radio.State != RadioState.On)
            {
                SetUnavailable("蓝牙已关闭");
                return false;
            }

            StopWatcher();
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _watcher.Received += OnReceived;
            _watcher.Stopped += OnStopped;
            _watcher.Start();

            IsAvailable = true;
            StatusChanged?.Invoke("BLE 扫描已启动");
            log.LogInformation("BLE 扫描已启动");
            return true;
        }
        catch (Exception ex)
        {
            SetUnavailable($"启动 BLE 扫描失败：{ex.Message}");
            return false;
        }
    }

    private void SetUnavailable(string reason)
    {
        IsAvailable = false;
        StatusChanged?.Invoke(reason);
        log.LogError("BLE 不可用：{Reason}", reason);
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        try { _watcher.Stop(); } catch { /* ya detenido */ }
        _watcher = null;
        IsAvailable = false;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var isRandom = args.BluetoothAddressType == BluetoothAddressType.Random;
        SampleReceived?.Invoke(new AdvertisementSample(
            args.BluetoothAddress,
            isRandom,
            args.RawSignalStrengthInDBm,
            string.IsNullOrEmpty(args.Advertisement.LocalName) ? null : args.Advertisement.LocalName,
            args.Timestamp));
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        IsAvailable = false;
        log.LogWarning("BLE Watcher 异常停止：{Error}", args.Error);
    }
}
```

- [ ] **Step 3: 把扫描接进 Worker 并打印广播**

替换 `src/AutoUnlockService/Worker.cs` 的内容：

```csharp
using AutoUnlockService.Ble;

namespace AutoUnlockService;

// Verificacion temporal: imprime cuantas emisiones llegan. En la Task 15 se sustituye por el orquestador real
public sealed class Worker(ILogger<Worker> log, IRssiSource source, TimeProvider time) : BackgroundService
{
    private int _sampleCount;
    private readonly HashSet<ulong> _addresses = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        source.SampleReceived += OnSample;
        source.StatusChanged += s => log.LogInformation("BLE 状态：{Status}", s);
        await source.StartAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken); }
            catch (OperationCanceledException) { break; }

            log.LogInformation("摘要：30 秒内收到 {Count} 条广播，来自 {Devices} 个不同地址",
                Interlocked.Exchange(ref _sampleCount, 0), _addresses.Count);
            lock (_addresses) { _addresses.Clear(); }
        }

        source.SampleReceived -= OnSample;
        await source.StopAsync();
    }

    private void OnSample(AdvertisementSample s)
    {
        Interlocked.Increment(ref _sampleCount);
        lock (_addresses) { _addresses.Add(s.Address); }
        log.LogDebug("广播 {Address:X12} RSSI={Rssi} 随机={Random} 名称={Name}",
            s.Address, s.Rssi, s.IsRandomAddress, s.LocalName ?? "-");
    }
}
```

- [ ] **Step 4: 注册依赖并把日志级别临时降到 Debug**

在 `Program.cs` 的 `Log.Logger` 配置里把 `.MinimumLevel.Information()` 临时改为 `.MinimumLevel.Debug()`，并在 DI 注册中加入扫描器：

```csharp
builder.Services.AddSingleton<IRssiSource, BleScanner>();
```

- [ ] **Step 5: 重新编译并安装**

Run（管理员 PowerShell）：
```
dotnet build src/AutoUnlockService/AutoUnlockService.csproj -c Release
.\scripts\install.ps1
```

- [ ] **Step 6: 验证 Session 0 能否收到广播（go/no-go）**

等待 60 秒后：
```
Get-Content "$env:ProgramData\AutoUnlock\logs\autounlock-*.log" -Tail 50
```

Expected（**通过**）：日志中出现「BLE 扫描已启动」，以及多条「广播 XXXXXXXXXXXX RSSI=-XX」，且 30 秒摘要显示收到的广播数大于 0。

**若摘要持续为 0 条**：说明 Session 0 拿不到广播，规格第 14 节风险 1 命中。**此时停止执行本计划，回报结果**。补救路径已在设计中预留：把 `BleScanner` 的实例移到 `AutoUnlockHelper` 进程（Task 13 建立），通过助手管道把 `AdvertisementSample` 回传，服务侧换一个实现了 `IRssiSource` 的管道客户端即可——状态机与解锁链路完全不受影响。这需要修订计划，不要自行展开。

- [ ] **Step 7: 把日志级别改回 Information**

把 `Program.cs` 里的 `.MinimumLevel.Debug()` 改回 `.MinimumLevel.Information()`。逐条广播的 Debug 日志量太大，不适合长期开启；后续调参靠 Task 15 的周期摘要。

- [ ] **Step 8: Commit**

```bash
git add src/AutoUnlockService
git commit -m "feat(ble): BLE 广播扫描与 Session 0 可行性验证"
```

---

### Task 3: 配置模型与校验

**Files:**
- Create: `src/AutoUnlockService/Config/AutoUnlockOptions.cs`
- Create: `src/AutoUnlockService/Config/OptionsValidator.cs`
- Create: `tests/AutoUnlockService.Tests/AutoUnlockService.Tests.csproj`
- Create: `tests/AutoUnlockService.Tests/OptionsValidatorTests.cs`
- Modify: `AutoUnlockMyPc.sln`

**Interfaces:**
- Consumes: 无
- Produces:
  - `AutoUnlockOptions`（含 `DeviceOptions` / `CredentialOptions` / `ProximityOptions` / `NotificationOptions`）
  - `enum IrkByteOrder { Auto, Normal, Reversed }`、`enum NotifierMode { Toast, Dialog }`
  - `record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)`
  - `static class OptionsValidator`，方法 `ValidationResult Validate(AutoUnlockOptions)` 与 `string? NormalizeIrk(string? raw)`（返回 32 位小写十六进制，非法则 null）

- [ ] **Step 1: 创建测试项目**

`tests/AutoUnlockService.Tests/AutoUnlockService.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <Platforms>x64</Platforms>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AutoUnlockService\AutoUnlockService.csproj" />
  </ItemGroup>

</Project>
```

Run:
```
dotnet sln AutoUnlockMyPc.sln add tests/AutoUnlockService.Tests/AutoUnlockService.Tests.csproj
```

- [ ] **Step 2: 写失败的测试**

`tests/AutoUnlockService.Tests/OptionsValidatorTests.cs`：

```csharp
using AutoUnlockService.Config;
using Xunit;

namespace AutoUnlockService.Tests;

public class OptionsValidatorTests
{
    private static AutoUnlockOptions Valid() => new()
    {
        Device = new DeviceOptions { Irk = "ec0234a357c8ad05341010a60a397d9b" },
        Credentials = new CredentialOptions { UserName = "Admin", Password = "secret" }
    };

    [Theory]
    [InlineData("ec0234a357c8ad05341010a60a397d9b")]
    [InlineData("EC0234A357C8AD05341010A60A397D9B")]
    [InlineData("ec:02:34:a3:57:c8:ad:05:34:10:10:a6:0a:39:7d:9b")]
    [InlineData("0xec0234a357c8ad05341010a60a397d9b")]
    [InlineData(" ec0234a357c8ad05341010a60a397d9b ")]
    public void NormalizeIrk_acepta_formatos_validos(string raw)
        => Assert.Equal("ec0234a357c8ad05341010a60a397d9b", OptionsValidator.NormalizeIrk(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ec0234a357c8ad05341010a60a397d9")]    // 31 caracteres
    [InlineData("ec0234a357c8ad05341010a60a397d9bb")]  // 33 caracteres
    [InlineData("zz0234a357c8ad05341010a60a397d9b")]   // no hexadecimal
    public void NormalizeIrk_rechaza_formatos_invalidos(string? raw)
        => Assert.Null(OptionsValidator.NormalizeIrk(raw));

    [Fact]
    public void Validate_acepta_configuracion_correcta()
        => Assert.True(OptionsValidator.Validate(Valid()).IsValid);

    [Fact]
    public void Validate_rechaza_irk_invalido()
    {
        var o = Valid();
        o.Device.Irk = "abc";
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("Irk"));
    }

    [Fact]
    public void Validate_rechaza_umbral_de_desbloqueo_no_mayor_que_el_de_bloqueo()
    {
        var o = Valid();
        o.Proximity.UnlockThresholdDbm = -90;   // menor que LockThresholdDbm (-80)
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("UnlockThresholdDbm"));
    }

    [Fact]
    public void Validate_rechaza_umbrales_iguales()
    {
        var o = Valid();
        o.Proximity.UnlockThresholdDbm = -80;   // igual a LockThresholdDbm
        Assert.False(OptionsValidator.Validate(o).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rechaza_ventanas_no_positivas(double seconds)
    {
        var o = Valid();
        o.Proximity.LockWindowSeconds = seconds;
        Assert.False(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_rechaza_min_samples_menor_que_uno()
    {
        var o = Valid();
        o.Proximity.MinSamples = 0;
        Assert.False(OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_rechaza_usuario_vacio()
    {
        var o = Valid();
        o.Credentials.UserName = "   ";
        var r = OptionsValidator.Validate(o);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("UserName"));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", true)]
    [InlineData("aabbccddeeff", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("AA:BB:CC:DD:EE", false)]
    [InlineData("GG:BB:CC:DD:EE:FF", false)]
    public void Validate_comprueba_identity_address(string? address, bool expected)
    {
        var o = Valid();
        o.Device.IdentityAddress = address;
        Assert.Equal(expected, OptionsValidator.Validate(o).IsValid);
    }

    [Fact]
    public void Validate_acumula_varios_errores()
    {
        var o = Valid();
        o.Device.Irk = "abc";
        o.Credentials.UserName = "";
        Assert.True(OptionsValidator.Validate(o).Errors.Count >= 2);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests -v minimal`
Expected: 编译失败，提示 `AutoUnlockOptions`、`OptionsValidator` 不存在。

- [ ] **Step 4: 写配置模型**

`src/AutoUnlockService/Config/AutoUnlockOptions.cs`：

```csharp
namespace AutoUnlockService.Config;

public sealed class AutoUnlockOptions
{
    public const string SectionName = "AutoUnlock";

    public DeviceOptions Device { get; set; } = new();
    public CredentialOptions Credentials { get; set; } = new();
    public ProximityOptions Proximity { get; set; } = new();
    public NotificationOptions Notification { get; set; } = new();
}

public enum IrkByteOrder { Auto, Normal, Reversed }

public enum NotifierMode { Toast, Dialog }

public sealed class DeviceOptions
{
    public string Irk { get; set; } = "";
    public IrkByteOrder IrkByteOrder { get; set; } = IrkByteOrder.Auto;
    public string? IdentityAddress { get; set; }
}

public sealed class CredentialOptions
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class ProximityOptions
{
    public double LockThresholdDbm { get; set; } = -80;
    public double LockWindowSeconds { get; set; } = 10;
    public double UnlockThresholdDbm { get; set; } = -45;
    public double UnlockWindowSeconds { get; set; } = 5;
    public int MinSamples { get; set; } = 3;
    public double StartupGraceSeconds { get; set; } = 15;
    public double UnlockPendingTimeoutSeconds { get; set; } = 20;
}

public sealed class NotificationOptions
{
    public NotifierMode Mode { get; set; } = NotifierMode.Toast;
    public double PromptTimeoutSeconds { get; set; } = 15;
    public double RejectSnoozeSeconds { get; set; } = 60;
    public double SnoozeMinutes { get; set; } = 5;
}
```

- [ ] **Step 5: 写校验器**

`src/AutoUnlockService/Config/OptionsValidator.cs`：

```csharp
using System.Globalization;

namespace AutoUnlockService.Config;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Ok() => new(true, []);
    public static ValidationResult Invalid(IReadOnlyList<string> errors) => new(false, errors);
}

public static class OptionsValidator
{
    // Quita espacios, dos puntos y el prefijo 0x; exige exactamente 32 caracteres hexadecimales
    public static string? NormalizeIrk(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim().Replace(":", "").Replace(" ", "").Replace("-", "");
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[2..];

        if (cleaned.Length != 32) return null;
        foreach (var c in cleaned)
            if (!Uri.IsHexDigit(c)) return null;

        return cleaned.ToLowerInvariant();
    }

    // Igual que el IRK pero de 12 caracteres; null o vacio significa "no configurado"
    public static ulong? NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
        if (cleaned.Length != 12) return null;
        return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    public static ValidationResult Validate(AutoUnlockOptions o)
    {
        var errors = new List<string>();

        if (NormalizeIrk(o.Device.Irk) is null)
            errors.Add("Device.Irk 必须是 32 个十六进制字符");

        if (!string.IsNullOrWhiteSpace(o.Device.IdentityAddress) &&
            NormalizeAddress(o.Device.IdentityAddress) is null)
            errors.Add("Device.IdentityAddress 必须是合法的 6 字节 MAC 地址");

        if (string.IsNullOrWhiteSpace(o.Credentials.UserName))
            errors.Add("Credentials.UserName 不能为空");

        var p = o.Proximity;
        if (p.UnlockThresholdDbm <= p.LockThresholdDbm)
            errors.Add("Proximity.UnlockThresholdDbm 必须大于 LockThresholdDbm，否则死区为负");

        if (p.LockWindowSeconds <= 0)   errors.Add("Proximity.LockWindowSeconds 必须为正数");
        if (p.UnlockWindowSeconds <= 0) errors.Add("Proximity.UnlockWindowSeconds 必须为正数");
        if (p.StartupGraceSeconds < 0)  errors.Add("Proximity.StartupGraceSeconds 不能为负数");
        if (p.UnlockPendingTimeoutSeconds <= 0)
            errors.Add("Proximity.UnlockPendingTimeoutSeconds 必须为正数");
        if (p.MinSamples < 1)           errors.Add("Proximity.MinSamples 必须大于等于 1");

        var n = o.Notification;
        if (n.PromptTimeoutSeconds <= 0) errors.Add("Notification.PromptTimeoutSeconds 必须为正数");
        if (n.RejectSnoozeSeconds <= 0)  errors.Add("Notification.RejectSnoozeSeconds 必须为正数");
        if (n.SnoozeMinutes <= 0)        errors.Add("Notification.SnoozeMinutes 必须为正数");

        return errors.Count == 0 ? ValidationResult.Ok() : ValidationResult.Invalid(errors);
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests -v minimal`
Expected: 全部通过。

- [ ] **Step 7: Commit**

```bash
git add src/AutoUnlockService/Config tests/AutoUnlockService.Tests AutoUnlockMyPc.sln
git commit -m "feat(config): 配置模型与校验规则"
```

---

### Task 4: 地址分类与 RPA 解析

移植参考项目 `BleRssiMonitor/src/BleRssiMonitor/Core/{BleAddress,RpaResolver}.cs`，改为单 IRK（服务只追一个设备，不需要 `Guid` 标识多把钥匙）。算法本身一行不改——它已被蓝牙核心规范的官方测试向量验证过。

**Files:**
- Create: `src/AutoUnlockService/Core/BleAddress.cs`
- Create: `src/AutoUnlockService/Core/RpaResolver.cs`
- Create: `tests/AutoUnlockService.Tests/RpaResolverTests.cs`

**Interfaces:**
- Consumes: `AutoUnlockService.Config.IrkByteOrder`（Task 3）
- Produces:
  - `enum AddressKind { Public, RandomStatic, Resolvable, NonResolvable }`
  - `static class BleAddress`：`string Format(ulong)`、`bool TryParse(string?, out ulong)`、`AddressKind Classify(ulong, bool)`
  - `sealed class RpaResolver`：`void SetKey(byte[] irk, IrkByteOrder order)`、`bool Matches(ulong address, bool isRandom)`、`IrkByteOrder? MatchedOrder { get; }`、`static uint ComputeHash(byte[] irk, uint prand)`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/RpaResolverTests.cs`：

```csharp
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using Xunit;

namespace AutoUnlockService.Tests;

public class RpaResolverTests
{
    // Vector de prueba oficial: Bluetooth Core Spec Vol 3 Part H, Apendice D.7
    internal static readonly byte[] SpecIrk = Convert.FromHexString("ec0234a357c8ad05341010a60a397d9b");
    internal const ulong SpecRpa = 0x708194_0DFBAAUL;

    internal static ulong MakeRpa(byte[] irk, uint prand) =>
        ((ulong)prand << 24) | RpaResolver.ComputeHash(irk, prand);

    [Fact]
    public void ComputeHash_coincide_con_el_vector_oficial()
        => Assert.Equal(0x0DFBAAu, RpaResolver.ComputeHash(SpecIrk, 0x708194));

    [Theory]
    [InlineData(IrkByteOrder.Normal)]
    [InlineData(IrkByteOrder.Auto)]
    public void Matches_con_irk_en_orden_normal(IrkByteOrder order)
    {
        var r = new RpaResolver();
        r.SetKey(SpecIrk, order);

        Assert.True(r.Matches(SpecRpa, isRandom: true));
        Assert.Equal(IrkByteOrder.Normal, r.MatchedOrder);
    }

    [Theory]
    [InlineData(IrkByteOrder.Reversed)]
    [InlineData(IrkByteOrder.Auto)]
    public void Matches_con_irk_introducida_al_reves(IrkByteOrder order)
    {
        var r = new RpaResolver();
        r.SetKey(Enumerable.Reverse(SpecIrk).ToArray(), order);

        Assert.True(r.Matches(SpecRpa, isRandom: true));
        Assert.Equal(IrkByteOrder.Reversed, r.MatchedOrder);
    }

    [Fact]
    public void Matches_modo_normal_no_acepta_entrada_invertida()
    {
        var r = new RpaResolver();
        r.SetKey(Enumerable.Reverse(SpecIrk).ToArray(), IrkByteOrder.Normal);

        Assert.False(r.Matches(SpecRpa, isRandom: true));
    }

    [Fact]
    public void Matches_irk_con_un_bit_cambiado_no_coincide()
    {
        var wrong = SpecIrk.ToArray();
        wrong[0] ^= 0x01;
        var r = new RpaResolver();
        r.SetKey(wrong, IrkByteOrder.Auto);

        Assert.False(r.Matches(SpecRpa, isRandom: true));
    }

    [Theory]
    [InlineData(SpecRpa, false)]            // direccion publica
    [InlineData(0xC08194_0DFBAAUL, true)]   // aleatoria estatica
    [InlineData(0x008194_0DFBAAUL, true)]   // no resoluble
    public void Matches_direccion_no_resoluble_devuelve_false(ulong address, bool isRandom)
    {
        var r = new RpaResolver();
        r.SetKey(SpecIrk, IrkByteOrder.Auto);

        Assert.False(r.Matches(address, isRandom));
    }

    [Fact]
    public void Matches_segunda_rpa_de_la_misma_irk_tambien_coincide()
    {
        var r = new RpaResolver();
        r.SetKey(SpecIrk, IrkByteOrder.Auto);

        Assert.True(r.Matches(MakeRpa(SpecIrk, 0x4ABCDE), isRandom: true));
    }

    [Fact]
    public void SetKey_limpia_los_fallos_cacheados()
    {
        var r = new RpaResolver();
        var wrong = SpecIrk.ToArray();
        wrong[0] ^= 0x01;
        r.SetKey(wrong, IrkByteOrder.Auto);
        Assert.False(r.Matches(SpecRpa, isRandom: true));

        r.SetKey(SpecIrk, IrkByteOrder.Auto);
        Assert.True(r.Matches(SpecRpa, isRandom: true));
    }

    [Fact]
    public void SetKey_rechaza_irk_de_longitud_incorrecta()
    {
        var r = new RpaResolver();
        Assert.Throws<ArgumentException>(() => r.SetKey(new byte[15], IrkByteOrder.Auto));
    }

    [Theory]
    [InlineData(0x708194_0DFBAAUL, true, AddressKind.Resolvable)]
    [InlineData(0xC08194_0DFBAAUL, true, AddressKind.RandomStatic)]
    [InlineData(0x008194_0DFBAAUL, true, AddressKind.NonResolvable)]
    [InlineData(0x708194_0DFBAAUL, false, AddressKind.Public)]
    public void Classify_identifica_el_tipo_de_direccion(ulong address, bool isRandom, AddressKind expected)
        => Assert.Equal(expected, BleAddress.Classify(address, isRandom));

    [Fact]
    public void Format_y_TryParse_son_simetricos()
    {
        const ulong address = 0xAABBCCDDEEFFUL;
        Assert.Equal("AA:BB:CC:DD:EE:FF", BleAddress.Format(address));
        Assert.True(BleAddress.TryParse("AA:BB:CC:DD:EE:FF", out var parsed));
        Assert.Equal(address, parsed);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests -v minimal`
Expected: 编译失败，提示 `BleAddress`、`RpaResolver`、`AddressKind` 不存在。

- [ ] **Step 3: 写地址工具类**

`src/AutoUnlockService/Core/BleAddress.cs`：

```csharp
using System.Globalization;

namespace AutoUnlockService.Core;

// Tipo de direccion BLE segun los dos bits mas significativos
public enum AddressKind { Public, RandomStatic, Resolvable, NonResolvable }

public static class BleAddress
{
    // Formatea una direccion de 48 bits como AA:BB:CC:DD:EE:FF (byte mas significativo primero)
    public static string Format(ulong address)
    {
        var parts = new string[6];
        for (int i = 0; i < 6; i++)
            parts[i] = ((address >> (8 * (5 - i))) & 0xFF).ToString("X2");
        return string.Join(':', parts);
    }

    // Acepta "AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF" o 12 digitos hexadecimales seguidos
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string hex = text.Trim().Replace(":", "").Replace("-", "");
        if (hex.Length != 12) return false;
        return ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    public static AddressKind Classify(ulong address, bool isRandom)
    {
        if (!isRandom) return AddressKind.Public;
        return ((address >> 46) & 0b11) switch
        {
            0b11 => AddressKind.RandomStatic,
            0b01 => AddressKind.Resolvable,
            0b00 => AddressKind.NonResolvable,
            // 0b10 esta reservado por la especificacion; lo tratamos como estatica
            _ => AddressKind.RandomStatic
        };
    }
}
```

- [ ] **Step 4: 写 RPA 解析器**

`src/AutoUnlockService/Core/RpaResolver.cs`：

```csharp
using System.Security.Cryptography;
using AutoUnlockService.Config;

namespace AutoUnlockService.Core;

// Version de un solo IRK: el servicio sigue un unico dispositivo
public sealed class RpaResolver
{
    private const int MaxCacheSize = 10_000;

    private readonly object _lock = new();
    // Cache direccion -> resultado; tambien guarda los fallos para no repetir AES
    private readonly Dictionary<ulong, bool> _cache = [];
    private List<(IrkByteOrder Order, byte[] Key)> _candidates = [];

    // Orden que realmente coincidio; null mientras no haya habido ninguna coincidencia
    public IrkByteOrder? MatchedOrder { get; private set; }

    public void SetKey(byte[] irk, IrkByteOrder order)
    {
        if (irk.Length != 16)
            throw new ArgumentException("IRK 必须是 16 字节", nameof(irk));

        var candidates = new List<(IrkByteOrder, byte[])>();
        if (order is IrkByteOrder.Normal or IrkByteOrder.Auto)
            candidates.Add((IrkByteOrder.Normal, irk.ToArray()));
        if (order is IrkByteOrder.Reversed or IrkByteOrder.Auto)
            candidates.Add((IrkByteOrder.Reversed, Enumerable.Reverse(irk).ToArray()));

        lock (_lock)
        {
            _candidates = candidates;
            _cache.Clear();
            MatchedOrder = null;
        }
    }

    public bool Matches(ulong address, bool isRandom)
    {
        if (BleAddress.Classify(address, isRandom) != AddressKind.Resolvable) return false;

        lock (_lock)
        {
            if (_cache.TryGetValue(address, out var cached)) return cached;

            // RPA = prand (24 bits altos) || hash (24 bits bajos)
            uint prand = (uint)(address >> 24) & 0xFFFFFF;
            uint hash  = (uint)address & 0xFFFFFF;

            bool matched = false;
            foreach (var c in _candidates)
            {
                if (ComputeHash(c.Key, prand) == hash)
                {
                    matched = true;
                    MatchedOrder = c.Order;
                    break;
                }
            }

            if (_cache.Count >= MaxCacheSize) _cache.Clear();
            _cache[address] = matched;
            return matched;
        }
    }

    // Funcion ah(k, r) del Bluetooth Core Spec Vol 3 Part H 2.2.2, bytes con MSB primero:
    // ah = e(k, 13 bytes de relleno 0x00 || r) mod 2^24
    public static uint ComputeHash(byte[] irk, uint prand)
    {
        var block = new byte[16];
        block[13] = (byte)(prand >> 16);
        block[14] = (byte)(prand >> 8);
        block[15] = (byte)prand;

        using var aes = Aes.Create();
        aes.Key = irk;
        byte[] output = aes.EncryptEcb(block, PaddingMode.None);
        return (uint)(output[13] << 16 | output[14] << 8 | output[15]);
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests -v minimal`
Expected: 全部通过，包含官方测试向量那条。

- [ ] **Step 6: Commit**

```bash
git add src/AutoUnlockService/Core tests/AutoUnlockService.Tests/RpaResolverTests.cs
git commit -m "feat(core): 移植 BLE 地址分类与 IRK 解析 RPA（单设备版）"
```

---

### Task 5: 目标设备判定

参考项目把「IRK 解析」放在 `RpaResolver`、「身份地址直接匹配」放在不移植的 `DeviceRegistry` 里。这里用一个很薄的 `DeviceMatcher` 把两条判定收敛到一处，状态机只需要一个布尔结论。

**Files:**
- Create: `src/AutoUnlockService/Core/DeviceMatcher.cs`
- Create: `tests/AutoUnlockService.Tests/DeviceMatcherTests.cs`

**Interfaces:**
- Consumes: `RpaResolver`、`BleAddress`（Task 4）、`DeviceOptions`、`OptionsValidator`（Task 3）、`AdvertisementSample`（Task 2）
- Produces: `sealed class DeviceMatcher`：`void Configure(DeviceOptions device)`、`bool IsTarget(AdvertisementSample sample)`、`IrkByteOrder? MatchedOrder { get; }`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/DeviceMatcherTests.cs`：

```csharp
using AutoUnlockService.Ble;
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using Xunit;

namespace AutoUnlockService.Tests;

public class DeviceMatcherTests
{
    private const string SpecIrkHex = "ec0234a357c8ad05341010a60a397d9b";
    private const ulong SpecRpa = 0x708194_0DFBAAUL;
    private const ulong Identity = 0xAABBCCDDEEFFUL;

    private static AdvertisementSample Sample(ulong address, bool isRandom) =>
        new(address, isRandom, -50, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void IsTarget_reconoce_la_rpa_por_irk()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IrkByteOrder = IrkByteOrder.Auto });

        Assert.True(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }

    [Fact]
    public void IsTarget_reconoce_la_direccion_de_identidad_sin_resolver_rpa()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IdentityAddress = "AA:BB:CC:DD:EE:FF" });

        // Direccion publica: nunca podria resolverse como RPA, debe coincidir por identidad
        Assert.True(m.IsTarget(Sample(Identity, isRandom: false)));
    }

    [Fact]
    public void IsTarget_sin_identity_address_solo_usa_irk()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IdentityAddress = null });

        Assert.False(m.IsTarget(Sample(Identity, isRandom: false)));
        Assert.True(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }

    [Fact]
    public void IsTarget_rechaza_dispositivo_ajeno()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex, IdentityAddress = "AA:BB:CC:DD:EE:FF" });

        Assert.False(m.IsTarget(Sample(0x112233445566UL, isRandom: true)));
    }

    [Fact]
    public void Configure_con_irk_invalida_no_reconoce_nada()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = "no-es-hexadecimal" });

        Assert.False(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }

    [Fact]
    public void Configure_cambia_el_dispositivo_seguido()
    {
        var m = new DeviceMatcher();
        m.Configure(new DeviceOptions { Irk = SpecIrkHex });
        Assert.True(m.IsTarget(Sample(SpecRpa, isRandom: true)));

        m.Configure(new DeviceOptions { Irk = "ec0234a357c8ad05341010a60a397d9a" });
        Assert.False(m.IsTarget(Sample(SpecRpa, isRandom: true)));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter DeviceMatcherTests -v minimal`
Expected: 编译失败，提示 `DeviceMatcher` 不存在。

- [ ] **Step 3: 写实现**

`src/AutoUnlockService/Core/DeviceMatcher.cs`：

```csharp
using AutoUnlockService.Ble;
using AutoUnlockService.Config;

namespace AutoUnlockService.Core;

// Unica pregunta que le interesa a la maquina de estados: esta emision es del dispositivo objetivo?
public sealed class DeviceMatcher
{
    private readonly RpaResolver _resolver = new();
    private readonly object _lock = new();
    private ulong? _identityAddress;
    private bool _hasKey;

    public IrkByteOrder? MatchedOrder => _resolver.MatchedOrder;

    public void Configure(DeviceOptions device)
    {
        var normalized = OptionsValidator.NormalizeIrk(device.Irk);
        lock (_lock)
        {
            _identityAddress = OptionsValidator.NormalizeAddress(device.IdentityAddress);
            _hasKey = normalized is not null;
        }
        if (normalized is not null)
            _resolver.SetKey(Convert.FromHexString(normalized), device.IrkByteOrder);
    }

    public bool IsTarget(AdvertisementSample sample)
    {
        ulong? identity;
        bool hasKey;
        lock (_lock)
        {
            identity = _identityAddress;
            hasKey = _hasKey;
        }

        // La direccion de identidad se comprueba primero: Windows puede reportarla
        // directamente en dispositivos ya emparejados, y entonces no hay RPA que resolver
        if (identity is { } id && sample.Address == id) return true;
        if (!hasKey) return false;

        return _resolver.Matches(sample.Address, sample.IsRandomAddress);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter DeviceMatcherTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/Core/DeviceMatcher.cs tests/AutoUnlockService.Tests/DeviceMatcherTests.cs
git commit -m "feat(core): 目标设备判定（身份地址优先、RPA 回落）"
```

---

### Task 6: RSSI 时间窗历史

状态机的全部判定都建立在这个类的窗口统计上。**Review Focus 第 4 条（广播乱序到达）在这里落实**：不假设时间戳单调递增；同时提供 `Clear()` 供唤醒后丢弃过期历史（Review Focus 第 1 条的一半），并用 `HasEverSeenDevice` 把「从未见过目标设备」与「见过但信号消失」区分开（Review Focus 第 3 条的基础）。

**Files:**
- Create: `src/AutoUnlockService/Core/RssiHistory.cs`
- Create: `tests/AutoUnlockService.Tests/RssiHistoryTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `readonly record struct WindowStats(int Count, double Average)`
  - `sealed class RssiHistory`：构造 `RssiHistory(TimeSpan retention)`；`void Add(short rssi, DateTimeOffset at)`、`WindowStats InWindow(DateTimeOffset now, TimeSpan window)`、`bool HasEverSeenDevice { get; }`、`void Clear()`、`void Reset()`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/RssiHistoryTests.cs`：

```csharp
using AutoUnlockService.Core;
using Xunit;

namespace AutoUnlockService.Tests;

public class RssiHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(120);

    [Fact]
    public void InWindow_sin_muestras_devuelve_cuenta_cero()
    {
        var h = new RssiHistory(Retention);
        Assert.Equal(0, h.InWindow(T0, TimeSpan.FromSeconds(10)).Count);
    }

    [Fact]
    public void InWindow_promedia_solo_las_muestras_dentro_de_la_ventana()
    {
        var h = new RssiHistory(Retention);
        h.Add(-40, T0.AddSeconds(-20));   // fuera de una ventana de 10 s
        h.Add(-50, T0.AddSeconds(-5));
        h.Add(-60, T0.AddSeconds(-1));

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(2, stats.Count);
        Assert.Equal(-55, stats.Average, precision: 6);
    }

    [Fact]
    public void InWindow_excluye_el_borde_antiguo_e_incluye_el_instante_actual()
    {
        var h = new RssiHistory(Retention);
        h.Add(-40, T0.AddSeconds(-10));   // exactamente en el borde: excluida
        h.Add(-60, T0);                   // justo ahora: incluida

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(1, stats.Count);
        Assert.Equal(-60, stats.Average, precision: 6);
    }

    [Fact]
    public void Add_acepta_marcas_de_tiempo_desordenadas()
    {
        var h = new RssiHistory(Retention);
        // El callback de WinRT no garantiza orden: llega primero la mas reciente
        h.Add(-50, T0.AddSeconds(-2));
        h.Add(-70, T0.AddSeconds(-8));
        h.Add(-60, T0.AddSeconds(-5));

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(3, stats.Count);
        Assert.Equal(-60, stats.Average, precision: 6);
    }

    [Fact]
    public void Add_acepta_marcas_de_tiempo_repetidas()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0.AddSeconds(-1));
        h.Add(-70, T0.AddSeconds(-1));

        var stats = h.InWindow(T0, TimeSpan.FromSeconds(10));

        Assert.Equal(2, stats.Count);
        Assert.Equal(-60, stats.Average, precision: 6);
    }

    [Fact]
    public void Add_descarta_muestras_mas_viejas_que_la_retencion()
    {
        var h = new RssiHistory(TimeSpan.FromSeconds(30));
        // Suficientes muestras antiguas para disparar la poda
        for (int i = 0; i < 20; i++) h.Add(-50, T0.AddSeconds(-300 - i));
        h.Add(-50, T0);

        // Una ventana enorme solo debe ver la muestra que sobrevivio a la poda
        Assert.Equal(1, h.InWindow(T0, TimeSpan.FromSeconds(600)).Count);
    }

    [Fact]
    public void HasEverSeenDevice_es_falso_hasta_la_primera_muestra()
    {
        var h = new RssiHistory(Retention);
        Assert.False(h.HasEverSeenDevice);

        h.Add(-50, T0);
        Assert.True(h.HasEverSeenDevice);
    }

    [Fact]
    public void Clear_vacia_las_muestras_pero_recuerda_que_el_dispositivo_existio()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0);

        h.Clear();

        Assert.Equal(0, h.InWindow(T0, TimeSpan.FromSeconds(10)).Count);
        Assert.True(h.HasEverSeenDevice);
    }

    [Fact]
    public void Reset_olvida_tambien_que_el_dispositivo_existio()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0);

        h.Reset();

        Assert.False(h.HasEverSeenDevice);
    }

    [Fact]
    public void InWindow_ignora_muestras_con_marca_futura_tras_un_salto_de_reloj()
    {
        var h = new RssiHistory(Retention);
        h.Add(-50, T0.AddSeconds(5));   // el reloj retrocedio: muestra en el futuro

        Assert.Equal(0, h.InWindow(T0, TimeSpan.FromSeconds(10)).Count);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter RssiHistoryTests -v minimal`
Expected: 编译失败，提示 `RssiHistory`、`WindowStats` 不存在。

- [ ] **Step 3: 写实现**

`src/AutoUnlockService/Core/RssiHistory.cs`：

```csharp
namespace AutoUnlockService.Core;

public readonly record struct WindowStats(int Count, double Average);

// Cola de muestras con poda por antiguedad. No asume que las marcas de tiempo
// lleguen ordenadas: el callback de WinRT no lo garantiza
public sealed class RssiHistory(TimeSpan retention)
{
    private const int PruneThreshold = 16;

    private readonly List<(DateTimeOffset Time, short Rssi)> _points = [];
    private readonly object _lock = new();
    private DateTimeOffset _newestSeen = DateTimeOffset.MinValue;

    public bool HasEverSeenDevice { get; private set; }

    public void Add(short rssi, DateTimeOffset at)
    {
        lock (_lock)
        {
            HasEverSeenDevice = true;
            _points.Add((at, rssi));
            if (at > _newestSeen) _newestSeen = at;

            // La poda es relativa a la muestra mas reciente vista, no a la recien anadida:
            // asi una muestra desordenada no borra el historico valido.
            // El umbral evita recorrer la lista en cada emision recibida
            if (_points.Count > PruneThreshold)
            {
                var cutoff = _newestSeen - retention;
                _points.RemoveAll(p => p.Time < cutoff);
            }
        }
    }

    public WindowStats InWindow(DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        lock (_lock)
        {
            int count = 0;
            double sum = 0;
            foreach (var p in _points)
            {
                // El borde antiguo es exclusivo y el instante actual inclusivo;
                // las marcas futuras se descartan por si el reloj retrocedio
                if (p.Time > from && p.Time <= now)
                {
                    count++;
                    sum += p.Rssi;
                }
            }
            return new WindowStats(count, count == 0 ? 0 : sum / count);
        }
    }

    // Tras despertar de suspension el historico ya no es representativo, pero el
    // dispositivo si existio: esa distincion la necesita la maquina de estados
    public void Clear()
    {
        lock (_lock)
        {
            _points.Clear();
            _newestSeen = DateTimeOffset.MinValue;
        }
    }

    // Se usa al cambiar de dispositivo: hay que olvidarlo todo
    public void Reset()
    {
        lock (_lock)
        {
            _points.Clear();
            _newestSeen = DateTimeOffset.MinValue;
            HasEverSeenDevice = false;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter RssiHistoryTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/Core/RssiHistory.cs tests/AutoUnlockService.Tests/RssiHistoryTests.cs
git commit -m "feat(core): RSSI 时间窗历史，容忍乱序与重复时间戳"
```

---

### Task 7: 接近状态机 —— 锁屏侧

建立状态机骨架，实现 `Unlocked` / `PendingLock` / `Snoozed` 三个状态与会话事件处理。解锁侧四个状态在 Task 8 填充。

**Review Focus 第 3 条在这里落实**：`HasEverSeenDevice` 为假时绝不锁屏。新装完服务、手表还没戴上时，窗口内样本数为 0 本会被判为「远」，宽限期一过就弹通知锁屏——用户对本软件的第一印象就成了被锁出去。

**Files:**
- Create: `src/AutoUnlockService/Core/ProximityStateMachine.cs`
- Create: `tests/AutoUnlockService.Tests/ProximityStateMachineLockTests.cs`

**Interfaces:**
- Consumes: `RssiHistory`、`WindowStats`（Task 6）、`ProximityOptions`、`NotificationOptions`（Task 3）
- Produces:
  - `enum ProximityState { Unlocked, PendingLock, Snoozed, LockedWaitingDeparture, LockedArmed, UnlockPending, Suspended }`
  - `enum ProximityAction { None, ShowLockPrompt, CancelLockPrompt, LockScreen, SendCredentials, NotifySuspended }`
  - `enum PromptResponse { Reject, Snooze, Timeout, Dismissed }`
  - `enum SessionEvent { Lock, Unlock }`
  - `sealed record StateChange(ProximityState From, ProximityState To, ProximityAction Action, string Reason)`
  - `sealed class ProximityStateMachine`：构造 `(TimeProvider time, ProximityOptions proximity, NotificationOptions notification, bool screenLockedAtStartup)`；属性 `ProximityOptions Proximity { get; set; }`、`NotificationOptions Notification { get; set; }`、`ProximityState State { get; }`；方法 `StateChange? Evaluate(RssiHistory)`、`StateChange? OnPromptResponse(PromptResponse)`、`StateChange? OnSessionEvent(SessionEvent)`、`void OnResume()`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/ProximityStateMachineLockTests.cs`：

```csharp
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class ProximityStateMachineLockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Build(
        bool screenLocked = false)
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120));
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), screenLocked);
        return (sm, time, history);
    }

    // Rellena la ventana con muestras del nivel indicado, una por segundo hacia atras
    private static void Fill(RssiHistory h, FakeTimeProvider time, short rssi, int count)
    {
        var now = time.GetUtcNow();
        for (int i = 0; i < count; i++)
            h.Add(rssi, now.AddSeconds(-i));
    }

    [Fact]
    public void Arranca_desbloqueado_si_la_pantalla_no_lo_esta()
        => Assert.Equal(ProximityState.Unlocked, Build().Sm.State);

    [Fact]
    public void Arranca_esperando_la_salida_si_la_pantalla_ya_estaba_bloqueada()
        => Assert.Equal(ProximityState.LockedWaitingDeparture, Build(screenLocked: true).Sm.State);

    [Fact]
    public void No_bloquea_durante_el_periodo_de_gracia_inicial()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);                      // el dispositivo existe
        time.Advance(TimeSpan.FromSeconds(14));    // la gracia son 15 s

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void No_bloquea_si_el_dispositivo_objetivo_nunca_ha_aparecido()
    {
        var (sm, time, history) = Build();
        // Sin ninguna muestra: recien instalado, el reloj todavia no se ha puesto
        time.Advance(TimeSpan.FromSeconds(600));

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void Pide_confirmacion_cuando_desaparece_la_senal()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));    // pasada la gracia y la ventana de 10 s

        var change = sm.Evaluate(history);

        Assert.NotNull(change);
        Assert.Equal(ProximityState.PendingLock, change.To);
        Assert.Equal(ProximityAction.ShowLockPrompt, change.Action);
        Assert.Contains("无信号", change.Reason);
    }

    [Fact]
    public void Pide_confirmacion_cuando_la_media_baja_del_umbral()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));
        Fill(history, time, -85, 5);               // media -85 < -80

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.PendingLock, change?.To);
        Assert.Contains("-85", change!.Reason);
    }

    [Fact]
    public void No_bloquea_con_menos_muestras_que_el_minimo()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(20));
        Fill(history, time, -85, 2);               // MinSamples = 3

        Assert.Null(sm.Evaluate(history));
    }

    [Fact]
    public void Cancela_el_aviso_si_la_senal_vuelve()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.PendingLock, sm.State);

        time.Advance(TimeSpan.FromSeconds(2));
        Fill(history, time, -40, 5);               // media -40 > -45

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.Unlocked, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
    }

    [Fact]
    public void Bloquea_cuando_vence_el_plazo_del_aviso()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        time.Advance(TimeSpan.FromSeconds(15));    // PromptTimeoutSeconds = 15
        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.LockScreen, change!.Action);
    }

    [Fact]
    public void Respuesta_de_rechazo_abre_un_silencio_de_sesenta_segundos()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        var change = sm.OnPromptResponse(PromptResponse.Reject);
        Assert.Equal(ProximityState.Snoozed, change?.To);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(sm.Evaluate(history));         // sigue en silencio

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ProximityState.Unlocked, sm.Evaluate(history)?.To);
    }

    [Fact]
    public void Respuesta_de_aplazamiento_silencia_cinco_minutos()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        sm.OnPromptResponse(PromptResponse.Snooze);

        time.Advance(TimeSpan.FromSeconds(299));
        Assert.Null(sm.Evaluate(history));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ProximityState.Unlocked, sm.Evaluate(history)?.To);
    }

    [Theory]
    [InlineData(PromptResponse.Timeout)]
    [InlineData(PromptResponse.Dismissed)]
    public void Timeout_y_descarte_bloquean_la_pantalla(PromptResponse response)
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        var change = sm.OnPromptResponse(response);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.LockScreen, change!.Action);
    }

    [Fact]
    public void Una_respuesta_fuera_de_PendingLock_se_ignora()
    {
        var (sm, _, _) = Build();
        Assert.Null(sm.OnPromptResponse(PromptResponse.Reject));
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }

    [Fact]
    public void Un_bloqueo_externo_cancela_el_aviso_pendiente()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);

        var change = sm.OnSessionEvent(SessionEvent.Lock);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
        Assert.Equal(ProximityAction.CancelLockPrompt, change!.Action);
    }

    [Fact]
    public void Un_bloqueo_externo_desde_Unlocked_exige_salir_antes_de_desbloquear()
    {
        var (sm, _, _) = Build();

        var change = sm.OnSessionEvent(SessionEvent.Lock);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
    }

    [Fact]
    public void Un_desbloqueo_manual_devuelve_el_estado_a_Unlocked()
    {
        var (sm, _, _) = Build(screenLocked: true);

        var change = sm.OnSessionEvent(SessionEvent.Unlock);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void OnResume_reabre_el_periodo_de_gracia_tras_un_salto_de_reloj()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));    // la gracia inicial ya paso

        // El portatil estuvo suspendido tres horas; el orquestador vacia el historico
        history.Clear();
        time.Advance(TimeSpan.FromHours(3));
        sm.OnResume();

        Assert.Null(sm.Evaluate(history));         // no bloquea nada mas abrir la tapa
        Assert.Equal(ProximityState.Unlocked, sm.State);

        time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(ProximityState.PendingLock, sm.Evaluate(history)?.To);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter ProximityStateMachineLockTests -v minimal`
Expected: 编译失败，提示 `ProximityStateMachine` 等类型不存在。

- [ ] **Step 3: 写实现**

`src/AutoUnlockService/Core/ProximityStateMachine.cs`：

```csharp
using AutoUnlockService.Config;

namespace AutoUnlockService.Core;

public enum ProximityState
{
    Unlocked,
    PendingLock,
    Snoozed,
    LockedWaitingDeparture,
    LockedArmed,
    UnlockPending,
    Suspended
}

public enum ProximityAction
{
    None,
    ShowLockPrompt,
    CancelLockPrompt,
    LockScreen,
    SendCredentials,
    NotifySuspended
}

public enum PromptResponse { Reject, Snooze, Timeout, Dismissed }

public enum SessionEvent { Lock, Unlock }

public sealed record StateChange(
    ProximityState From, ProximityState To, ProximityAction Action, string Reason);

// Toda la politica de bloqueo y desbloqueo vive aqui. No toca Windows ni la red:
// devuelve la accion que el orquestador debe ejecutar
public sealed class ProximityStateMachine
{
    private readonly TimeProvider _time;
    private DateTimeOffset _graceUntil;
    private DateTimeOffset _snoozeUntil;
    private DateTimeOffset _promptDeadline;
    // Se recuerda incluso en Suspended, para saber a que estado volver al reanudar
    private bool _screenLocked;

    public ProximityStateMachine(
        TimeProvider time,
        ProximityOptions proximity,
        NotificationOptions notification,
        bool screenLockedAtStartup)
    {
        _time = time;
        Proximity = proximity;
        Notification = notification;
        _screenLocked = screenLockedAtStartup;
        _graceUntil = time.GetUtcNow() + TimeSpan.FromSeconds(proximity.StartupGraceSeconds);
        State = screenLockedAtStartup
            ? ProximityState.LockedWaitingDeparture
            : ProximityState.Unlocked;
    }

    // Mutables: la recarga en caliente las sustituye sin reconstruir la maquina
    public ProximityOptions Proximity { get; set; }
    public NotificationOptions Notification { get; set; }

    public ProximityState State { get; private set; }

    public StateChange? Evaluate(RssiHistory history)
    {
        var now = _time.GetUtcNow();
        return State switch
        {
            ProximityState.Unlocked    => EvaluateUnlocked(history, now),
            ProximityState.PendingLock => EvaluatePendingLock(history, now),
            ProximityState.Snoozed     => EvaluateSnoozed(now),
            // Los estados de desbloqueo se implementan en la Task 8
            _ => null
        };
    }

    private StateChange? EvaluateUnlocked(RssiHistory history, DateTimeOffset now)
    {
        if (now < _graceUntil) return null;

        // Nunca bloqueamos si el dispositivo objetivo no ha aparecido ni una sola vez:
        // recien instalado, "sin muestras" significa "sin configurar", no "el usuario se fue"
        if (!history.HasEverSeenDevice) return null;

        var (far, reason) = IsFar(history, now);
        if (!far) return null;

        _promptDeadline = now + TimeSpan.FromSeconds(Notification.PromptTimeoutSeconds);
        return Transition(ProximityState.PendingLock, ProximityAction.ShowLockPrompt, reason);
    }

    private StateChange? EvaluatePendingLock(RssiHistory history, DateTimeOffset now)
    {
        var (near, nearReason) = IsNear(history, now);
        if (near)
            return Transition(ProximityState.Unlocked, ProximityAction.CancelLockPrompt,
                $"信号恢复：{nearReason}");

        // Red de seguridad: aunque la respuesta del aviso nunca llegue, el plazo vence igual
        if (now >= _promptDeadline)
        {
            _screenLocked = true;
            return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.LockScreen,
                "通知超时无响应");
        }

        return null;
    }

    private StateChange? EvaluateSnoozed(DateTimeOffset now) =>
        now >= _snoozeUntil
            ? Transition(ProximityState.Unlocked, ProximityAction.None, "延期结束")
            : null;

    public StateChange? OnPromptResponse(PromptResponse response)
    {
        if (State != ProximityState.PendingLock) return null;
        var now = _time.GetUtcNow();

        switch (response)
        {
            case PromptResponse.Reject:
                _snoozeUntil = now + TimeSpan.FromSeconds(Notification.RejectSnoozeSeconds);
                return Transition(ProximityState.Snoozed, ProximityAction.None,
                    $"用户拒绝锁屏，{Notification.RejectSnoozeSeconds:0} 秒内不再提示");

            case PromptResponse.Snooze:
                _snoozeUntil = now + TimeSpan.FromMinutes(Notification.SnoozeMinutes);
                return Transition(ProximityState.Snoozed, ProximityAction.None,
                    $"用户延期 {Notification.SnoozeMinutes:0} 分钟");

            default:
                _screenLocked = true;
                return Transition(ProximityState.LockedWaitingDeparture, ProximityAction.LockScreen,
                    response == PromptResponse.Timeout ? "通知超时无响应" : "用户关闭了通知");
        }
    }

    public StateChange? OnSessionEvent(SessionEvent e)
    {
        // Suspended es el unico estado que ignora los eventos de sesion: si el usuario
        // entra escribiendo la clave a mano, eso no significa que la clave del fichero
        // haya dejado de estar mal
        if (State == ProximityState.Suspended)
        {
            _screenLocked = e == SessionEvent.Lock;
            return null;
        }

        if (e == SessionEvent.Lock)
        {
            _screenLocked = true;
            if (State == ProximityState.LockedWaitingDeparture) return null;

            var action = State == ProximityState.PendingLock
                ? ProximityAction.CancelLockPrompt
                : ProximityAction.None;
            return Transition(ProximityState.LockedWaitingDeparture, action, "屏幕已锁定");
        }

        _screenLocked = false;
        return State == ProximityState.Unlocked
            ? null
            : Transition(ProximityState.Unlocked, ProximityAction.None, "屏幕已解锁");
    }

    // Tras despertar de suspension el reloj puede haber saltado horas. El orquestador
    // vacia el historico y aqui se vuelve a conceder el periodo de gracia, para no
    // bloquear la pantalla en el instante de abrir la tapa
    public void OnResume()
    {
        _graceUntil = _time.GetUtcNow() + TimeSpan.FromSeconds(Proximity.StartupGraceSeconds);
    }

    private (bool Far, string Reason) IsFar(RssiHistory history, DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(Proximity.LockWindowSeconds);
        var stats = history.InWindow(now, window);

        if (stats.Count == 0)
            return (true, $"{Proximity.LockWindowSeconds:0} 秒内无信号");

        if (stats.Count < Proximity.MinSamples) return (false, "");

        return stats.Average < Proximity.LockThresholdDbm
            ? (true, $"{Proximity.LockWindowSeconds:0} 秒内平均 {stats.Average:0.0} dBm" +
                     $"（{stats.Count} 个读数）< {Proximity.LockThresholdDbm:0}")
            : (false, "");
    }

    private (bool Near, string Reason) IsNear(RssiHistory history, DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(Proximity.UnlockWindowSeconds);
        var stats = history.InWindow(now, window);

        // Sin señal nunca es "cerca": el desbloqueo jamas se basa en una suposicion
        if (stats.Count < Proximity.MinSamples) return (false, "");

        return stats.Average > Proximity.UnlockThresholdDbm
            ? (true, $"{Proximity.UnlockWindowSeconds:0} 秒内平均 {stats.Average:0.0} dBm" +
                     $"（{stats.Count} 个读数）> {Proximity.UnlockThresholdDbm:0}")
            : (false, "");
    }

    private StateChange Transition(ProximityState to, ProximityAction action, string reason)
    {
        var change = new StateChange(State, to, action, reason);
        State = to;
        return change;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter ProximityStateMachineLockTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/Core/ProximityStateMachine.cs tests/AutoUnlockService.Tests/ProximityStateMachineLockTests.cs
git commit -m "feat(core): 接近状态机锁屏侧（通知、延期、启动宽限、未见过设备不锁）"
```

---

### Task 8: 接近状态机 —— 解锁侧与停摆

填充 `LockedWaitingDeparture` / `LockedArmed` / `UnlockPending` / `Suspended`，并加入 NTSTATUS 停摆策略。**这是整个项目最该写测试的地方**，尤其是「锁屏后原地不动绝不解锁」和「停摆不被会话事件清除」这两条。

**Files:**
- Create: `src/AutoUnlockService/Credentials/NtStatusPolicy.cs`
- Modify: `src/AutoUnlockService/Core/ProximityStateMachine.cs`
- Create: `tests/AutoUnlockService.Tests/NtStatusPolicyTests.cs`
- Create: `tests/AutoUnlockService.Tests/ProximityStateMachineUnlockTests.cs`

**Interfaces:**
- Consumes: Task 7 的 `ProximityStateMachine` 全部类型
- Produces:
  - `static class NtStatusPolicy`：常量 `StatusLogonFailure`、`StatusPasswordExpired`、`StatusPasswordMustChange`、`StatusAccountLockedOut`（均为 `uint`）；方法 `bool ShouldSuspend(uint ntStatus)`、`string Describe(uint ntStatus)`
  - `sealed record AuthResult(bool Success, uint NtStatus)`
  - `ProximityStateMachine` 新增方法 `StateChange? OnAuthResult(AuthResult)`、`StateChange? OnCredentialsChanged(bool preValidationPassed)`

- [ ] **Step 1: 写 NTSTATUS 策略的失败测试**

`tests/AutoUnlockService.Tests/NtStatusPolicyTests.cs`：

```csharp
using AutoUnlockService.Credentials;
using Xunit;

namespace AutoUnlockService.Tests;

public class NtStatusPolicyTests
{
    [Theory]
    [InlineData(0xC000006Du)]   // STATUS_LOGON_FAILURE
    [InlineData(0xC0000071u)]   // STATUS_PASSWORD_EXPIRED
    [InlineData(0xC0000224u)]   // STATUS_PASSWORD_MUST_CHANGE
    [InlineData(0xC0000234u)]   // STATUS_ACCOUNT_LOCKED_OUT
    public void ShouldSuspend_es_cierto_para_fallos_de_credencial(uint status)
        => Assert.True(NtStatusPolicy.ShouldSuspend(status));

    [Theory]
    [InlineData(0x00000000u)]   // STATUS_SUCCESS
    [InlineData(0xC000015Bu)]   // STATUS_LOGON_TYPE_NOT_GRANTED
    [InlineData(0xC0000022u)]   // STATUS_ACCESS_DENIED
    [InlineData(0xDEADBEEFu)]   // desconocido
    public void ShouldSuspend_es_falso_para_el_resto(uint status)
        => Assert.False(NtStatusPolicy.ShouldSuspend(status));

    [Fact]
    public void Describe_da_un_texto_en_chino_para_los_codigos_conocidos()
        => Assert.Contains("密码", NtStatusPolicy.Describe(0xC000006Du));

    [Fact]
    public void Describe_da_el_codigo_hexadecimal_para_los_desconocidos()
        => Assert.Contains("DEADBEEF", NtStatusPolicy.Describe(0xDEADBEEFu));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter NtStatusPolicyTests -v minimal`
Expected: 编译失败，提示 `NtStatusPolicy` 不存在。

- [ ] **Step 3: 写 NTSTATUS 策略**

`src/AutoUnlockService/Credentials/NtStatusPolicy.cs`：

```csharp
using System.Globalization;

namespace AutoUnlockService.Credentials;

// Decide si un fallo de autenticacion significa "la credencial del fichero esta mal".
// Solo esos casos paran el servicio; el resto se reintenta
public static class NtStatusPolicy
{
    public const uint StatusLogonFailure      = 0xC000006D;
    public const uint StatusPasswordExpired   = 0xC0000071;
    public const uint StatusPasswordMustChange = 0xC0000224;
    public const uint StatusAccountLockedOut  = 0xC0000234;

    public static bool ShouldSuspend(uint ntStatus) => ntStatus is
        StatusLogonFailure or
        StatusPasswordExpired or
        StatusPasswordMustChange or
        StatusAccountLockedOut;

    public static string Describe(uint ntStatus) => ntStatus switch
    {
        StatusLogonFailure       => "用户名或密码错误",
        StatusPasswordExpired    => "密码已过期",
        StatusPasswordMustChange => "密码必须修改后才能登录",
        StatusAccountLockedOut   => "账户已被锁定",
        _ => $"登录失败（NTSTATUS 0x{ntStatus.ToString("X8", CultureInfo.InvariantCulture)}）"
    };
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter NtStatusPolicyTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: 写解锁侧的失败测试**

`tests/AutoUnlockService.Tests/ProximityStateMachineUnlockTests.cs`：

```csharp
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using AutoUnlockService.Credentials;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class ProximityStateMachineUnlockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Build()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120));
        // Arrancamos con la pantalla ya bloqueada: es el escenario de esta bateria
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), true);
        return (sm, time, history);
    }

    private static void Fill(RssiHistory h, FakeTimeProvider time, short rssi, int count)
    {
        var now = time.GetUtcNow();
        for (int i = 0; i < count; i++)
            h.Add(rssi, now.AddSeconds(-i));
    }

    // Lleva la maquina hasta LockedArmed: el usuario se fue de verdad
    private static void Arm(ProximityStateMachine sm, FakeTimeProvider time, RssiHistory history)
    {
        history.Add(-50, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }

    [Fact]
    public void Pegado_al_ordenador_tras_bloquear_no_desbloquea_nunca()
    {
        var (sm, time, history) = Build();

        // El usuario pulso Win+L y sigue sentado: senal fortisima, pero nunca se fue
        for (int i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Fill(history, time, -30, 5);
            var change = sm.Evaluate(history);
            Assert.NotEqual(ProximityAction.SendCredentials, change?.Action ?? ProximityAction.None);
        }

        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Tras_detectar_la_salida_queda_armado()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));   // ventana sin senal

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedArmed, change?.To);
        Assert.Equal(ProximityAction.None, change!.Action);
    }

    [Fact]
    public void Armado_y_cerca_envia_las_credenciales_una_sola_vez()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);

        var change = sm.Evaluate(history);
        Assert.Equal(ProximityState.UnlockPending, change?.To);
        Assert.Equal(ProximityAction.SendCredentials, change!.Action);

        // Mientras espera resultado no vuelve a enviar, aunque siga cerca
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        Assert.Null(sm.Evaluate(history));
    }

    [Fact]
    public void El_resultado_correcto_devuelve_a_Unlocked()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(true, 0));

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Un_desbloqueo_manual_durante_la_espera_tambien_cierra_el_ciclo()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnSessionEvent(SessionEvent.Unlock);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Clave_erronea_deja_el_servicio_parado_y_avisa()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        Assert.Equal(ProximityState.Suspended, change?.To);
        Assert.Equal(ProximityAction.NotifySuspended, change!.Action);
    }

    [Fact]
    public void Un_fallo_que_no_es_de_credencial_solo_reintenta()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(false, 0xC0000022));

        Assert.Equal(ProximityState.LockedArmed, change?.To);
    }

    [Fact]
    public void Si_nunca_llega_el_resultado_vuelve_a_quedar_armado()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        // ReportResult puede no llegar nunca si LogonUI se destruye
        time.Advance(TimeSpan.FromSeconds(21));   // UnlockPendingTimeoutSeconds = 20

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedArmed, change?.To);
        Assert.NotEqual(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Suspended_no_se_sale_con_un_desbloqueo_manual()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        // El usuario entra escribiendo la clave a mano; eso no arregla el fichero
        Assert.Null(sm.OnSessionEvent(SessionEvent.Unlock));
        Assert.Equal(ProximityState.Suspended, sm.State);

        Assert.Null(sm.OnSessionEvent(SessionEvent.Lock));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Suspended_ignora_las_muestras()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        time.Advance(TimeSpan.FromSeconds(5));
        Fill(history, time, -40, 5);

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Corregir_la_clave_reanuda_el_servicio_en_el_estado_correcto()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        // La pantalla sigue bloqueada, asi que hay que volver a exigir la salida
        var change = sm.OnCredentialsChanged(preValidationPassed: true);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
    }

    [Fact]
    public void Corregir_la_clave_con_la_pantalla_abierta_reanuda_en_Unlocked()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        sm.OnSessionEvent(SessionEvent.Unlock);   // el usuario entro a mano
        var change = sm.OnCredentialsChanged(preValidationPassed: true);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Una_clave_que_sigue_sin_validar_no_reanuda()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        Assert.Null(sm.OnCredentialsChanged(preValidationPassed: false));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Una_credencial_que_no_valida_para_el_servicio_aunque_no_hubiera_fallado_antes()
    {
        var (sm, _, _) = Build();

        var change = sm.OnCredentialsChanged(preValidationPassed: false);

        Assert.Equal(ProximityState.Suspended, change?.To);
        Assert.Equal(ProximityAction.NotifySuspended, change!.Action);
    }

    [Fact]
    public void Armado_pero_con_senal_debil_no_envia_credenciales()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -60, 5);   // entre -80 y -45: zona muerta

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }
}
```

- [ ] **Step 6: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter ProximityStateMachineUnlockTests -v minimal`
Expected: 编译失败，提示 `AuthResult`、`OnAuthResult`、`OnCredentialsChanged` 不存在。

- [ ] **Step 7: 扩展状态机**

在 `src/AutoUnlockService/Core/ProximityStateMachine.cs` 顶部的 using 中加入：

```csharp
using AutoUnlockService.Credentials;
```

在 `StateChange` 记录下方加入：

```csharp
public sealed record AuthResult(bool Success, uint NtStatus);
```

在类的字段区（`private bool _screenLocked;` 之后）加入：

```csharp
    private DateTimeOffset _unlockSentAt;
```

把 `Evaluate` 里那行 `// Los estados de desbloqueo se implementan en la Task 8` 及其下的 `_ => null` 替换为：

```csharp
            ProximityState.LockedWaitingDeparture => EvaluateWaitingDeparture(history, now),
            ProximityState.LockedArmed            => EvaluateArmed(history, now),
            ProximityState.UnlockPending          => EvaluateUnlockPending(now),
            // Suspended no reacciona a las muestras: solo lo saca un cambio de configuracion
            _ => null
```

在 `EvaluateSnoozed` 之后加入三个新的求值方法：

```csharp
    private StateChange? EvaluateWaitingDeparture(RssiHistory history, DateTimeOffset now)
    {
        var (far, reason) = IsFar(history, now);
        return far
            ? Transition(ProximityState.LockedArmed, ProximityAction.None, $"已确认离开：{reason}")
            : null;
    }

    private StateChange? EvaluateArmed(RssiHistory history, DateTimeOffset now)
    {
        var (near, reason) = IsNear(history, now);
        if (!near) return null;

        _unlockSentAt = now;
        return Transition(ProximityState.UnlockPending, ProximityAction.SendCredentials,
            $"已确认靠近：{reason}");
    }

    private StateChange? EvaluateUnlockPending(DateTimeOffset now)
    {
        // ReportResult no esta garantizado: si LogonUI se destruye, el resultado nunca llega.
        // Agotado el plazo volvemos a armar y reintentamos; esto NO es motivo para parar
        var timeout = TimeSpan.FromSeconds(Proximity.UnlockPendingTimeoutSeconds);
        return now - _unlockSentAt >= timeout
            ? Transition(ProximityState.LockedArmed, ProximityAction.None, "等待认证结果超时，将重试")
            : null;
    }
```

在 `OnSessionEvent` 之后加入两个新的事件入口：

```csharp
    public StateChange? OnAuthResult(AuthResult result)
    {
        if (State != ProximityState.UnlockPending) return null;

        if (result.Success)
        {
            _screenLocked = false;
            return Transition(ProximityState.Unlocked, ProximityAction.None, "自动解锁成功");
        }

        if (NtStatusPolicy.ShouldSuspend(result.NtStatus))
            return Transition(ProximityState.Suspended, ProximityAction.NotifySuspended,
                $"自动解锁已暂停：{NtStatusPolicy.Describe(result.NtStatus)}");

        return Transition(ProximityState.LockedArmed, ProximityAction.None,
            $"解锁失败将重试：{NtStatusPolicy.Describe(result.NtStatus)}");
    }

    // La llama el orquestador cuando cambian usuario o clave en el fichero,
    // con el resultado de la prevalidacion LogonUser
    public StateChange? OnCredentialsChanged(bool preValidationPassed)
    {
        if (!preValidationPassed)
        {
            return State == ProximityState.Suspended
                ? null
                : Transition(ProximityState.Suspended, ProximityAction.NotifySuspended,
                    "凭据预验证失败，自动解锁已暂停");
        }

        if (State != ProximityState.Suspended) return null;

        var target = _screenLocked
            ? ProximityState.LockedWaitingDeparture
            : ProximityState.Unlocked;
        return Transition(target, ProximityAction.None, "凭据已更新且验证通过，自动解锁恢复");
    }
```

- [ ] **Step 8: 运行全部测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests -v minimal`
Expected: 全部通过，包括 Task 7 的锁屏侧测试没有回归。

- [ ] **Step 9: Commit**

```bash
git add src/AutoUnlockService/Core/ProximityStateMachine.cs src/AutoUnlockService/Credentials/NtStatusPolicy.cs tests/AutoUnlockService.Tests
git commit -m "feat(core): 状态机解锁侧与停摆策略（先离开再回来、认证失败停摆）"
```

---

### Task 9: 配置热更新闸门

`IOptionsMonitor` 的 `reloadOnChange` 有两个老毛病：保存一次触发多次回调，以及读到写了一半的文件。`OptionsGate` 把这两件事挡在状态机之前。

**Review Focus 第 2 条在这里落实**：文件被改成非法 JSON 时，`IOptionsMonitor` 会交回一份**全默认值**的对象——`UserName` 和 `Password` 都是空串。若不拦住，服务会拿着空凭据去解锁，必定失败，还可能触发账户锁定策略。校验不通过就必须保留内存里的旧配置。

把 `IOptionsMonitor` 留在 `Program.cs` 里对接，`OptionsGate` 只接受一个候选对象，这样它可以完全不依赖配置框架来测试。

**Files:**
- Create: `src/AutoUnlockService/Config/OptionsGate.cs`
- Create: `tests/AutoUnlockService.Tests/OptionsGateTests.cs`

**Interfaces:**
- Consumes: `AutoUnlockOptions`、`OptionsValidator`（Task 3）
- Produces:
  - `sealed record OptionsChange(AutoUnlockOptions Options, bool DeviceChanged, bool CredentialsChanged)`
  - `sealed class OptionsGate : IDisposable`：构造 `(AutoUnlockOptions initial, ILogger<OptionsGate> log, TimeProvider time, TimeSpan debounce)`；属性 `AutoUnlockOptions Current { get; }`；事件 `event Action<OptionsChange>? Changed`；方法 `void Submit(AutoUnlockOptions candidate)`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/OptionsGateTests.cs`：

```csharp
using AutoUnlockService.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class OptionsGateTests
{
    private const string IrkA = "ec0234a357c8ad05341010a60a397d9b";
    private const string IrkB = "ec0234a357c8ad05341010a60a397d9a";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private static AutoUnlockOptions Options(string irk = IrkA, string user = "Admin", string pass = "p") => new()
    {
        Device = new DeviceOptions { Irk = irk },
        Credentials = new CredentialOptions { UserName = user, Password = pass }
    };

    private static (OptionsGate Gate, FakeTimeProvider Time, List<OptionsChange> Changes) Build()
    {
        var time = new FakeTimeProvider();
        var gate = new OptionsGate(Options(), NullLogger<OptionsGate>.Instance, time, Debounce);
        var changes = new List<OptionsChange>();
        gate.Changed += changes.Add;
        return (gate, time, changes);
    }

    [Fact]
    public void Varios_envios_seguidos_producen_un_solo_evento()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromMilliseconds(100));
        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromMilliseconds(100));
        gate.Submit(Options(irk: IrkB));

        Assert.Empty(changes);                     // todavia dentro del antirrebote

        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Single(changes);
    }

    [Fact]
    public void Una_configuracion_invalida_no_sustituye_a_la_vigente()
    {
        var (gate, time, changes) = Build();
        var antes = gate.Current;

        gate.Submit(Options(irk: "no-es-hexadecimal"));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
        Assert.Same(antes, gate.Current);
    }

    [Fact]
    public void Un_fichero_corrupto_con_credenciales_vacias_se_rechaza()
    {
        var (gate, time, changes) = Build();
        var antes = gate.Current;

        // Lo que entrega IOptionsMonitor cuando el JSON deja de ser valido
        gate.Submit(new AutoUnlockOptions());
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
        Assert.Same(antes, gate.Current);
        Assert.Equal("Admin", gate.Current.Credentials.UserName);
    }

    [Fact]
    public void Un_cambio_valido_sustituye_la_configuracion_vigente()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.Equal(IrkB, gate.Current.Device.Irk);
    }

    [Fact]
    public void Marca_el_cambio_de_dispositivo()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(changes[0].DeviceChanged);
        Assert.False(changes[0].CredentialsChanged);
    }

    [Fact]
    public void Marca_el_cambio_de_credenciales()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(pass: "otra"));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(changes[0].CredentialsChanged);
        Assert.False(changes[0].DeviceChanged);
    }

    [Fact]
    public void Un_cambio_solo_de_umbrales_no_marca_ni_dispositivo_ni_credenciales()
    {
        var (gate, time, changes) = Build();
        var nuevo = Options();
        nuevo.Proximity.UnlockThresholdDbm = -40;

        gate.Submit(nuevo);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.False(changes[0].DeviceChanged);
        Assert.False(changes[0].CredentialsChanged);
        Assert.Equal(-40, gate.Current.Proximity.UnlockThresholdDbm);
    }

    [Fact]
    public void Un_envio_identico_al_vigente_no_dispara_evento()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options());
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(changes);
    }

    [Fact]
    public void Tras_rechazar_una_invalida_sigue_aceptando_la_siguiente_valida()
    {
        var (gate, time, changes) = Build();

        gate.Submit(Options(irk: "malo"));
        time.Advance(TimeSpan.FromSeconds(1));
        gate.Submit(Options(irk: IrkB));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(changes);
        Assert.Equal(IrkB, gate.Current.Device.Irk);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter OptionsGateTests -v minimal`
Expected: 编译失败，提示 `OptionsGate`、`OptionsChange` 不存在。

- [ ] **Step 3: 写实现**

`src/AutoUnlockService/Config/OptionsGate.cs`：

```csharp
namespace AutoUnlockService.Config;

public sealed record OptionsChange(
    AutoUnlockOptions Options, bool DeviceChanged, bool CredentialsChanged);

// Filtra las recargas en caliente: antirrebote, validacion y, si no pasa,
// conservar la configuracion que ya estaba funcionando
public sealed class OptionsGate : IDisposable
{
    private readonly ILogger<OptionsGate> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly object _lock = new();

    private ITimer? _timer;
    private AutoUnlockOptions? _pending;

    public OptionsGate(
        AutoUnlockOptions initial, ILogger<OptionsGate> log, TimeProvider time, TimeSpan debounce)
    {
        Current = initial;
        _log = log;
        _time = time;
        _debounce = debounce;
    }

    public AutoUnlockOptions Current { get; private set; }

    public event Action<OptionsChange>? Changed;

    // IOptionsMonitor puede llamar varias veces por un solo guardado, y a veces con el
    // fichero a medio escribir: por eso no se aplica nada hasta que el antirrebote vence
    public void Submit(AutoUnlockOptions candidate)
    {
        lock (_lock)
        {
            _pending = candidate;
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Apply(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Apply()
    {
        OptionsChange? change = null;

        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;

            var candidate = _pending;
            _pending = null;
            if (candidate is null) return;

            var result = OptionsValidator.Validate(candidate);
            if (!result.IsValid)
            {
                // Nunca se degrada a la configuracion nueva: seguir con la que funcionaba
                foreach (var error in result.Errors)
                    _log.LogError("配置校验失败，已保留原配置：{Error}", error);
                return;
            }

            bool deviceChanged = DeviceDiffers(Current, candidate);
            bool credentialsChanged = CredentialsDiffer(Current, candidate);
            if (!deviceChanged && !credentialsChanged && !OtherDiffers(Current, candidate)) return;

            Current = candidate;
            change = new OptionsChange(candidate, deviceChanged, credentialsChanged);

            // Nunca se registran valores de credenciales, solo que cambiaron
            _log.LogInformation("配置已重载（设备变更={Device}，凭据变更={Credentials}）",
                deviceChanged, credentialsChanged);
        }

        if (change is not null) Changed?.Invoke(change);
    }

    private static bool DeviceDiffers(AutoUnlockOptions a, AutoUnlockOptions b) =>
        !string.Equals(OptionsValidator.NormalizeIrk(a.Device.Irk),
                       OptionsValidator.NormalizeIrk(b.Device.Irk), StringComparison.Ordinal) ||
        a.Device.IrkByteOrder != b.Device.IrkByteOrder ||
        OptionsValidator.NormalizeAddress(a.Device.IdentityAddress) !=
        OptionsValidator.NormalizeAddress(b.Device.IdentityAddress);

    private static bool CredentialsDiffer(AutoUnlockOptions a, AutoUnlockOptions b) =>
        !string.Equals(a.Credentials.UserName, b.Credentials.UserName, StringComparison.Ordinal) ||
        !string.Equals(a.Credentials.Password, b.Credentials.Password, StringComparison.Ordinal);

    private static bool OtherDiffers(AutoUnlockOptions a, AutoUnlockOptions b) =>
        a.Proximity.LockThresholdDbm != b.Proximity.LockThresholdDbm ||
        a.Proximity.LockWindowSeconds != b.Proximity.LockWindowSeconds ||
        a.Proximity.UnlockThresholdDbm != b.Proximity.UnlockThresholdDbm ||
        a.Proximity.UnlockWindowSeconds != b.Proximity.UnlockWindowSeconds ||
        a.Proximity.MinSamples != b.Proximity.MinSamples ||
        a.Proximity.StartupGraceSeconds != b.Proximity.StartupGraceSeconds ||
        a.Proximity.UnlockPendingTimeoutSeconds != b.Proximity.UnlockPendingTimeoutSeconds ||
        a.Notification.Mode != b.Notification.Mode ||
        a.Notification.PromptTimeoutSeconds != b.Notification.PromptTimeoutSeconds ||
        a.Notification.RejectSnoozeSeconds != b.Notification.RejectSnoozeSeconds ||
        a.Notification.SnoozeMinutes != b.Notification.SnoozeMinutes;

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter OptionsGateTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/Config/OptionsGate.cs tests/AutoUnlockService.Tests/OptionsGateTests.cs
git commit -m "feat(config): 热更新闸门，防抖并在校验失败时保留旧配置"
```

---

### Task 10: 凭据预验证

规格要求「双保险」的第一道：在服务启动和每次凭据变更时就用 `LogonUser` 验一次，不等到锁屏才发现密码是错的。

用户名三种格式（本地、Microsoft 账户、域）的拆分是纯逻辑，用 TDD 覆盖；`LogonUser` 本身是 Win32 调用，靠实机验收。

**Files:**
- Create: `src/AutoUnlockService/Credentials/UserNameParser.cs`
- Create: `src/AutoUnlockService/Credentials/CredentialValidator.cs`
- Create: `tests/AutoUnlockService.Tests/UserNameParserTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `readonly record struct LogonName(string? Domain, string User)`
  - `static class UserNameParser`：`LogonName Split(string userName)`
  - `sealed class CredentialValidator(ILogger<CredentialValidator> log)`：`bool Validate(string userName, string password)`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/UserNameParserTests.cs`：

```csharp
using AutoUnlockService.Credentials;
using Xunit;

namespace AutoUnlockService.Tests;

public class UserNameParserTests
{
    [Fact]
    public void Una_cuenta_local_usa_el_punto_como_dominio()
    {
        var name = UserNameParser.Split("Admin");

        Assert.Equal(".", name.Domain);
        Assert.Equal("Admin", name.User);
    }

    [Fact]
    public void Una_cuenta_de_dominio_se_parte_por_la_barra()
    {
        var name = UserNameParser.Split("KIROL\\hq.qiu");

        Assert.Equal("KIROL", name.Domain);
        Assert.Equal("hq.qiu", name.User);
    }

    [Fact]
    public void Una_cuenta_microsoft_conserva_el_correo_entero_como_usuario()
    {
        var name = UserNameParser.Split("MicrosoftAccount\\alguien@outlook.com");

        Assert.Equal("MicrosoftAccount", name.Domain);
        Assert.Equal("alguien@outlook.com", name.User);
    }

    [Fact]
    public void Un_upn_va_sin_dominio_porque_LogonUser_lo_resuelve_solo()
    {
        var name = UserNameParser.Split("hq.qiu@kirol.es");

        Assert.Null(name.Domain);
        Assert.Equal("hq.qiu@kirol.es", name.User);
    }

    [Fact]
    public void Solo_se_parte_por_la_primera_barra()
    {
        var name = UserNameParser.Split("DOM\\sub\\user");

        Assert.Equal("DOM", name.Domain);
        Assert.Equal("sub\\user", name.User);
    }

    [Fact]
    public void Se_recortan_los_espacios_sobrantes()
    {
        var name = UserNameParser.Split("  Admin  ");

        Assert.Equal(".", name.Domain);
        Assert.Equal("Admin", name.User);
    }

    [Fact]
    public void Una_barra_inicial_no_deja_un_dominio_vacio()
    {
        var name = UserNameParser.Split("\\Admin");

        Assert.Equal(".", name.Domain);
        Assert.Equal("Admin", name.User);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter UserNameParserTests -v minimal`
Expected: 编译失败，提示 `UserNameParser` 不存在。

- [ ] **Step 3: 写用户名拆分**

`src/AutoUnlockService/Credentials/UserNameParser.cs`：

```csharp
namespace AutoUnlockService.Credentials;

public readonly record struct LogonName(string? Domain, string User);

// Las tres formas que acepta Windows: local, dominio y cuenta Microsoft.
// El UPN se pasa entero con dominio nulo, que es como LogonUser espera resolverlo
public static class UserNameParser
{
    public static LogonName Split(string userName)
    {
        var trimmed = userName.Trim();

        int slash = trimmed.IndexOf('\\');
        if (slash > 0)
            return new LogonName(trimmed[..slash], trimmed[(slash + 1)..]);

        // Una barra al principio no aporta dominio: "\Admin" es una cuenta local
        if (slash == 0) trimmed = trimmed[1..];

        return trimmed.Contains('@')
            ? new LogonName(null, trimmed)
            : new LogonName(".", trimmed);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter UserNameParserTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: 写 LogonUser 预验证**

`src/AutoUnlockService/Credentials/CredentialValidator.cs`：

```csharp
using System.Runtime.InteropServices;

namespace AutoUnlockService.Credentials;

// Prevalidacion: comprueba usuario y clave antes de que haga falta desbloquear.
// Usa LOGON32_LOGON_NETWORK, que no crea sesion interactiva ni perfil de usuario
public sealed class CredentialValidator(ILogger<CredentialValidator> log)
{
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    public bool Validate(string userName, string password)
    {
        var name = UserNameParser.Split(userName);

        if (!LogonUserW(name.User, name.Domain, password,
                Logon32LogonNetwork, Logon32ProviderDefault, out var token))
        {
            int error = Marshal.GetLastWin32Error();
            // Jamas se registra la clave, solo el usuario y el codigo de error
            log.LogError("凭据预验证失败，用户 {User}，错误码 {Error}", userName, error);
            return false;
        }

        token.Dispose();
        log.LogInformation("凭据预验证通过，用户 {User}", userName);
        return true;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LogonUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle phToken);
}
```

- [ ] **Step 6: 实机验证 LogonUser**

因为需要真实账户，这一步靠手工验证。临时在 `Worker.ExecuteAsync` 开头插入一次调用（验证完删掉）：

```csharp
new CredentialValidator(loggerFactory.CreateLogger<CredentialValidator>())
    .Validate("你的用户名", "你的密码");
```

Run：重新编译、`.\scripts\install.ps1`，然后查看日志。
Expected: 正确密码时出现「凭据预验证通过」；故意写错一位时出现「凭据预验证失败…错误码 1326」（`ERROR_LOGON_FAILURE`）。**确认日志里没有出现密码明文。**

验证完把临时代码删掉。

- [ ] **Step 7: Commit**

```bash
git add src/AutoUnlockService/Credentials tests/AutoUnlockService.Tests/UserNameParserTests.cs
git commit -m "feat(credentials): 用户名格式拆分与 LogonUser 预验证"
```

---

### Task 11: 凭据提供程序改长连接并回传认证结果

现在 CP 读一次凭据就关闭连接，回的 `OK` 只代表「我收到了」。改成长连接后，CP 在 `ReportResult` 拿到 `NTSTATUS` 时把真实认证结果写回管道。

同时**必须更新 `TestCredentialProviderApp`**：它说的是旧协议，不改就会和新 CP 对不上话。改完之后它正好成为本任务的验证工具——不需要服务存在，就能把新协议端到端跑通。

**Files:**
- Modify: `src/AutoUnlockCredentialProvider/PipeClient.h`
- Modify: `src/AutoUnlockCredentialProvider/PipeClient.cpp`
- Modify: `src/AutoUnlockCredentialProvider/Credential.h:60-62`（`AuthFailCallback` 相关声明）
- Modify: `src/AutoUnlockCredentialProvider/Credential.cpp:45-48,135-143`
- Modify: `src/AutoUnlockCredentialProvider/CredentialProvider.h`（删除 `_authFailed`）
- Modify: `src/AutoUnlockCredentialProvider/CredentialProvider.cpp:7-37,86-97,148-163`
- Modify: `src/TestCredentialProviderApp/Form1.cs:161-186`

**Interfaces:**
- Consumes: 无（C++ 侧自成一体）
- Produces: 管道协议 —— 服务发 `CREDS\n<username>\n<password>\n`；CP 立即回 `ACK\n`；认证结束后回 `RESULT OK\n` 或 `RESULT FAIL <8位十六进制>\n`

- [ ] **Step 1: 改写 PipeClient 头文件**

`src/AutoUnlockCredentialProvider/PipeClient.h` 全文替换为：

```cpp
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <functional>

using CredReceivedCallback = std::function<void(const std::wstring&, const std::wstring&)>;

class CPipeClient
{
public:
    explicit CPipeClient(CredReceivedCallback callback);
    ~CPipeClient();

    void Start();
    void Stop();

    // La llama la credencial desde ReportResult; despierta al hilo del canal
    void ReportResult(NTSTATUS status);

private:
    static DWORD WINAPI ThreadProc(LPVOID lpParam);
    void Run();
    bool HandleConnection(HANDLE hPipe);
    bool WriteLine(HANDLE hPipe, const std::string& text);

    HANDLE               _hThread;
    HANDLE               _hStopEvent;
    HANDLE               _hResultEvent;   // senalizado cuando llega el NTSTATUS
    CredReceivedCallback _callback;
    NTSTATUS             _pendingStatus;
};
```

- [ ] **Step 2: 改写 PipeClient 实现**

`src/AutoUnlockCredentialProvider/PipeClient.cpp` 全文替换为：

```cpp
#include "PipeClient.h"
#include <string>
#include <vector>

static const WCHAR PIPE_NAME[] = L"\\\\.\\pipe\\AutoUnlock";

// Cuanto esperamos el NTSTATUS antes de rendirnos: ReportResult no esta garantizado
static const DWORD RESULT_TIMEOUT_MS = 25000;

CPipeClient::CPipeClient(CredReceivedCallback callback)
    : _hThread(nullptr)
    , _callback(std::move(callback))
    , _pendingStatus(0)
{
    _hStopEvent   = CreateEventW(nullptr, TRUE,  FALSE, nullptr);
    _hResultEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
}

CPipeClient::~CPipeClient()
{
    Stop();
    if (_hStopEvent)   CloseHandle(_hStopEvent);
    if (_hResultEvent) CloseHandle(_hResultEvent);
}

void CPipeClient::Start()
{
    ResetEvent(_hStopEvent);
    _hThread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
}

void CPipeClient::Stop()
{
    if (_hStopEvent) SetEvent(_hStopEvent);
    if (_hThread)
    {
        WaitForSingleObject(_hThread, 5000);
        CloseHandle(_hThread);
        _hThread = nullptr;
    }
}

void CPipeClient::ReportResult(NTSTATUS status)
{
    _pendingStatus = status;
    if (_hResultEvent) SetEvent(_hResultEvent);
}

DWORD WINAPI CPipeClient::ThreadProc(LPVOID lpParam)
{
    reinterpret_cast<CPipeClient*>(lpParam)->Run();
    return 0;
}

void CPipeClient::Run()
{
    while (WaitForSingleObject(_hStopEvent, 0) != WAIT_OBJECT_0)
    {
        HANDLE hPipe = CreateFileW(
            PIPE_NAME, GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, 0, nullptr);

        if (hPipe == INVALID_HANDLE_VALUE)
        {
            WaitForSingleObject(_hStopEvent, 1000);
            continue;
        }

        DWORD dwMode = PIPE_READMODE_BYTE;
        SetNamedPipeHandleState(hPipe, &dwMode, nullptr, nullptr);

        // La conexion se mantiene viva hasta que falle: el servicio decide cuando enviar
        HandleConnection(hPipe);
        CloseHandle(hPipe);

        WaitForSingleObject(_hStopEvent, 500);
    }
}

bool CPipeClient::WriteLine(HANDLE hPipe, const std::string& text)
{
    DWORD written = 0;
    return WriteFile(hPipe, text.c_str(), static_cast<DWORD>(text.size()), &written, nullptr) != FALSE;
}

bool CPipeClient::HandleConnection(HANDLE hPipe)
{
    std::string buffer;

    while (WaitForSingleObject(_hStopEvent, 0) != WAIT_OBJECT_0)
    {
        char chunk[512] = {};
        DWORD bytesRead = 0;

        if (!ReadFile(hPipe, chunk, sizeof(chunk), &bytesRead, nullptr) || bytesRead == 0)
        {
            SecureZeroMemory(chunk, sizeof(chunk));
            break;   // el servicio cerro o fallo el canal: se reconectara
        }

        buffer.append(chunk, bytesRead);
        SecureZeroMemory(chunk, sizeof(chunk));

        // El canal es de bytes: un mensaje CREDS son tres lineas completas
        for (;;)
        {
            size_t n1 = buffer.find('\n');
            if (n1 == std::string::npos) break;

            std::string verb = buffer.substr(0, n1);
            if (verb != "CREDS")
            {
                buffer.erase(0, n1 + 1);   // mensaje desconocido: se descarta
                continue;
            }

            size_t n2 = buffer.find('\n', n1 + 1);
            if (n2 == std::string::npos) break;
            size_t n3 = buffer.find('\n', n2 + 1);
            if (n3 == std::string::npos) break;

            std::string usernameUtf8 = buffer.substr(n1 + 1, n2 - n1 - 1);
            std::string passwordUtf8 = buffer.substr(n2 + 1, n3 - n2 - 1);
            buffer.erase(0, n3 + 1);

            auto toWide = [](const std::string& s) -> std::wstring {
                if (s.empty()) return {};
                int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
                std::wstring w(len - 1, L'\0');
                MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), len);
                return w;
            };

            std::wstring username = toWide(usernameUtf8);
            std::wstring password = toWide(passwordUtf8);

            WriteLine(hPipe, "ACK\n");

            ResetEvent(_hResultEvent);
            _callback(username, password);

            // Las copias locales se borran en cuanto la credencial se las ha quedado
            SecureZeroMemory(password.data(), password.size() * sizeof(wchar_t));
            SecureZeroMemory(const_cast<char*>(passwordUtf8.data()), passwordUtf8.size());
            SecureZeroMemory(const_cast<char*>(buffer.data()), buffer.size());

            HANDLE waits[2] = { _hResultEvent, _hStopEvent };
            DWORD waited = WaitForMultipleObjects(2, waits, FALSE, RESULT_TIMEOUT_MS);

            if (waited == WAIT_OBJECT_0)
            {
                char line[64] = {};
                if (_pendingStatus == 0)
                    WriteLine(hPipe, "RESULT OK\n");
                else
                {
                    sprintf_s(line, "RESULT FAIL %08X\n", static_cast<unsigned>(_pendingStatus));
                    WriteLine(hPipe, line);
                }
            }
            else if (waited == WAIT_OBJECT_0 + 1)
            {
                return false;   // parada solicitada
            }
            // En caso de timeout no se escribe nada: el servicio tiene su propio plazo
        }
    }

    SecureZeroMemory(const_cast<char*>(buffer.data()), buffer.size());
    return true;
}
```

- [ ] **Step 3: 让凭据回调携带 NTSTATUS**

在 `src/AutoUnlockCredentialProvider/Credential.h` 中，把

```cpp
    using AuthFailCallback = std::function<void()>;
    void SetAuthFailCallback(AuthFailCallback cb);
```

替换为

```cpp
    using AuthResultCallback = std::function<void(NTSTATUS)>;
    void SetAuthResultCallback(AuthResultCallback cb);
```

并把私有字段 `AuthFailCallback _authFailCb;` 替换为 `AuthResultCallback _authResultCb;`。

在 `src/AutoUnlockCredentialProvider/Credential.cpp` 中，把 `SetAuthFailCallback` 整个函数替换为：

```cpp
void CAutoUnlockCredential::SetAuthResultCallback(AuthResultCallback cb)
{
    _authResultCb = std::move(cb);
}
```

并把 `ReportResult` 的函数体替换为：

```cpp
HRESULT CAutoUnlockCredential::ReportResult(NTSTATUS ntsStatus, NTSTATUS, PWSTR* ppwsz,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsi)
{
    *ppwsz = nullptr;
    *pcpsi = CPSI_NONE;

    // El servicio decide que hacer con el resultado; aqui solo se informa
    if (_authResultCb) _authResultCb(ntsStatus);

    return S_OK;
}
```

- [ ] **Step 4: 删除 CP 里的本地停摆标志**

在 `src/AutoUnlockCredentialProvider/CredentialProvider.h` 中删除私有字段 `bool _authFailed;`。

在 `src/AutoUnlockCredentialProvider/CredentialProvider.cpp` 中：

构造函数的初始化列表删掉 `, _authFailed(false)`，并把设置回调那段替换为：

```cpp
    if (_pCredential)
    {
        _pCredential->SetAuthResultCallback([this](NTSTATUS status)
        {
            if (_pPipeClient) _pPipeClient->ReportResult(status);
        });
    }
```

`Advise` 开头那三行重置 `_authFailed` 的临界区代码删掉，只保留后面对 `_pcpe` 的处理。

`OnCredentialsReceived` 的函数体替换为：

```cpp
void CAutoUnlockCredentialProvider::OnCredentialsReceived(
    const std::wstring& username, const std::wstring& password)
{
    ICredentialProviderEvents* pcpe = nullptr;
    UINT_PTR ctx = 0;

    EnterCriticalSection(&_cs);
    if (_pCredential)
    {
        _pCredential->SetCredentials(username, password);
        if (_pcpe) { pcpe = _pcpe; pcpe->AddRef(); ctx = _upAdviseContext; }
    }
    LeaveCriticalSection(&_cs);

    if (pcpe) { pcpe->CredentialsChanged(ctx); pcpe->Release(); }
}
```

注意：`_pCredential` 的构造顺序要保证 `_pPipeClient` 在回调被触发前已创建。现有构造函数里 `_pCredential` 先于 `_pPipeClient` 创建，但回调要到真正认证时才执行，那时两者都已就绪，无需调整顺序。

- [ ] **Step 5: 把测试工具改到新协议**

在 `src/TestCredentialProviderApp/Form1.cs` 中，把 `SendCredentialsAsync` 整个方法替换为：

```csharp
    private async Task SendCredentialsAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string username = string.Empty, password = string.Empty;
        Invoke(() =>
        {
            username = txtUserName.Text.Trim();
            password = txtPassword.Text;
        });

        if (string.IsNullOrEmpty(username))
        {
            Log("⚠ 用户名为空，已跳过发送");
            return;
        }

        byte[] payload = Encoding.UTF8.GetBytes($"CREDS\n{username}\n{password}\n");
        await pipe.WriteAsync(payload, ct);
        _credSentAt = DateTime.Now;
        Log($"已发送凭据：{username} / {"*".PadRight(password.Length, '*')}");

        // El CP responde ACK enseguida y RESULT cuando Windows ha resuelto la autenticacion
        var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        string? ack = await reader.ReadLineAsync(ct);
        Log($"收到确认：{ack}");

        string? result = await reader.ReadLineAsync(ct);
        Log(result switch
        {
            "RESULT OK"                       => "✓ 认证成功",
            not null when result.StartsWith("RESULT FAIL ") =>
                $"✗ 认证失败，NTSTATUS 0x{result["RESULT FAIL ".Length..]}",
            null                              => "连接已断开，未收到认证结果",
            _                                 => $"收到未知响应：{result}"
        });
    }
```

- [ ] **Step 6: 编译 CP 与测试工具**

Run：
```
msbuild src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj /p:Configuration=Release /p:Platform=x64
dotnet build src/TestCredentialProviderApp/TestCredentialProviderApp.csproj -c Release
```
Expected: 两者均编译成功。

- [ ] **Step 7: 实机验证新协议**

以管理员运行 `TestCredentialProviderApp`：

1. 点「安装 Credential Provider」（若已装过则点「重新安装 CP」，它会覆盖并重新注册新编译的 DLL）
2. 填入**正确**的用户名和密码，点「锁屏（触发式）」
3. 预期：锁屏 → 5 秒倒计时 → 日志出现「收到确认：ACK」→ 自动解锁 → 日志出现「✓ 认证成功」
4. 再次运行，填入**错误**的密码，点「锁屏（触发式）」
5. 预期：日志出现「✗ 认证失败，NTSTATUS 0xC000006D」，屏幕保持锁定

第 4 步是本任务的核心验收点：**错误密码必须能把具体的 NTSTATUS 回传到服务端**，这是停摆策略的全部依据。

- [ ] **Step 8: Commit**

```bash
git add src/AutoUnlockCredentialProvider src/TestCredentialProviderApp
git commit -m "feat(cp): 管道改长连接并回传认证结果 NTSTATUS"
```

---

### Task 12: 服务侧凭据管道

`\\.\pipe\AutoUnlock` 的服务端，DACL 只放行 SYSTEM（服务与 winlogon 都以 SYSTEM 运行）。协议的编解码抽成纯函数用 TDD 覆盖，管道本身靠 Task 11 已经验证过的 CP 做实机联调。

**Files:**
- Create: `src/AutoUnlockService/Credentials/CredentialProtocol.cs`
- Create: `src/AutoUnlockService/Credentials/CredentialPipeServer.cs`
- Create: `tests/AutoUnlockService.Tests/CredentialProtocolTests.cs`
- Modify: `src/AutoUnlockService/AutoUnlockService.csproj`（加 `System.IO.Pipes.AccessControl`）

**Interfaces:**
- Consumes: `AuthResult`（Task 8）
- Produces:
  - `static class CredentialProtocol`：`byte[] BuildCreds(string userName, string password)`、`bool TryParseResult(string line, out AuthResult result)`
  - `sealed class CredentialPipeServer : IAsyncDisposable`：构造 `(ILogger<CredentialPipeServer> log)`；属性 `bool IsClientConnected { get; }`；事件 `event Action<AuthResult>? AuthResultReceived`；方法 `void Start(CancellationToken ct)`、`Task<bool> SendCredentialsAsync(string userName, string password, CancellationToken ct)`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/CredentialProtocolTests.cs`：

```csharp
using System.Text;
using AutoUnlockService.Core;
using AutoUnlockService.Credentials;
using Xunit;

namespace AutoUnlockService.Tests;

public class CredentialProtocolTests
{
    [Fact]
    public void BuildCreds_produce_tres_lineas_en_utf8()
    {
        var bytes = CredentialProtocol.BuildCreds("Admin", "clave");

        Assert.Equal("CREDS\nAdmin\nclave\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BuildCreds_conserva_los_caracteres_no_ascii()
    {
        var bytes = CredentialProtocol.BuildCreds("用户", "contraseña");

        Assert.Equal("CREDS\n用户\ncontraseña\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void TryParseResult_reconoce_el_exito()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT OK", out var r));
        Assert.True(r.Success);
        Assert.Equal(0u, r.NtStatus);
    }

    [Fact]
    public void TryParseResult_reconoce_el_fallo_con_su_codigo()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT FAIL C000006D", out var r));
        Assert.False(r.Success);
        Assert.Equal(0xC000006Du, r.NtStatus);
    }

    [Fact]
    public void TryParseResult_acepta_hexadecimal_en_minusculas()
    {
        Assert.True(CredentialProtocol.TryParseResult("RESULT FAIL c000006d", out var r));
        Assert.Equal(0xC000006Du, r.NtStatus);
    }

    [Fact]
    public void TryParseResult_tolera_espacios_y_retorno_de_carro()
    {
        Assert.True(CredentialProtocol.TryParseResult("  RESULT OK\r ", out var r));
        Assert.True(r.Success);
    }

    [Theory]
    [InlineData("ACK")]
    [InlineData("")]
    [InlineData("RESULT")]
    [InlineData("RESULT FAIL")]
    [InlineData("RESULT FAIL no-es-hex")]
    [InlineData("RESULT MAYBE")]
    public void TryParseResult_rechaza_lo_que_no_es_un_resultado(string line)
        => Assert.False(CredentialProtocol.TryParseResult(line, out _));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter CredentialProtocolTests -v minimal`
Expected: 编译失败，提示 `CredentialProtocol` 不存在。

- [ ] **Step 3: 写协议编解码**

`src/AutoUnlockService/Credentials/CredentialProtocol.cs`：

```csharp
using System.Globalization;
using System.Text;
using AutoUnlockService.Core;

namespace AutoUnlockService.Credentials;

// Protocolo de texto con el proveedor de credenciales C++:
//   servicio -> CP   CREDS\n<usuario>\n<clave>\n
//   CP -> servicio   ACK\n  y despues  RESULT OK\n  o  RESULT FAIL <8 hex>\n
public static class CredentialProtocol
{
    private const string ResultOk = "RESULT OK";
    private const string ResultFailPrefix = "RESULT FAIL ";

    public static byte[] BuildCreds(string userName, string password) =>
        Encoding.UTF8.GetBytes($"CREDS\n{userName}\n{password}\n");

    public static bool TryParseResult(string line, out AuthResult result)
    {
        result = new AuthResult(false, 0);
        var trimmed = line.Trim();

        if (trimmed == ResultOk)
        {
            result = new AuthResult(true, 0);
            return true;
        }

        if (!trimmed.StartsWith(ResultFailPrefix, StringComparison.Ordinal)) return false;

        var hex = trimmed[ResultFailPrefix.Length..].Trim();
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var status))
            return false;

        result = new AuthResult(false, status);
        return true;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter CredentialProtocolTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: 加上管道 ACL 所需的包**

在 `src/AutoUnlockService/AutoUnlockService.csproj` 的 `PackageReference` 组里加入：

```xml
    <PackageReference Include="System.IO.Pipes.AccessControl" Version="6.0.0" />
```

- [ ] **Step 6: 写管道服务端**

`src/AutoUnlockService/Credentials/CredentialPipeServer.cs`：

```csharp
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AutoUnlockService.Core;

namespace AutoUnlockService.Credentials;

// Servidor de \\.\pipe\AutoUnlock. Mantiene una conexion viva con el CP y empuja
// las credenciales solo cuando la maquina de estados lo pide
public sealed class CredentialPipeServer(ILogger<CredentialPipeServer> log) : IAsyncDisposable
{
    private const string PipeName = "AutoUnlock";

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private Task? _loop;
    private CancellationTokenSource? _cts;

    public bool IsClientConnected => _pipe?.IsConnected == true;

    public event Action<AuthResult>? AuthResultReceived;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = CreatePipe();
                log.LogInformation("凭据管道已就绪，等待凭据提供程序连接");
                await _pipe.WaitForConnectionAsync(ct);
                log.LogInformation("凭据提供程序已连接");

                await ReadLoopAsync(_pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning("凭据管道异常：{Message}，2 秒后重建", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                var pipe = _pipe;
                _pipe = null;
                if (pipe is not null) await pipe.DisposeAsync();
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;   // el CP cerro la conexion

            if (line.Trim() == "ACK")
            {
                log.LogDebug("凭据提供程序已确认收到凭据");
                continue;
            }

            if (CredentialProtocol.TryParseResult(line, out var result))
            {
                log.LogInformation("收到认证结果：成功={Success} NTSTATUS=0x{Status:X8}",
                    result.Success, result.NtStatus);
                AuthResultReceived?.Invoke(result);
                continue;
            }

            log.LogWarning("凭据管道收到无法识别的消息");
        }
    }

    public async Task<bool> SendCredentialsAsync(string userName, string password, CancellationToken ct)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
        {
            log.LogWarning("要推送凭据时没有凭据提供程序连接，本次放弃");
            return false;
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            var payload = CredentialProtocol.BuildCreds(userName, password);
            await pipe.WriteAsync(payload, ct);
            await pipe.FlushAsync(ct);
            // Nunca se registra la clave, solo el usuario
            log.LogInformation("已向凭据提供程序推送凭据，用户 {User}", userName);
            Array.Clear(payload);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning("推送凭据失败：{Message}", ex.Message);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Solo SYSTEM puede conectarse: el servicio y winlogon corren con esa identidad
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        if (_pipe is not null) await _pipe.DisposeAsync();
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}
```

- [ ] **Step 7: 编译确认无误**

Run: `dotnet build src/AutoUnlockService/AutoUnlockService.csproj -c Release`
Expected: 编译成功。管道的端到端联调在 Task 15 把编排接起来后、以及 Task 17 的实机验收里完成。

- [ ] **Step 8: Commit**

```bash
git add src/AutoUnlockService/Credentials tests/AutoUnlockService.Tests/CredentialProtocolTests.cs src/AutoUnlockService/AutoUnlockService.csproj
git commit -m "feat(credentials): 凭据管道协议与服务端（仅 SYSTEM 可连接）"
```

---

### Task 13: 助手管道协议

服务与助手之间的文本协议。两个进程都要用，所以文件放在服务项目里，助手项目用 `Link` 引用同一份源码——比起复制，少一处走样的机会；比起再开一个类库项目，少一层构建。

**Files:**
- Create: `src/AutoUnlockService/Session/HelperProtocol.cs`
- Create: `tests/AutoUnlockService.Tests/HelperProtocolTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `enum PromptOutcome { Reject, Snooze, Timeout, Dismissed }`
  - `abstract record HelperCommand`，含嵌套 `Prompt(int Id, double TimeoutSeconds, double SnoozeMinutes)`、`Cancel(int Id)`、`Lock`、`Notify(string Message)`
  - `static class HelperProtocol`：`BuildPrompt`、`BuildCancel`、`BuildLock`、`BuildNotify`、`TryParseCommand`、`BuildHello`、`BuildResponse`、`BuildLocked`、`TryParseResponse`

- [ ] **Step 1: 写失败的测试**

`tests/AutoUnlockService.Tests/HelperProtocolTests.cs`：

```csharp
using AutoUnlockService.Session;
using Xunit;

namespace AutoUnlockService.Tests;

public class HelperProtocolTests
{
    [Fact]
    public void Prompt_ida_y_vuelta()
    {
        var line = HelperProtocol.BuildPrompt(7, 15, 5);

        Assert.True(HelperProtocol.TryParseCommand(line, out var cmd));
        var prompt = Assert.IsType<HelperCommand.Prompt>(cmd);
        Assert.Equal(7, prompt.Id);
        Assert.Equal(15, prompt.TimeoutSeconds);
        Assert.Equal(5, prompt.SnoozeMinutes);
    }

    [Fact]
    public void Cancel_ida_y_vuelta()
    {
        Assert.True(HelperProtocol.TryParseCommand(HelperProtocol.BuildCancel(3), out var cmd));
        Assert.Equal(3, Assert.IsType<HelperCommand.Cancel>(cmd).Id);
    }

    [Fact]
    public void Lock_ida_y_vuelta()
    {
        Assert.True(HelperProtocol.TryParseCommand(HelperProtocol.BuildLock(), out var cmd));
        Assert.IsType<HelperCommand.Lock>(cmd);
    }

    [Fact]
    public void Notify_conserva_un_mensaje_con_espacios()
    {
        var line = HelperProtocol.BuildNotify("密码验证失败，自动解锁已暂停");

        Assert.True(HelperProtocol.TryParseCommand(line, out var cmd));
        Assert.Equal("密码验证失败，自动解锁已暂停", Assert.IsType<HelperCommand.Notify>(cmd).Message);
    }

    [Fact]
    public void Los_numeros_usan_formato_invariante()
    {
        // En una maquina con coma decimal esto rompería si no se fijara la cultura
        var line = HelperProtocol.BuildPrompt(1, 12.5, 2.5);

        Assert.Contains("12.5", line);
        Assert.True(HelperProtocol.TryParseCommand(line, out _));
    }

    [Theory]
    [InlineData(PromptOutcome.Reject)]
    [InlineData(PromptOutcome.Snooze)]
    [InlineData(PromptOutcome.Timeout)]
    [InlineData(PromptOutcome.Dismissed)]
    public void Response_ida_y_vuelta(PromptOutcome outcome)
    {
        var line = HelperProtocol.BuildResponse(42, outcome);

        Assert.True(HelperProtocol.TryParseResponse(line, out var id, out var parsed));
        Assert.Equal(42, id);
        Assert.Equal(outcome, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PROMPT")]
    [InlineData("PROMPT a b c")]
    [InlineData("QUE_ES_ESTO 1")]
    public void TryParseCommand_rechaza_basura(string line)
        => Assert.False(HelperProtocol.TryParseCommand(line, out _));

    [Theory]
    [InlineData("RESPONSE 1 QUIZA")]
    [InlineData("RESPONSE x REJECT")]
    [InlineData("HELLO 1")]
    public void TryParseResponse_rechaza_basura(string line)
        => Assert.False(HelperProtocol.TryParseResponse(line, out _, out _));

    [Fact]
    public void Hello_y_Locked_terminan_en_salto_de_linea()
    {
        Assert.EndsWith("\n", HelperProtocol.BuildHello(1));
        Assert.EndsWith("\n", HelperProtocol.BuildLocked());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter HelperProtocolTests -v minimal`
Expected: 编译失败，提示 `HelperProtocol` 不存在。

- [ ] **Step 3: 写实现**

`src/AutoUnlockService/Session/HelperProtocol.cs`：

```csharp
using System.Globalization;

namespace AutoUnlockService.Session;

public enum PromptOutcome { Reject, Snooze, Timeout, Dismissed }

public abstract record HelperCommand
{
    public sealed record Prompt(int Id, double TimeoutSeconds, double SnoozeMinutes) : HelperCommand;
    public sealed record Cancel(int Id) : HelperCommand;
    public sealed record Lock : HelperCommand;
    public sealed record Notify(string Message) : HelperCommand;
}

// Protocolo de texto entre el servicio y el ayudante de sesion.
// Los numeros van siempre en cultura invariante: la maquina puede usar coma decimal
public static class HelperProtocol
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string BuildPrompt(int id, double timeoutSeconds, double snoozeMinutes) =>
        string.Create(Inv, $"PROMPT {id} {timeoutSeconds} {snoozeMinutes}\n");

    public static string BuildCancel(int id) => string.Create(Inv, $"CANCEL {id}\n");

    public static string BuildLock() => "LOCK\n";

    public static string BuildNotify(string message) => $"NOTIFY {message}\n";

    public static bool TryParseCommand(string line, out HelperCommand command)
    {
        command = null!;
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        var verb = parts[0];
        var rest = parts.Length > 1 ? parts[1] : "";

        switch (verb)
        {
            case "LOCK":
                command = new HelperCommand.Lock();
                return true;

            case "NOTIFY":
                if (rest.Length == 0) return false;
                command = new HelperCommand.Notify(rest);
                return true;

            case "CANCEL":
                if (!int.TryParse(rest, NumberStyles.Integer, Inv, out var cancelId)) return false;
                command = new HelperCommand.Cancel(cancelId);
                return true;

            case "PROMPT":
                var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length != 3) return false;
                if (!int.TryParse(args[0], NumberStyles.Integer, Inv, out var id)) return false;
                if (!double.TryParse(args[1], NumberStyles.Float, Inv, out var timeout)) return false;
                if (!double.TryParse(args[2], NumberStyles.Float, Inv, out var snooze)) return false;
                command = new HelperCommand.Prompt(id, timeout, snooze);
                return true;

            default:
                return false;
        }
    }

    public static string BuildHello(int sessionId) => string.Create(Inv, $"HELLO {sessionId}\n");

    public static string BuildResponse(int id, PromptOutcome outcome) =>
        string.Create(Inv, $"RESPONSE {id} {outcome.ToString().ToUpperInvariant()}\n");

    public static string BuildLocked() => "LOCKED\n";

    public static bool TryParseResponse(string line, out int id, out PromptOutcome outcome)
    {
        id = 0;
        outcome = PromptOutcome.Timeout;

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "RESPONSE") return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, Inv, out id)) return false;

        return Enum.TryParse(parts[2], ignoreCase: true, out outcome)
            && Enum.IsDefined(outcome);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter HelperProtocolTests -v minimal`
Expected: 全部通过。

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/Session/HelperProtocol.cs tests/AutoUnlockService.Tests/HelperProtocolTests.cs
git commit -m "feat(session): 服务与助手之间的文本协议"
```

---

### Task 14: 用户会话助手进程

无窗口的用户态程序，由服务拉起。负责两件事：弹真正的 Toast 并把按钮结果回传，以及执行 `LockWorkStation`。

**Files:**
- Create: `src/AutoUnlockHelper/AutoUnlockHelper.csproj`
- Create: `src/AutoUnlockHelper/Program.cs`
- Create: `src/AutoUnlockHelper/ToastPrompt.cs`
- Create: `src/AutoUnlockHelper/ScreenLock.cs`
- Modify: `AutoUnlockMyPc.sln`

**Interfaces:**
- Consumes: `HelperProtocol`、`HelperCommand`、`PromptOutcome`（Task 13，通过 `Link` 共享源码）
- Produces: 可执行文件 `AutoUnlockHelper.exe`，连接 `\\.\pipe\AutoUnlockHelper` 并遵守 Task 13 的协议

- [ ] **Step 1: 创建助手项目**

`src/AutoUnlockHelper/AutoUnlockHelper.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <OutputType>WinExe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AutoUnlockHelper</RootNamespace>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <!-- Registra solo el AUMID y el acceso directo del menu Inicio, que es lo que
         Windows exige para mostrar Toasts desde una aplicacion sin empaquetar -->
    <PackageReference Include="CommunityToolkit.WinUI.Notifications" Version="7.1.2" />
  </ItemGroup>

  <ItemGroup>
    <!-- Se comparte el fichero en lugar de duplicarlo: una sola definicion del protocolo -->
    <Compile Include="..\AutoUnlockService\Session\HelperProtocol.cs" Link="HelperProtocol.cs" />
  </ItemGroup>

</Project>
```

`src/AutoUnlockHelper/app.manifest`（不要求提升权限，助手以登录用户身份运行）：

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifest-version="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
```

`OutputType` 用 `WinExe` 是为了不弹出控制台窗口。

- [ ] **Step 2: 写锁屏调用**

`src/AutoUnlockHelper/ScreenLock.cs`：

```csharp
using System.Runtime.InteropServices;

namespace AutoUnlockHelper;

// El ayudante vive en la sesion del usuario, asi que puede bloquear directamente
internal static class ScreenLock
{
    public static bool Lock() => LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
```

- [ ] **Step 3: 写 Toast 提示**

`src/AutoUnlockHelper/ToastPrompt.cs`：

```csharp
using System.Globalization;
using AutoUnlockService.Session;
using CommunityToolkit.WinUI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AutoUnlockHelper;

// Envoltorio del Toast con dos botones. El proceso sigue vivo, asi que los eventos
// Activated y Dismissed llegan en proceso y no hace falta registrar un activador COM.
// ToastNotificationManagerCompat se encarga del AUMID y del acceso directo del menu
// Inicio, que Windows exige a las aplicaciones sin empaquetar
internal sealed class ToastPrompt
{
    private readonly ToastNotifier _notifier = ToastNotificationManagerCompat.CreateToastNotifier();
    private readonly object _lock = new();
    private ToastNotification? _current;
    private int _currentId;
    private bool _answered;

    public event Action<int, PromptOutcome>? Responded;

    public void Show(int id, double timeoutSeconds, double snoozeMinutes)
    {
        var xml = new XmlDocument();
        xml.LoadXml(string.Format(CultureInfo.InvariantCulture, """
            <toast scenario="reminder" launch="ignore">
              <visual>
                <binding template="ToastGeneric">
                  <text>即将锁定屏幕</text>
                  <text>未检测到你的设备，{0:0} 秒后将自动锁屏。</text>
                </binding>
              </visual>
              <actions>
                <action content="拒绝" arguments="reject" activationType="foreground"/>
                <action content="{1:0} 分钟内不锁定" arguments="snooze" activationType="foreground"/>
              </actions>
            </toast>
            """, timeoutSeconds, snoozeMinutes));

        var toast = new ToastNotification(xml) { Tag = "autounlock-lock-prompt" };
        toast.Activated += OnActivated;
        toast.Dismissed += OnDismissed;

        lock (_lock)
        {
            HideCurrentLocked();
            _current = toast;
            _currentId = id;
            _answered = false;
        }

        _notifier.Show(toast);
    }

    public void Cancel(int id)
    {
        lock (_lock)
        {
            if (_currentId != id) return;
            _answered = true;      // cancelado por el servicio: no se responde nada
            HideCurrentLocked();
        }
    }

    // Aviso sin botones, usado al entrar en estado parado
    public void Notify(string message)
    {
        var xml = new XmlDocument();
        xml.LoadXml($"""
            <toast launch="ignore">
              <visual>
                <binding template="ToastGeneric">
                  <text>AutoUnlock</text>
                  <text>{System.Security.SecurityElement.Escape(message)}</text>
                </binding>
              </visual>
            </toast>
            """);
        _notifier.Show(new ToastNotification(xml));
    }

    private void OnActivated(ToastNotification sender, object args)
    {
        var arguments = (args as ToastActivatedEventArgs)?.Arguments ?? "";
        var outcome = arguments switch
        {
            "reject" => PromptOutcome.Reject,
            "snooze" => PromptOutcome.Snooze,
            _ => PromptOutcome.Dismissed
        };
        Answer(outcome);
    }

    private void OnDismissed(ToastNotification sender, ToastDismissedEventArgs args)
    {
        // ApplicationHidden es nuestra propia cancelacion: no es respuesta del usuario
        if (args.Reason == ToastDismissalReason.ApplicationHidden) return;
        Answer(PromptOutcome.Dismissed);
    }

    private void Answer(PromptOutcome outcome)
    {
        int id;
        lock (_lock)
        {
            if (_answered) return;
            _answered = true;
            id = _currentId;
        }
        Responded?.Invoke(id, outcome);
    }

    private void HideCurrentLocked()
    {
        if (_current is null) return;
        try { _notifier.Hide(_current); } catch { /* ya no estaba */ }
        _current.Activated -= OnActivated;
        _current.Dismissed -= OnDismissed;
        _current = null;
    }
}
```

- [ ] **Step 4: 写助手主程序**

`src/AutoUnlockHelper/Program.cs`：

```csharp
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using AutoUnlockService.Session;

namespace AutoUnlockHelper;

internal static class Program
{
    private const string PipeName = "AutoUnlockHelper";

    private static StreamWriter? _writer;
    private static ToastPrompt? _prompt;
    private static CancellationTokenSource? _promptTimeout;

    private static async Task Main()
    {
        _prompt = new ToastPrompt();
        _prompt.Responded += OnResponded;

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(30));

        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await _writer.WriteAsync(HelperProtocol.BuildHello(Process.GetCurrentProcess().SessionId));

        using var reader = new StreamReader(pipe, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!HelperProtocol.TryParseCommand(line, out var command)) continue;

            switch (command)
            {
                case HelperCommand.Prompt p:
                    StartPrompt(p);
                    break;

                case HelperCommand.Cancel c:
                    _promptTimeout?.Cancel();
                    _prompt.Cancel(c.Id);
                    break;

                case HelperCommand.Lock:
                    ScreenLock.Lock();
                    await _writer.WriteAsync(HelperProtocol.BuildLocked());
                    break;

                case HelperCommand.Notify n:
                    _prompt.Notify(n.Message);
                    break;
            }
        }
        // El servicio cerro el canal: el ayudante termina y sera relanzado si hace falta
    }

    private static void StartPrompt(HelperCommand.Prompt p)
    {
        _promptTimeout?.Cancel();
        _promptTimeout = new CancellationTokenSource();
        var token = _promptTimeout.Token;

        _prompt!.Show(p.Id, p.TimeoutSeconds, p.SnoozeMinutes);

        // El propio ayudante vigila el plazo; el servicio tiene ademas el suyo
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(p.TimeoutSeconds), token); }
            catch (OperationCanceledException) { return; }
            OnResponded(p.Id, PromptOutcome.Timeout);
        }, token);
    }

    private static void OnResponded(int id, PromptOutcome outcome)
    {
        _promptTimeout?.Cancel();
        try { _writer?.Write(HelperProtocol.BuildResponse(id, outcome)); }
        catch { /* el canal murio; el servicio aplicara su propio plazo */ }
    }
}
```

- [ ] **Step 5: 加入解决方案并编译**

Run:
```
dotnet sln AutoUnlockMyPc.sln add src/AutoUnlockHelper/AutoUnlockHelper.csproj
dotnet build src/AutoUnlockHelper/AutoUnlockHelper.csproj -c Release
```
Expected: 编译成功。

- [ ] **Step 6: 验证 Toast 能弹出且按钮能回调**

未打包的应用要显示 Toast，必须在开始菜单有一个带 AppUserModelID 的快捷方式。`ToastNotificationManagerCompat` 会在首次调用时**自动创建**它——所以这里不需要手工建快捷方式，也不需要安装脚本去写 `System.AppUserModel.ID`（用 PowerShell 写这个属性要手搓 `IPropertyStore` COM 互操作，又长又容易出错，这正是选用这个包的原因）。

用一个临时的管道服务端驱动助手（在仓库根目录新建 `scratch-helper-test.ps1`，验证完删掉）：

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeServerStream('AutoUnlockHelper','InOut',1,'Byte','Asynchronous')
Start-Process (Resolve-Path '.\src\AutoUnlockHelper\bin\x64\Release\net10.0-windows10.0.19041.0\AutoUnlockHelper.exe')
$pipe.WaitForConnection()
$writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($pipe)
Write-Host "收到：$($reader.ReadLine())"       # HELLO <sesion>
$writer.Write("PROMPT 1 15 5`n")
Write-Host "点击 Toast 上的按钮…"
Write-Host "收到：$($reader.ReadLine())"       # RESPONSE 1 REJECT / SNOOZE / ...
$pipe.Dispose()
```

Expected: 屏幕右下角出现带「拒绝」和「5 分钟内不锁定」两个按钮的通知；点任一按钮，脚本打印出对应的 `RESPONSE`；什么都不点等 15 秒，打印 `RESPONSE 1 TIMEOUT`。

**如果 Toast 根本不显示**：到 Windows 设置 → 系统 → 通知，确认「AutoUnlockHelper」未被禁用，且专注助手（勿扰模式）处于关闭状态。

**如果 Toast 显示但按钮点击没有触发 `RESPONSE`**：说明这台机器上按钮激活必须经过 COM 激活器，而不是进程内事件。改为订阅 `ToastNotificationManagerCompat.OnActivated`（它带 `ToastArguments`，`ToastPrompt.OnActivated` 的映射逻辑可原样复用），并把 `<action>` 的 `activationType` 从 `foreground` 改为 `background`。协议、助手主程序和服务侧全部不受影响。

验证完删除 `scratch-helper-test.ps1`。

- [ ] **Step 7: Commit**

```bash
git add src/AutoUnlockHelper AutoUnlockMyPc.sln
git commit -m "feat(helper): 用户会话助手，Toast 通知与锁屏执行"
```

---

### Task 15: 通知与锁屏抽象、降级逻辑

两个抽象都在服务侧，各有「经助手」与「不经助手」两套实现。本任务先做抽象、降级外壳，以及不依赖助手的那套（`WTSSendMessageW` 对话框 + `CreateProcessAsUser` 锁屏）——它们是助手挂掉时的兜底，必须先能独立工作。

**Review Focus 第 5 条在这里落实**：助手在倒计时途中崩溃时，不能让服务干等到超时才锁屏（用户全程没看到任何提示）。`ResilientNotifier` 捕获助手不可用，**立刻用剩余时间改走对话框重新提示**。

**Files:**
- Create: `src/AutoUnlockService/Notifications/INotifier.cs`
- Create: `src/AutoUnlockService/Notifications/ResilientNotifier.cs`
- Create: `src/AutoUnlockService/Notifications/WtsMessageNotifier.cs`
- Create: `src/AutoUnlockService/Session/IScreenLocker.cs`
- Create: `src/AutoUnlockService/Session/ProcessScreenLocker.cs`
- Create: `src/AutoUnlockService/Session/WtsNative.cs`
- Create: `tests/AutoUnlockService.Tests/ResilientNotifierTests.cs`

**Interfaces:**
- Consumes: `PromptOutcome`（Task 13）
- Produces:
  - `interface INotifier`：`Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)`、`Task CancelAsync()`、`Task NotifyAsync(string message, CancellationToken ct)`
  - `sealed class HelperUnavailableException : Exception`
  - `sealed class ResilientNotifier : INotifier`：构造 `(INotifier primary, INotifier fallback, ILogger<ResilientNotifier> log, TimeProvider time)`；属性 `bool UsingFallback { get; }`
  - `sealed class WtsMessageNotifier : INotifier`
  - `interface IScreenLocker`：`Task<bool> LockAsync(CancellationToken ct)`
  - `sealed class ProcessScreenLocker : IScreenLocker`
  - `static class WtsNative`：`uint GetActiveConsoleSessionId()`、`bool TryQueryUserToken(uint sessionId, out SafeAccessTokenHandle token)`

- [ ] **Step 1: 写抽象**

`src/AutoUnlockService/Notifications/INotifier.cs`：

```csharp
using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Se lanza cuando el canal del ayudante no esta disponible y hay que degradar
public sealed class HelperUnavailableException(string message) : Exception(message);

public interface INotifier
{
    // Devuelve como respondio el usuario, o Timeout si se agoto el plazo
    Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct);

    // La señal volvio: retirar el aviso pendiente si la implementacion puede
    Task CancelAsync();

    // Aviso sin botones (p. ej. al entrar en estado parado)
    Task NotifyAsync(string message, CancellationToken ct);
}
```

`src/AutoUnlockService/Session/IScreenLocker.cs`：

```csharp
namespace AutoUnlockService.Session;

public interface IScreenLocker
{
    Task<bool> LockAsync(CancellationToken ct);
}
```

- [ ] **Step 2: 写降级外壳的失败测试**

`tests/AutoUnlockService.Tests/ResilientNotifierTests.cs`：

```csharp
using AutoUnlockService.Notifications;
using AutoUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class ResilientNotifierTests
{
    private sealed class FakeNotifier : INotifier
    {
        public PromptOutcome Outcome { get; set; } = PromptOutcome.Reject;
        public Exception? Throws { get; set; }
        public int PromptCalls { get; private set; }
        public TimeSpan LastTimeout { get; private set; }
        public int CancelCalls { get; private set; }
        public List<string> Notifications { get; } = [];
        public TimeSpan DelayBeforeThrow { get; set; }
        public FakeTimeProvider? Time { get; set; }

        public async Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
        {
            PromptCalls++;
            LastTimeout = timeout;
            if (Throws is not null)
            {
                if (DelayBeforeThrow > TimeSpan.Zero && Time is not null)
                    Time.Advance(DelayBeforeThrow);
                await Task.Yield();
                throw Throws;
            }
            return Outcome;
        }

        public Task CancelAsync() { CancelCalls++; return Task.CompletedTask; }

        public Task NotifyAsync(string message, CancellationToken ct)
        {
            Notifications.Add(message);
            return Task.CompletedTask;
        }
    }

    private static (ResilientNotifier N, FakeNotifier Primary, FakeNotifier Fallback, FakeTimeProvider Time) Build()
    {
        var time = new FakeTimeProvider();
        var primary = new FakeNotifier { Time = time };
        var fallback = new FakeNotifier { Time = time };
        var n = new ResilientNotifier(primary, fallback, NullLogger<ResilientNotifier>.Instance, time);
        return (n, primary, fallback, time);
    }

    [Fact]
    public async Task Usa_el_primario_mientras_funcione()
    {
        var (n, primary, fallback, _) = Build();
        primary.Outcome = PromptOutcome.Snooze;

        var result = await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(PromptOutcome.Snooze, result);
        Assert.Equal(1, primary.PromptCalls);
        Assert.Equal(0, fallback.PromptCalls);
        Assert.False(n.UsingFallback);
    }

    [Fact]
    public async Task Si_el_ayudante_cae_reintenta_en_el_acto_con_el_respaldo()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        fallback.Outcome = PromptOutcome.Reject;

        var result = await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(PromptOutcome.Reject, result);
        Assert.Equal(1, fallback.PromptCalls);
        Assert.True(n.UsingFallback);
    }

    [Fact]
    public async Task El_respaldo_recibe_el_tiempo_que_quedaba_no_el_plazo_entero()
    {
        var (n, primary, fallback, time) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        primary.DelayBeforeThrow = TimeSpan.FromSeconds(6);

        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(TimeSpan.FromSeconds(9), fallback.LastTimeout);
    }

    [Fact]
    public async Task Si_ya_no_queda_tiempo_el_respaldo_recibe_un_plazo_minimo()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        primary.DelayBeforeThrow = TimeSpan.FromSeconds(30);

        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.True(fallback.LastTimeout >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Una_vez_degradado_no_vuelve_a_intentar_el_primario()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new HelperUnavailableException("ayudante caido");
        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.Equal(1, primary.PromptCalls);
        Assert.Equal(2, fallback.PromptCalls);
    }

    [Fact]
    public async Task Un_fallo_que_no_es_del_ayudante_no_degrada()
    {
        var (n, primary, fallback, _) = Build();
        primary.Throws = new InvalidOperationException("otra cosa");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => n.PromptAsync(TimeSpan.FromSeconds(15), 5, default));
        Assert.False(n.UsingFallback);
        Assert.Equal(0, fallback.PromptCalls);
    }

    [Fact]
    public async Task Cancel_y_Notify_siguen_a_la_implementacion_activa()
    {
        var (n, primary, fallback, _) = Build();

        await n.CancelAsync();
        await n.NotifyAsync("hola", default);
        Assert.Equal(1, primary.CancelCalls);
        Assert.Single(primary.Notifications);

        primary.Throws = new HelperUnavailableException("ayudante caido");
        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        await n.CancelAsync();
        await n.NotifyAsync("adios", default);
        Assert.Equal(1, fallback.CancelCalls);
        Assert.Single(fallback.Notifications);
    }

    [Fact]
    public async Task Degradar_se_puede_forzar_desde_fuera()
    {
        var (n, primary, fallback, _) = Build();

        n.Degrade("助手连续启动失败");
        await n.PromptAsync(TimeSpan.FromSeconds(15), 5, default);

        Assert.True(n.UsingFallback);
        Assert.Equal(0, primary.PromptCalls);
        Assert.Equal(1, fallback.PromptCalls);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/AutoUnlockService.Tests --filter ResilientNotifierTests -v minimal`
Expected: 编译失败，提示 `ResilientNotifier` 不存在。

- [ ] **Step 4: 写降级外壳**

`src/AutoUnlockService/Notifications/ResilientNotifier.cs`：

```csharp
using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Mantiene el aviso funcionando aunque el ayudante desaparezca: la degradacion es
// automatica y definitiva dentro de la vida del servicio
public sealed class ResilientNotifier(
    INotifier primary,
    INotifier fallback,
    ILogger<ResilientNotifier> log,
    TimeProvider time) : INotifier
{
    private static readonly TimeSpan MinimumFallbackTimeout = TimeSpan.FromSeconds(5);

    private int _degraded;

    public bool UsingFallback => Volatile.Read(ref _degraded) != 0;

    private INotifier Active => UsingFallback ? fallback : primary;

    public void Degrade(string reason)
    {
        if (Interlocked.Exchange(ref _degraded, 1) == 0)
            log.LogWarning("通知方式已降级为对话框：{Reason}", reason);
    }

    public async Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
    {
        if (UsingFallback)
            return await fallback.PromptAsync(timeout, snoozeMinutes, ct);

        var startedAt = time.GetUtcNow();
        try
        {
            return await primary.PromptAsync(timeout, snoozeMinutes, ct);
        }
        catch (HelperUnavailableException ex)
        {
            Degrade(ex.Message);

            // El usuario no llego a ver nada: se vuelve a preguntar ya mismo con lo
            // que quedaba de plazo, en vez de dejarle sin aviso hasta el bloqueo
            var elapsed = time.GetUtcNow() - startedAt;
            var remaining = timeout - elapsed;
            if (remaining < MinimumFallbackTimeout) remaining = MinimumFallbackTimeout;

            return await fallback.PromptAsync(remaining, snoozeMinutes, ct);
        }
    }

    public Task CancelAsync() => Active.CancelAsync();

    public Task NotifyAsync(string message, CancellationToken ct) => Active.NotifyAsync(message, ct);
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/AutoUnlockService.Tests --filter ResilientNotifierTests -v minimal`
Expected: 全部通过。

- [ ] **Step 6: 写 WTS 原生封装**

`src/AutoUnlockService/Session/WtsNative.cs`：

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoUnlockService.Session;

// Puente entre la sesion 0 del servicio y la sesion interactiva del usuario
public static class WtsNative
{
    public const uint InvalidSession = 0xFFFFFFFF;

    public static uint GetActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    public static bool TryQueryUserToken(uint sessionId, out SafeAccessTokenHandle token)
    {
        if (WTSQueryUserToken(sessionId, out token)) return true;
        token.Dispose();
        return false;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle phToken);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WTSSendMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSSendMessage(
        IntPtr hServer, uint sessionId,
        string pTitle, uint titleLength,
        string pMessage, uint messageLength,
        uint style, uint timeoutSeconds,
        out uint pResponse, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    internal static readonly IntPtr CurrentServer = IntPtr.Zero;
}
```

- [ ] **Step 7: 写对话框通知**

`src/AutoUnlockService/Notifications/WtsMessageNotifier.cs`：

```csharp
using System.Globalization;
using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Implementacion sin ayudante: WTSSendMessage esta pensado justo para esto,
// un servicio que pregunta algo con plazo en la sesion del usuario.
// No la afecta el modo "no molestar", pero el dialogo no se puede cerrar desde fuera
public sealed class WtsMessageNotifier(ILogger<WtsMessageNotifier> log) : INotifier
{
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconExclamation = 0x00000030;
    private const uint MbSystemModal = 0x00001000;
    private const uint IdYes = 6;
    private const uint IdNo = 7;

    public Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
    {
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession)
        {
            log.LogWarning("没有活动的控制台会话，无法弹出提示，按超时处理");
            return Task.FromResult(PromptOutcome.Timeout);
        }

        const string title = "AutoUnlock";
        var message = string.Format(CultureInfo.InvariantCulture,
            "未检测到你的设备，{0:0} 秒后将自动锁屏。\n\n" +
            "「是」= 本次不锁屏\n" +
            "「否」= {1:0} 分钟内不锁屏",
            timeout.TotalSeconds, snoozeMinutes);

        return Task.Run(() =>
        {
            bool ok = WtsNative.WTSSendMessage(
                WtsNative.CurrentServer, sessionId,
                title, (uint)(title.Length * 2),
                message, (uint)(message.Length * 2),
                MbYesNo | MbIconExclamation | MbSystemModal,
                (uint)timeout.TotalSeconds,
                out uint response,
                bWait: true);

            if (!ok)
            {
                log.LogWarning("WTSSendMessage 调用失败，按超时处理");
                return PromptOutcome.Timeout;
            }

            return response switch
            {
                IdYes => PromptOutcome.Reject,
                IdNo  => PromptOutcome.Snooze,
                _     => PromptOutcome.Timeout
            };
        }, ct);
    }

    // El dialogo clasico no se puede retirar desde fuera: esta limitacion es conocida
    // y esta documentada en el diseño
    public Task CancelAsync() => Task.CompletedTask;

    public Task NotifyAsync(string message, CancellationToken ct)
    {
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession) return Task.CompletedTask;

        const string title = "AutoUnlock";
        const uint mbOk = 0x00000000;
        WtsNative.WTSSendMessage(
            WtsNative.CurrentServer, sessionId,
            title, (uint)(title.Length * 2),
            message, (uint)(message.Length * 2),
            mbOk | MbIconExclamation, 0, out _, bWait: false);

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 8: 写不经助手的锁屏**

`src/AutoUnlockService/Session/ProcessScreenLocker.cs`：

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoUnlockService.Session;

// Un servicio en la sesion 0 no puede llamar a LockWorkStation: hay que ejecutarlo
// dentro de la sesion del usuario con su propio token
public sealed class ProcessScreenLocker(ILogger<ProcessScreenLocker> log) : IScreenLocker
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    public Task<bool> LockAsync(CancellationToken ct) => Task.FromResult(Lock());

    private bool Lock()
    {
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession)
        {
            log.LogWarning("没有活动的控制台会话，无法锁屏");
            return false;
        }

        if (!WtsNative.TryQueryUserToken(sessionId, out var token))
        {
            log.LogWarning("获取用户令牌失败，错误码 {Error}，无法锁屏", Marshal.GetLastWin32Error());
            return false;
        }

        using (token)
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var command = $@"{Environment.SystemDirectory}\rundll32.exe user32.dll,LockWorkStation";

            bool ok = CreateProcessAsUserW(
                token, null, command,
                IntPtr.Zero, IntPtr.Zero, false,
                CreateUnicodeEnvironment | CreateNoWindow,
                IntPtr.Zero, null, ref si, out var pi);

            if (!ok)
            {
                log.LogWarning("CreateProcessAsUser 失败，错误码 {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            log.LogInformation("已在会话 {Session} 中执行锁屏", sessionId);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
```

- [ ] **Step 9: 编译确认无误**

Run: `dotnet build src/AutoUnlockService/AutoUnlockService.csproj -c Release`
Expected: 编译成功。这两个 Win32 实现在 Task 19 的实机验收第 9 项（杀助手降级）里一并验证。

- [ ] **Step 10: Commit**

```bash
git add src/AutoUnlockService/Notifications src/AutoUnlockService/Session tests/AutoUnlockService.Tests/ResilientNotifierTests.cs
git commit -m "feat(notify): 通知与锁屏抽象、自动降级外壳、对话框与进程锁屏实现"
```

---

### Task 16: 助手管道、进程守护与会话事件

把助手真正接起来：管道服务端、基于它的 `INotifier` 与 `IScreenLocker` 实现、负责拉起和守护助手的 `HelperLauncher`，以及从 SCM 拿锁屏/解锁与电源恢复通知的 `SessionMonitor`。

本任务先把 Task 15 里 `ProcessScreenLocker` 内嵌的 `CreateProcessAsUser` 抽出来共用——`HelperLauncher` 要做同一件事，没必要有两份 P/Invoke。

**Files:**
- Create: `src/AutoUnlockService/Session/UserSessionProcess.cs`
- Modify: `src/AutoUnlockService/Session/ProcessScreenLocker.cs`（改为调用上面那个）
- Create: `src/AutoUnlockService/Session/HelperPipeServer.cs`
- Create: `src/AutoUnlockService/Session/HelperScreenLocker.cs`
- Create: `src/AutoUnlockService/Session/HelperLauncher.cs`
- Create: `src/AutoUnlockService/Session/SessionMonitor.cs`
- Create: `src/AutoUnlockService/Notifications/HelperNotifier.cs`

**Interfaces:**
- Consumes: `HelperProtocol`（Task 13）、`INotifier`、`IScreenLocker`、`HelperUnavailableException`、`ResilientNotifier`、`WtsNative`（Task 15）
- Produces:
  - `static class UserSessionProcess`：`int? StartInActiveSession(string commandLine, ILogger log)`
  - `sealed class HelperPipeServer : IAsyncDisposable`：`bool IsConnected`、事件 `Connected(int sessionId)` / `Disconnected` / `ResponseReceived(int id, PromptOutcome)`、方法 `void Start(CancellationToken)`、`Task<bool> SendAsync(string line, CancellationToken)`、`Task<bool> WaitForLockedAsync(TimeSpan, CancellationToken)`
  - `sealed class HelperNotifier : INotifier`
  - `sealed class HelperScreenLocker : IScreenLocker`
  - `sealed class HelperLauncher`：`void Start(CancellationToken)`、事件 `Action<string>? DegradeRequested`
  - `sealed class SessionMonitor : WindowsServiceLifetime`：事件 `SessionLocked` / `SessionUnlocked` / `Resumed`

- [ ] **Step 1: 抽出在用户会话里启动进程的能力**

`src/AutoUnlockService/Session/UserSessionProcess.cs`：

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoUnlockService.Session;

// Un servicio en la sesion 0 no puede crear ventanas ni bloquear la pantalla:
// todo eso se ejecuta con el token del usuario de la sesion interactiva
public static class UserSessionProcess
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    public static int? StartInActiveSession(string commandLine, ILogger log)
    {
        var sessionId = WtsNative.GetActiveConsoleSessionId();
        if (sessionId == WtsNative.InvalidSession)
        {
            log.LogWarning("没有活动的控制台会话，无法在用户会话中启动进程");
            return null;
        }

        if (!WtsNative.TryQueryUserToken(sessionId, out var token))
        {
            log.LogWarning("获取用户令牌失败，错误码 {Error}", Marshal.GetLastWin32Error());
            return null;
        }

        using (token)
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };

            bool ok = CreateProcessAsUserW(
                token, null, commandLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CreateUnicodeEnvironment | CreateNoWindow,
                IntPtr.Zero, null, ref si, out var pi);

            if (!ok)
            {
                log.LogWarning("CreateProcessAsUser 失败，错误码 {Error}", Marshal.GetLastWin32Error());
                return null;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
```

- [ ] **Step 2: 让 ProcessScreenLocker 改用它**

`src/AutoUnlockService/Session/ProcessScreenLocker.cs` 全文替换为：

```csharp
namespace AutoUnlockService.Session;

// Respaldo de bloqueo cuando el ayudante no esta disponible
public sealed class ProcessScreenLocker(ILogger<ProcessScreenLocker> log) : IScreenLocker
{
    public Task<bool> LockAsync(CancellationToken ct)
    {
        var command = $@"{Environment.SystemDirectory}\rundll32.exe user32.dll,LockWorkStation";
        bool ok = UserSessionProcess.StartInActiveSession(command, log) is not null;
        if (ok) log.LogInformation("已通过 rundll32 在用户会话中锁屏");
        return Task.FromResult(ok);
    }
}
```

- [ ] **Step 3: 写助手管道服务端**

`src/AutoUnlockService/Session/HelperPipeServer.cs`：

```csharp
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AutoUnlockService.Session;

// Servidor de \\.\pipe\AutoUnlockHelper. El ayudante es el cliente y se reconecta
// cada vez que lo relanzamos
public sealed class HelperPipeServer(ILogger<HelperPipeServer> log) : IAsyncDisposable
{
    private const string PipeName = "AutoUnlockHelper";

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private TaskCompletionSource<bool>? _lockedAck;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsConnected => _pipe?.IsConnected == true;

    public event Action<int>? Connected;
    public event Action? Disconnected;
    public event Action<int, PromptOutcome>? ResponseReceived;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = CreatePipe();
                await _pipe.WaitForConnectionAsync(ct);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };

                using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
                while (!ct.IsCancellationRequested && _pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning("助手管道异常：{Message}", ex.Message);
            }
            finally
            {
                var pipe = _pipe;
                _pipe = null;
                _writer = null;
                if (pipe is not null) await pipe.DisposeAsync();

                // Cualquier espera pendiente debe romperse, no quedarse colgada
                _lockedAck?.TrySetResult(false);
                Disconnected?.Invoke();
            }
        }
    }

    private void Dispatch(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("HELLO ", StringComparison.Ordinal)
            && int.TryParse(trimmed[6..], out var sessionId))
        {
            log.LogInformation("助手已连接，会话 {Session}", sessionId);
            Connected?.Invoke(sessionId);
            return;
        }

        if (trimmed == "LOCKED")
        {
            _lockedAck?.TrySetResult(true);
            return;
        }

        if (HelperProtocol.TryParseResponse(trimmed, out var id, out var outcome))
        {
            log.LogInformation("用户对锁屏提示的响应：{Outcome}", outcome);
            ResponseReceived?.Invoke(id, outcome);
            return;
        }

        log.LogWarning("助手管道收到无法识别的消息");
    }

    public async Task<bool> SendAsync(string line, CancellationToken ct)
    {
        var writer = _writer;
        if (writer is null || !IsConnected) return false;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (line == HelperProtocol.BuildLock())
                _lockedAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await writer.WriteAsync(line.AsMemory(), ct);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning("向助手发送指令失败：{Message}", ex.Message);
            return false;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> WaitForLockedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var ack = _lockedAck;
        if (ack is null) return false;

        var completed = await Task.WhenAny(ack.Task, Task.Delay(timeout, ct));
        return completed == ack.Task && ack.Task.Result;
    }

    // SYSTEM (el servicio) y el usuario interactivo (el ayudante)
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 4096, outBufferSize: 4096, security);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (_pipe is not null) await _pipe.DisposeAsync();
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}
```

- [ ] **Step 4: 写经助手的通知实现**

`src/AutoUnlockService/Notifications/HelperNotifier.cs`：

```csharp
using AutoUnlockService.Session;

namespace AutoUnlockService.Notifications;

// Delega el aviso en el ayudante, que es quien puede mostrar un Toast de verdad
public sealed class HelperNotifier(HelperPipeServer pipe, ILogger<HelperNotifier> log) : INotifier
{
    private readonly object _lock = new();
    private TaskCompletionSource<PromptOutcome>? _pending;
    private int _pendingId;
    private int _nextId;

    public void Attach()
    {
        pipe.ResponseReceived += OnResponse;
        // Si el ayudante muere a mitad de la cuenta atras, el usuario no ha visto nada:
        // hay que romper la espera para que ResilientNotifier vuelva a preguntar ya
        pipe.Disconnected += OnDisconnected;
    }

    public async Task<PromptOutcome> PromptAsync(TimeSpan timeout, double snoozeMinutes, CancellationToken ct)
    {
        if (!pipe.IsConnected) throw new HelperUnavailableException("助手未连接");

        TaskCompletionSource<PromptOutcome> tcs;
        int id;
        lock (_lock)
        {
            id = ++_nextId;
            _pendingId = id;
            _pending = tcs = new TaskCompletionSource<PromptOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (!await pipe.SendAsync(HelperProtocol.BuildPrompt(id, timeout.TotalSeconds, snoozeMinutes), ct))
            throw new HelperUnavailableException("向助手发送提示指令失败");

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    public async Task CancelAsync()
    {
        int id;
        lock (_lock)
        {
            if (_pending is null) return;
            id = _pendingId;
            _pending = null;
        }
        await pipe.SendAsync(HelperProtocol.BuildCancel(id), CancellationToken.None);
    }

    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        if (!await pipe.SendAsync(HelperProtocol.BuildNotify(message), ct))
            throw new HelperUnavailableException("向助手发送通知失败");
    }

    private void OnResponse(int id, PromptOutcome outcome)
    {
        lock (_lock)
        {
            if (_pending is null || id != _pendingId) return;
            var tcs = _pending;
            _pending = null;
            tcs.TrySetResult(outcome);
        }
    }

    private void OnDisconnected()
    {
        lock (_lock)
        {
            if (_pending is null) return;
            var tcs = _pending;
            _pending = null;
            log.LogWarning("助手在提示未决时断开，改用对话框重新提示");
            tcs.TrySetException(new HelperUnavailableException("助手在提示未决时断开"));
        }
    }
}
```

- [ ] **Step 5: 写经助手的锁屏实现**

`src/AutoUnlockService/Session/HelperScreenLocker.cs`：

```csharp
namespace AutoUnlockService.Session;

public sealed class HelperScreenLocker(
    HelperPipeServer pipe, ProcessScreenLocker fallback, ILogger<HelperScreenLocker> log) : IScreenLocker
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> LockAsync(CancellationToken ct)
    {
        if (pipe.IsConnected
            && await pipe.SendAsync(HelperProtocol.BuildLock(), ct)
            && await pipe.WaitForLockedAsync(AckTimeout, ct))
        {
            log.LogInformation("助手已执行锁屏");
            return true;
        }

        // Bloquear la pantalla es lo mas importante del servicio: si el ayudante
        // no confirma, se intenta por la via directa antes de darse por vencido
        log.LogWarning("助手未确认锁屏，改用进程方式");
        return await fallback.LockAsync(ct);
    }
}
```

- [ ] **Step 6: 写助手守护**

`src/AutoUnlockService/Session/HelperLauncher.cs`：

```csharp
namespace AutoUnlockService.Session;

// Mantiene vivo el ayudante en la sesion interactiva. Tras varios fallos seguidos
// pide degradar en vez de dejar el servicio sin avisos
public sealed class HelperLauncher(
    HelperPipeServer pipe, ILogger<HelperLauncher> log, TimeProvider time) : IDisposable
{
    private const int MaxConsecutiveFailures = 3;
    private static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFailures;

    public event Action<string>? DegradeRequested;

    public void Start(CancellationToken ct)
    {
        pipe.Connected += _ => Interlocked.Exchange(ref _consecutiveFailures, 0);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "AutoUnlockHelper.exe");

        while (!ct.IsCancellationRequested)
        {
            if (!pipe.IsConnected && _consecutiveFailures < MaxConsecutiveFailures)
            {
                if (UserSessionProcess.StartInActiveSession($"\"{exePath}\"", log) is null)
                {
                    int failures = Interlocked.Increment(ref _consecutiveFailures);
                    log.LogWarning("启动助手失败（第 {Count} 次）", failures);

                    if (failures >= MaxConsecutiveFailures)
                        DegradeRequested?.Invoke($"助手连续 {MaxConsecutiveFailures} 次启动失败");
                }
                else
                {
                    log.LogInformation("助手进程已启动，等待连接");
                }
            }

            try { await Task.Delay(RelaunchDelay, time, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
```

注意 `_consecutiveFailures` 只在**启动失败**时增加；助手被杀掉后能正常重启的情况下计数会在 `Connected` 时清零，所以反复手杀助手不会误触发降级，只有真的起不来才会。

- [ ] **Step 7: 写会话与电源事件监听**

`src/AutoUnlockService/Session/SessionMonitor.cs`：

```csharp
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace AutoUnlockService.Session;

// Sustituye al ciclo de vida por defecto para poder pedirle al SCM los avisos de
// bloqueo, desbloqueo y reanudacion tras suspension
public sealed class SessionMonitor : WindowsServiceLifetime
{
    public SessionMonitor(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor)
    {
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        ServiceName = "AutoUnlockService";
    }

    public event Action? SessionLocked;
    public event Action? SessionUnlocked;
    public event Action? Resumed;

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

        switch (changeDescription.Reason)
        {
            case SessionChangeReason.SessionLock:
                SessionLocked?.Invoke();
                break;
            case SessionChangeReason.SessionUnlock:
            case SessionChangeReason.SessionLogon:
                SessionUnlocked?.Invoke();
                break;
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        // Tras despertar el reloj puede haber saltado horas: el historico ya no sirve
        if (powerStatus is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic)
            Resumed?.Invoke();

        return base.OnPowerEvent(powerStatus);
    }
}
```

`WindowsServiceLifetime` 继承自 `ServiceBase`，所以 `CanHandleSessionChangeEvent` 与 `CanHandlePowerEvent` 必须在服务启动前（构造函数里）设置才有效。

- [ ] **Step 8: 编译确认无误**

Run: `dotnet build src/AutoUnlockService/AutoUnlockService.csproj -c Release`
Expected: 编译成功。需要 `System.ServiceProcess.ServiceController` 包；若编译报找不到 `ServiceBase`，在 csproj 中加入：

```xml
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="9.0.0" />
```

- [ ] **Step 9: Commit**

```bash
git add src/AutoUnlockService/Session src/AutoUnlockService/Notifications/HelperNotifier.cs src/AutoUnlockService/AutoUnlockService.csproj
git commit -m "feat(session): 助手管道、进程守护与降级、会话与电源事件监听"
```

---

### Task 17: Worker 编排与周期摘要日志

把前面所有零件接成一个服务：样本流进状态机，状态机的动作分派到通知、锁屏和凭据推送，配置热更新作用到各处。同时加上调阈值离不开的周期摘要日志。

**Files:**
- Modify: `src/AutoUnlockService/Worker.cs`（全文替换）
- Modify: `src/AutoUnlockService/Program.cs`（全文替换）

**Interfaces:**
- Consumes: 前面全部任务的产物
- Produces: 完整可运行的服务

- [ ] **Step 1: 写编排 Worker**

`src/AutoUnlockService/Worker.cs` 全文替换为：

```csharp
using AutoUnlockService.Ble;
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using AutoUnlockService.Credentials;
using AutoUnlockService.Notifications;
using AutoUnlockService.Session;

namespace AutoUnlockService;

public sealed class Worker(
    ILogger<Worker> log,
    TimeProvider time,
    OptionsGate options,
    IRssiSource source,
    DeviceMatcher matcher,
    CredentialValidator validator,
    CredentialPipeServer credentialPipe,
    HelperPipeServer helperPipe,
    HelperLauncher launcher,
    HelperNotifier helperNotifier,
    ResilientNotifier notifier,
    IScreenLocker locker,
    SessionMonitor sessions) : BackgroundService
{
    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromSeconds(120);

    private readonly RssiHistory _history = new(HistoryRetention);
    private readonly object _gate = new();
    private ProximityStateMachine _machine = null!;
    private DateTimeOffset _lastSummary;
    private DateTimeOffset _lastMatch;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var current = options.Current;
        matcher.Configure(current.Device);
        _machine = new ProximityStateMachine(time, current.Proximity, current.Notification, false);
        _lastSummary = time.GetUtcNow();
        _lastMatch = time.GetUtcNow();

        helperNotifier.Attach();
        helperPipe.Start(ct);
        launcher.Start(ct);
        credentialPipe.Start(ct);
        await source.StartAsync(ct);

        source.SampleReceived += OnSample;
        source.StatusChanged += s => log.LogInformation("BLE 状态：{Status}", s);
        credentialPipe.AuthResultReceived += r => Transition(() => _machine.OnAuthResult(r));
        launcher.DegradeRequested += notifier.Degrade;
        sessions.SessionLocked += () => Transition(() => _machine.OnSessionEvent(SessionEvent.Lock));
        sessions.SessionUnlocked += () => Transition(() => _machine.OnSessionEvent(SessionEvent.Unlock));
        sessions.Resumed += OnResumed;
        options.Changed += OnOptionsChanged;

        // Nota: la respuesta del aviso NO se suscribe aqui. HelperNotifier ya consume
        // helperPipe.ResponseReceived y la devuelve como resultado de PromptAsync;
        // suscribirse tambien aqui llamaria a la maquina de estados dos veces

        // La prevalidacion es la primera mitad del doble seguro
        bool credentialsOk = validator.Validate(current.Credentials.UserName, current.Credentials.Password);
        Transition(() => _machine.OnCredentialsChanged(credentialsOk));

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(EvaluateInterval, time, ct); }
            catch (OperationCanceledException) { break; }

            Transition(() => _machine.Evaluate(_history));
            WriteSummaryIfDue();
        }

        source.SampleReceived -= OnSample;
        await source.StopAsync();
    }

    private void OnSample(AdvertisementSample sample)
    {
        if (!matcher.IsTarget(sample)) return;
        _history.Add(sample.Rssi, sample.Timestamp);
        _lastMatch = time.GetUtcNow();
    }

    private void OnResumed()
    {
        // El reloj puede haber saltado horas: el historico ya no representa nada
        log.LogInformation("系统已从睡眠中恢复，清空 RSSI 历史并重新进入宽限期");
        _history.Clear();
        lock (_gate) { _machine.OnResume(); }
    }

    private void OnOptionsChanged(OptionsChange change)
    {
        lock (_gate)
        {
            _machine.Proximity = change.Options.Proximity;
            _machine.Notification = change.Options.Notification;
        }

        if (change.DeviceChanged)
        {
            matcher.Configure(change.Options.Device);
            _history.Reset();
            log.LogInformation("目标设备已变更，解析缓存与 RSSI 历史已清空");
        }

        if (change.CredentialsChanged)
        {
            bool ok = validator.Validate(
                change.Options.Credentials.UserName, change.Options.Credentials.Password);
            Transition(() => _machine.OnCredentialsChanged(ok));
        }
    }

    // La maquina de estados no es thread-safe y aqui la tocan tres hilos distintos:
    // el temporizador de evaluacion, los callbacks de los dos canales y los eventos
    // de sesion. El candado cubre solo el calculo de la transicion; la accion
    // resultante se despacha fuera para no bloquear a los demas
    private void Transition(Func<StateChange?> step)
    {
        StateChange? change;
        lock (_gate) { change = step(); }

        if (change is null) return;
        log.LogInformation("状态 {From} → {To}：{Reason}", change.From, change.To, change.Reason);
        _ = DispatchAsync(change);
    }

    private async Task DispatchAsync(StateChange change)
    {
        try
        {
            switch (change.Action)
            {
                case ProximityAction.ShowLockPrompt:
                    var n = options.Current.Notification;
                    var outcome = await notifier.PromptAsync(
                        TimeSpan.FromSeconds(n.PromptTimeoutSeconds), n.SnoozeMinutes, CancellationToken.None);
                    Transition(() => _machine.OnPromptResponse(ToPromptResponse(outcome)));
                    break;

                case ProximityAction.CancelLockPrompt:
                    await notifier.CancelAsync();
                    break;

                case ProximityAction.LockScreen:
                    await notifier.CancelAsync();
                    await locker.LockAsync(CancellationToken.None);
                    break;

                case ProximityAction.SendCredentials:
                    var c = options.Current.Credentials;
                    await credentialPipe.SendCredentialsAsync(c.UserName, c.Password, CancellationToken.None);
                    break;

                case ProximityAction.NotifySuspended:
                    await notifier.NotifyAsync(change.Reason + "，请修改 appsettings.json 后自动恢复",
                        CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "执行动作 {Action} 时出错", change.Action);
        }
    }

    private static PromptResponse ToPromptResponse(PromptOutcome outcome) => outcome switch
    {
        PromptOutcome.Reject => PromptResponse.Reject,
        PromptOutcome.Snooze => PromptResponse.Snooze,
        PromptOutcome.Dismissed => PromptResponse.Dismissed,
        _ => PromptResponse.Timeout
    };

    // Sin este resumen habria que activar el nivel Debug para ajustar los umbrales,
    // y eso llena el registro con una linea por emision recibida
    private void WriteSummaryIfDue()
    {
        var now = time.GetUtcNow();
        if (now - _lastSummary < SummaryInterval) return;
        _lastSummary = now;

        var p = options.Current.Proximity;
        var stats = _history.InWindow(now, TimeSpan.FromSeconds(p.UnlockWindowSeconds));

        log.LogInformation(
            "摘要：状态 {State}，最近 {Window:0} 秒 {Count} 个读数，平均 {Average:0.0} dBm，距上次匹配 {Since:0} 秒",
            _machine.State, p.UnlockWindowSeconds, stats.Count,
            stats.Count == 0 ? double.NaN : stats.Average, (now - _lastMatch).TotalSeconds);

        if (now - _lastMatch > TimeSpan.FromMinutes(5))
            log.LogWarning("已有 5 分钟未匹配到目标设备，请检查 IRK 配置或设备是否在范围内");
    }
}
```

- [ ] **Step 2: 写依赖注册**

`src/AutoUnlockService/Program.cs` 全文替换为：

```csharp
using AutoUnlockService.Ble;
using AutoUnlockService.Config;
using AutoUnlockService.Core;
using AutoUnlockService.Credentials;
using AutoUnlockService.Notifications;
using AutoUnlockService.Session;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace AutoUnlockService;

public static class Program
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoUnlock");

    public static void Main(string[] args)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(DataDir, "logs", "autounlock-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .WriteTo.EventLog("AutoUnlock", manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateLogger();

        try
        {
            Log.Information("AutoUnlockService 正在启动");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Configuration.AddJsonFile(
                Path.Combine(DataDir, "appsettings.json"), optional: false, reloadOnChange: true);

            builder.Services.AddWindowsService(o => o.ServiceName = "AutoUnlockService");
            builder.Services.AddSerilog();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.Configure<AutoUnlockOptions>(
                builder.Configuration.GetSection(AutoUnlockOptions.SectionName));

            // Sustituye al ciclo de vida que registra AddWindowsService, para recibir
            // los avisos de bloqueo, desbloqueo y reanudacion
            builder.Services.AddSingleton<SessionMonitor>();
            builder.Services.AddSingleton<IHostLifetime>(sp => sp.GetRequiredService<SessionMonitor>());

            builder.Services.AddSingleton(sp =>
            {
                var initial = sp.GetRequiredService<IOptionsMonitor<AutoUnlockOptions>>().CurrentValue;
                var validation = OptionsValidator.Validate(initial);
                if (!validation.IsValid)
                {
                    foreach (var error in validation.Errors)
                        Log.Fatal("配置无效，服务拒绝启动：{Error}", error);
                    throw new InvalidOperationException("appsettings.json 配置无效");
                }

                var gate = new OptionsGate(
                    initial, sp.GetRequiredService<ILogger<OptionsGate>>(),
                    sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMilliseconds(250));

                sp.GetRequiredService<IOptionsMonitor<AutoUnlockOptions>>()
                  .OnChange(candidate => gate.Submit(candidate));

                return gate;
            });

            builder.Services.AddSingleton<IRssiSource, BleScanner>();
            builder.Services.AddSingleton<DeviceMatcher>();
            builder.Services.AddSingleton<CredentialValidator>();
            builder.Services.AddSingleton<CredentialPipeServer>();
            builder.Services.AddSingleton<HelperPipeServer>();
            builder.Services.AddSingleton<HelperLauncher>();
            builder.Services.AddSingleton<HelperNotifier>();
            builder.Services.AddSingleton<WtsMessageNotifier>();
            builder.Services.AddSingleton<ProcessScreenLocker>();

            builder.Services.AddSingleton(sp => new ResilientNotifier(
                sp.GetRequiredService<HelperNotifier>(),
                sp.GetRequiredService<WtsMessageNotifier>(),
                sp.GetRequiredService<ILogger<ResilientNotifier>>(),
                sp.GetRequiredService<TimeProvider>()));

            builder.Services.AddSingleton<IScreenLocker>(sp => new HelperScreenLocker(
                sp.GetRequiredService<HelperPipeServer>(),
                sp.GetRequiredService<ProcessScreenLocker>(),
                sp.GetRequiredService<ILogger<HelperScreenLocker>>()));

            builder.Services.AddHostedService<Worker>();

            builder.Build().Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "AutoUnlockService 启动失败");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
```

注意 `Notification.Mode` 为 `Dialog` 时的处理：`ResilientNotifier` 构造后，`Worker` 启动时若 `options.Current.Notification.Mode == NotifierMode.Dialog` 就调用一次 `notifier.Degrade("配置指定使用对话框")`。把这行加在 `Worker.ExecuteAsync` 里 `helperNotifier.Attach();` 之前：

```csharp
        if (current.Notification.Mode == NotifierMode.Dialog)
            notifier.Degrade("配置指定使用对话框模式");
```

- [ ] **Step 3: 编译并安装**

Run（管理员 PowerShell）：
```
dotnet build AutoUnlockMyPc.sln -c Release
.\scripts\install.ps1
```
Expected: 编译成功，服务启动。

- [ ] **Step 4: 确认服务起来并在写摘要**

填好 `%ProgramData%\AutoUnlock\appsettings.json` 里的 IRK、用户名和密码后重启服务：

```
Restart-Service AutoUnlockService
Start-Sleep -Seconds 70
Get-Content "$env:ProgramData\AutoUnlock\logs\autounlock-*.log" -Tail 30
```
Expected: 日志出现「凭据预验证通过」「BLE 扫描已启动」「助手已连接，会话 N」，以及每 30 秒一条「摘要：状态 Unlocked，最近 5 秒 N 个读数，平均 -XX.X dBm」。**确认日志里没有密码明文。**

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/Worker.cs src/AutoUnlockService/Program.cs
git commit -m "feat(service): 编排全部组件并加入周期运行摘要日志"
```

---

### Task 18: 完整安装与卸载脚本

把 Task 1 的最小脚本补成正式版：收紧配置文件 ACL、部署助手与 CP、注册 COM、幂等重装。

AUMID 快捷方式不需要脚本处理——`ToastNotificationManagerCompat` 在助手首次弹通知时会自己创建。

**Files:**
- Modify: `scripts/install.ps1`（全文替换）
- Modify: `scripts/uninstall.ps1`（全文替换）
- Modify: `README.md`

**Interfaces:**
- Consumes: 三个构建产物（服务、助手、CP DLL）
- Produces: 幂等的安装与卸载脚本

- [ ] **Step 1: 写完整安装脚本**

`scripts/install.ps1` 全文替换为：

```powershell
#Requires -RunAsAdministrator
<#
    Instala AutoUnlock: servicio, ayudante y proveedor de credenciales.
    Es idempotente: se puede volver a ejecutar para actualizar.
#>
param(
    [string]$Configuration = 'Release',
    [string]$InstallDir    = 'C:\Program Files\AutoUnlock',
    [string]$DataDir       = (Join-Path $env:ProgramData 'AutoUnlock')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AutoUnlockService'
$repoRoot    = Split-Path $PSScriptRoot -Parent
$tfm         = 'net10.0-windows10.0.19041.0'

$serviceOut = Join-Path $repoRoot "src\AutoUnlockService\bin\x64\$Configuration\$tfm"
$helperOut  = Join-Path $repoRoot "src\AutoUnlockHelper\bin\x64\$Configuration\$tfm"
$cpDll      = Join-Path $repoRoot "src\AutoUnlockCredentialProvider\bin\$Configuration\x64\AutoUnlockCredentialProvider.dll"

foreach ($p in @($serviceOut, $helperOut, $cpDll)) {
    if (-not (Test-Path $p)) { throw "未找到构建产物：$p`n请先执行 dotnet build 与 msbuild" }
}

# ── 1. 停止旧服务 ────────────────────────────────────────────────────────────
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host '停止已有服务…'
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 3
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}
Get-Process -Name 'AutoUnlockHelper' -ErrorAction SilentlyContinue | Stop-Process -Force

# ── 2. 目录与文件 ───────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $InstallDir            | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null

Write-Host '拷贝服务与助手…'
Copy-Item -Path (Join-Path $serviceOut '*') -Destination $InstallDir -Recurse -Force
Copy-Item -Path (Join-Path $helperOut  '*') -Destination $InstallDir -Recurse -Force

$targetDll = Join-Path $InstallDir 'AutoUnlockCP.dll'
Copy-Item -Path $cpDll -Destination $targetDll -Force

# ── 3. 配置文件与权限 ───────────────────────────────────────────────────────
$configPath = Join-Path $DataDir 'appsettings.json'
if (-not (Test-Path $configPath)) {
    Copy-Item -Path (Join-Path $serviceOut 'appsettings.json') -Destination $configPath
    Write-Host "已生成配置模板：$configPath"
} else {
    Write-Host "保留已有配置：$configPath"
}
# El fichero de servicio no debe quedarse: el que manda es el de ProgramData
Remove-Item (Join-Path $InstallDir 'appsettings.json') -ErrorAction SilentlyContinue

Write-Host '收紧配置目录权限（仅 SYSTEM 与 Administrators）…'
icacls $DataDir /inheritance:r                       | Out-Null
icacls $DataDir /grant:r 'SYSTEM:(OI)(CI)F'          | Out-Null
icacls $DataDir /grant:r 'Administrators:(OI)(CI)F'  | Out-Null

# ── 4. 注册凭据提供程序 ─────────────────────────────────────────────────────
Write-Host '注册凭据提供程序…'
$proc = Start-Process regsvr32.exe -ArgumentList "/s `"$targetDll`"" -Wait -PassThru -NoNewWindow
if ($proc.ExitCode -ne 0) { throw "regsvr32 失败，退出码 $($proc.ExitCode)" }

# ── 5. 事件日志源 ───────────────────────────────────────────────────────────
if (-not [System.Diagnostics.EventLog]::SourceExists('AutoUnlock')) {
    New-EventLog -LogName Application -Source 'AutoUnlock'
    Write-Host '已注册事件日志源 AutoUnlock'
}

# ── 6. 创建并启动服务 ───────────────────────────────────────────────────────
$exePath = Join-Path $InstallDir 'AutoUnlockService.exe'
sc.exe create $serviceName binPath= "`"$exePath`"" start= delayed-auto DisplayName= 'AutoUnlock 近距离自动解锁' | Out-Null
sc.exe description $serviceName '基于 BLE RSSI 的近距离自动锁屏与解锁服务' | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($config.AutoUnlock.Device.Irk) -or
    [string]::IsNullOrWhiteSpace($config.AutoUnlock.Credentials.UserName)) {
    Write-Warning "配置尚未填写完整，服务不会启动。"
    Write-Warning "请编辑 $configPath 填入 Device.Irk、Credentials.UserName 与 Password，"
    Write-Warning "然后执行： Start-Service $serviceName"
} else {
    sc.exe start $serviceName | Out-Null
    Write-Host "服务已启动。日志目录：$(Join-Path $DataDir 'logs')"
}
```

脚本故意在配置没填完时**不启动服务**：`appsettings.json` 无效时服务会拒绝启动（Task 17 的设计），与其让它启动失败再去翻事件日志，不如在安装时就讲清楚。

- [ ] **Step 2: 写完整卸载脚本**

`scripts/uninstall.ps1` 全文替换为：

```powershell
#Requires -RunAsAdministrator
param(
    [string]$InstallDir = 'C:\Program Files\AutoUnlock',
    [string]$DataDir    = (Join-Path $env:ProgramData 'AutoUnlock'),
    [switch]$KeepData
)

$ErrorActionPreference = 'Continue'
$serviceName = 'AutoUnlockService'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    sc.exe stop $serviceName | Out-Null
    Start-Sleep -Seconds 3
    sc.exe delete $serviceName | Out-Null
    Write-Host '服务已删除'
}

Get-Process -Name 'AutoUnlockHelper' -ErrorAction SilentlyContinue | Stop-Process -Force

$targetDll = Join-Path $InstallDir 'AutoUnlockCP.dll'
if (Test-Path $targetDll) {
    Start-Process regsvr32.exe -ArgumentList "/u /s `"$targetDll`"" -Wait -NoNewWindow
    Write-Host '凭据提供程序已注销'
}

if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir; Write-Host '程序文件已删除' }

if (-not $KeepData -and (Test-Path $DataDir)) {
    if ((Read-Host "是否删除配置与日志（$DataDir）？输入 yes 确认") -eq 'yes') {
        Remove-Item -Recurse -Force $DataDir
        Write-Host '配置与日志已删除'
    } else {
        Write-Host "已保留 $DataDir"
    }
}

Write-Host '卸载完成。注销凭据提供程序后建议重启一次以确保 winlogon 释放 DLL。'
```

- [ ] **Step 3: 更新 README**

把 `README.md` 中关于 DX-BT401 串口模块、.NET 8 与 `cmdkey` 的内容替换为新架构的说明，至少覆盖：前提条件（Windows 11 x64、支持 BLE 的适配器、.NET 10 运行时、VC++ 2022 运行库）、三个项目的构建命令、`scripts\install.ps1` 的用法、`appsettings.json` 每个参数的含义（直接用设计文档 §6.5 的表格）、如何用 `BleRssiMonitor` 取得 IRK 并观察实时 RSSI 来调阈值、`Notification.Mode` 切 `Dialog` 的场景（勿扰模式吞掉 Toast），以及卸载步骤。

- [ ] **Step 4: 验证幂等重装**

Run（管理员 PowerShell）：
```
dotnet build AutoUnlockMyPc.sln -c Release
msbuild src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj /p:Configuration=Release /p:Platform=x64
.\scripts\install.ps1
.\scripts\install.ps1
Get-Service AutoUnlockService
(Get-Acl "$env:ProgramData\AutoUnlock").Access | Format-Table IdentityReference, FileSystemRights
```
Expected: 连续执行两次都成功，服务处于 `Running`；ACL 只列出 `NT AUTHORITY\SYSTEM` 与 `BUILTIN\Administrators`，**没有 `Users` 或 `Authenticated Users`**。

再用一个普通用户账户确认读不到配置（若有第二个账户）：该账户执行 `Get-Content "$env:ProgramData\AutoUnlock\appsettings.json"` 应当被拒绝访问。

- [ ] **Step 5: Commit**

```bash
git add scripts README.md
git commit -m "feat(deploy): 完整安装卸载脚本与 README 更新"
```

---

### Task 19: 实机验收

前面所有任务的自动化测试只覆盖纯算法。BLE、两条管道、Toast 和 Win32 调用必须在真机上走一遍。按顺序执行，每项都记录结果。

**Files:**
- Modify: `docs/superpowers/plans/2026-10-13-autounlock-service.md`（把结果勾选在本任务下）

**Interfaces:**
- Consumes: 完整安装好的系统
- Produces: 验收结论

**准备**：按 Task 18 安装完毕；`appsettings.json` 填入用 `BleRssiMonitor` 取得的 IRK、正确的用户名与密码；服务处于 `Running`；日志窗口开着：

```powershell
Get-Content "$env:ProgramData\AutoUnlock\logs\autounlock-$(Get-Date -Format yyyyMMdd).log" -Wait -Tail 20
```

- [ ] **1. 走开触发锁屏**：戴着设备走出约 10 米并停留。预期：约 10 秒后弹出带两个按钮的通知；不操作，15 秒后屏幕锁定。日志中出现 `Unlocked → PendingLock` 与 `PendingLock → LockedWaitingDeparture`。

- [ ] **2. 「拒绝」按钮**：回来解锁，重新走开，通知弹出后点「拒绝」。预期：不锁屏；60 秒内即使持续无信号也不再弹；60 秒后才会重新弹出。日志出现 `PendingLock → Snoozed`。

- [ ] **3. 「5 分钟内不锁定」按钮**：同上但点第二个按钮。预期：5 分钟内不弹不锁。

- [ ] **4. 倒计时期间走回来**：通知弹出后立刻走回电脑前。预期：通知自动消失，不锁屏。日志出现 `PendingLock → Unlocked：信号恢复`。

- [ ] **5. 原地不动不解锁（核心保证）**：坐在电脑前按 Win+L，然后**不要离开**，等 2 分钟。预期：**屏幕始终保持锁定**，日志停留在 `LockedWaitingDeparture`，不出现任何 `SendCredentials`。

- [ ] **6. 离开再回来解锁**：接上一步，走远到信号消失（日志出现 `LockedWaitingDeparture → LockedArmed`），再走回来。预期：约 5 秒内自动解锁，日志出现 `LockedArmed → UnlockPending`、`收到认证结果：成功=True`、`UnlockPending → Unlocked`。

- [ ] **7. 错密码停摆与恢复**：把 `appsettings.json` 的密码改错一位并保存。预期：日志出现「凭据预验证失败」与 `→ Suspended`，并弹出一条说明通知。此时锁屏后走开再回来**不会**自动解锁。把密码改回正确值保存，预期：日志出现「凭据已更新且验证通过，自动解锁恢复」，功能恢复。

- [ ] **8. 关闭蓝牙**：在设置里关掉蓝牙。预期：按无信号流程走——弹通知、超时锁屏。日志出现「BLE 不可用：蓝牙已关闭」。重新打开蓝牙后扫描自动恢复。

- [ ] **9. 杀助手与降级**：任务管理器结束 `AutoUnlockHelper.exe`。预期：5 秒内被重新拉起，日志出现「助手已连接」。随后把 `AutoUnlockHelper.exe` 临时改名使其无法启动，预期：连续 3 次启动失败后日志出现「通知方式已降级为对话框」，再触发锁屏时弹出的是经典对话框而非 Toast，两个按钮（是/否）行为与 Toast 一致。改回文件名后重启服务恢复 Toast。

- [ ] **10. 配置热更新**：把 `UnlockThresholdDbm` 从 -45 改为 -55 并保存。预期：约 250 毫秒后日志出现「配置已重载（设备变更=False，凭据变更=False）」，**服务未重启**，后续解锁判定使用新阈值（可从周期摘要对照）。

- [ ] **11. 睡眠唤醒**：合上笔记本盖子（或执行休眠）至少 1 分钟，再唤醒。预期：日志出现「系统已从睡眠中恢复，清空 RSSI 历史并重新进入宽限期」，且唤醒后 15 秒内**不会**弹通知锁屏。

- [ ] **12. 日志不含密码**：
```powershell
Select-String -Path "$env:ProgramData\AutoUnlock\logs\*.log" -Pattern '你的密码' -SimpleMatch
```
预期：**无任何匹配**。

- [ ] **13. 记录结果并提交**

把上面各项的实测结果（通过／偏差）补充到本文件，偏差明显的阈值直接在 `appsettings.json` 里调好，把调整后的值同步回 `README.md` 的参数表。

```bash
git add docs/superpowers/plans/2026-10-13-autounlock-service.md README.md
git commit -m "docs: 记录 AutoUnlockService 实机验收结果"
```

---

### Task 20: 判定改版 —— 时间窗口平均 → 批次全数达标

验收期发现时间窗平均对观测节奏敏感：Apple Watch 息屏后广播降频，窗口内样本不足会被误判为「无信号」而弹窗。改版把判定从「固定时间窗取平均」换成「按个数收集批次、全数达标」：判决只看收到的每一条读数是否达标，不依赖采样节奏；完全无读数时用静默注入补足。（2026-10-18 二次修订：最初实现为批内取平均；用户实机反馈偶发低读数会把平均值拉低造成误弹窗，遂改为全数达标——批内任一条不达标即整批作废、重新触发。）

**决策（2026-10-18 冻结；同日二次修订为全数达标）**

- R1 解锁：第一条高于解锁阈值（默认 -45）为触发器（不计入批次）→ 再收 `UnlockSampleCount` 条且**每一条都高于阈值** → 推送凭据；任一条不达标旧批作废、重新触发；批次内出现超过静默宽限（3 秒）的间隙同样作废。
- R2 弹窗：第一条低于锁定阈值（默认 -80）触发 → 再收 `LockPromptSampleCount` 条且**每一条都低于阈值** → 弹窗；任一条不达标即整批作废。
- R3 锁定：弹窗后**新收** `LockConfirmSampleCount` 条且**每一条都低于阈值** → 锁屏；任一条不达标整批作废重来。取消原 15 秒自动锁屏；锁定只由批次或用户点击决定（关闭 X／拒绝满期后重新评估）。
- R4 静默：连续 ≥3 秒无读数后，每秒注入 1 个极低值（-127 dBm，计入批次且本身可触发）。
- R5 恢复：弹窗期间按解锁标准（触发 + 3 条全数达标）判定，达标即撤弹窗回到 `Unlocked`。
- R6 Win+L 后的「已确认离开」沿用 R2（5 条全数达标）。
- R7 按钮语义不变：拒绝=60 秒静默、5 分钟=息屏不锁、关闭 X=立即锁屏；`Timeout` 降为 no-op（不再锁屏）。

**配置变化**（旧键留在用户 `appsettings.json` 里、绑定忽略、不生效）

- 删：`Proximity.LockWindowSeconds`、`Proximity.UnlockWindowSeconds`、`Proximity.MinSamples`、`Notification.PromptTimeoutSeconds`。
- 增：`UnlockSampleCount`=3（1–20）、`LockPromptSampleCount`=5（1–50）、`LockConfirmSampleCount`=10（1–50）。
- 校验消息用字面量（MirrorDriftGuard 逐字比对）：「Proximity.UnlockSampleCount 必须在 1 到 20 之间」等 3 条。
- 管理端配置页 16→15 字段；遥测 `thr` 收缩为 `lockDbm / unlockDbm / unlockCount / lockPromptCount / lockConfirmCount`。

**协议变化**：`PROMPT` 删除 timeout 字段（`PROMPT {id} {snooze}`）；`HelperProtocol.MaxTimeoutSeconds`（600）仅保留给降级对话框自限；HelperNotifier 改为无限期等待（仅用户响应／撤回／断连结束）。

**实现要点**：批次只记个数（不存样本、不求和），批内任一条不达标即整批作废；触发读数不计入、静默值计入；每次状态迁移重置两侧水位；启动宽限门保留在 `Unlocked`/`LockedWaitingDeparture`（不在 `PendingLock`）；弹窗期间 -80~-65 悬浮读数保持弹窗；`OnResume` 显式重置两侧并续宽限；`BatchProgressText`（锁屏确认/解锁确认/离开确认 x/N）进摘要日志。

**Files:**
- Modify: `src/AutoUnlockService/`（Config、Core/RssiHistory、Core/ProximityStateMachine 重写、Notifications、Session/HelperProtocol、Worker、Management、Program）
- Modify: `src/AutoUnlockHelper/`（ToastPrompt 去计时器与倒计时、Program 调用点）
- Modify: `src/AutoUnlockManager.Core/`（Config/Management 镜像）、`src/AutoUnlockManager/`（ConfigPage.xaml、ConfigViewModel）
- Modify: `tests/`（服务端状态机/协议/通知/遥测测试全量更新 + 新批次用例；管理端镜像测试）
- Modify: `README.md`、本文件

**结果**：全量构建 0 警 0 错；测试 523 全绿（服务 322 + 管理端 201）。发布产物已重建（管理端 532 文件 / 232.9 MB）。实机验收（按改版后新版验收单重跑）待执行。

- [x] **Step 1: 服务端判定与配置改版**
- [x] **Step 2: 通知管道与协议改版**
- [x] **Step 3: 遥测与管理端镜像同步**
- [x] **Step 4: 测试全量更新**
- [x] **Step 5: 构建与自动化测试全绿**
- [ ] **Step 6: 发布并重装（管理员运行 `scripts/install.ps1`）**
- [ ] **Step 7: 实机验收（新版验收单）并记录结果**
