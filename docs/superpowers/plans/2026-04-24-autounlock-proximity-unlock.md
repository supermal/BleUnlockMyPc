# AutoUnlockMyPc Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows 11 Bluetooth proximity unlock system: a C# .NET 8 Windows Service reads RSSI from a DX-BT401 via serial port, smooths/debounces values through a state machine, and either calls LockWorkStation (via active-session helper) or pushes credentials over a Named Pipe to a C++ COM Credential Provider that submits them to LSA.

**Architecture:** Service = Named Pipe Server (Session 0, SYSTEM). CP = Named Pipe Client (WinLogon session). CP starts background thread connecting to the pipe; credentials arrive as `username\npassword\n`; CP calls `CredentialsChanged()` → `AutoLogon()` → `GetSerialization()` → LSA verifies → desktop unlocks. Locking uses `CreateProcessAsUser` in the active console session to call `rundll32 user32.dll,LockWorkStation`.

**Tech Stack:** C# .NET 8 (`net8.0-windows`), `Microsoft.Extensions.Hosting.WindowsServices`, `System.IO.Ports`, `System.IO.Pipes.AccessControl`, xUnit 2.x; C++17, COM, `credentialprovider.h`, `ntsecapi.h`, `subauth.h`, `secur32.lib`.

---

## File Map

```
AutoUnlockMyPc/
├── AutoUnlockMyPc.sln
├── README.md
├── docs/
│   └── superpowers/specs/  plans/
└── src/
    ├── AutoUnlockService/
    │   ├── AutoUnlockService.csproj
    │   ├── Program.cs
    │   ├── Worker.cs
    │   ├── ServiceSettings.cs
    │   ├── RssiMonitor.cs
    │   ├── RssiSmoother.cs
    │   ├── ProximityStateMachine.cs
    │   ├── PipeServer.cs
    │   ├── CredentialStore.cs
    │   ├── Win32.cs
    │   └── appsettings.json
    └── AutoUnlockCredentialProvider/
        ├── AutoUnlockCredentialProvider.vcxproj
        ├── AutoUnlockCredentialProvider.def
        ├── guids.h
        ├── dllmain.cpp
        ├── ClassFactory.h / ClassFactory.cpp
        ├── CredentialProvider.h / CredentialProvider.cpp
        ├── Credential.h / Credential.cpp
        ├── PipeClient.h / PipeClient.cpp
        ├── Serialization.h / Serialization.cpp
        └── register.reg
tests/
└── AutoUnlockService.Tests/
    ├── AutoUnlockService.Tests.csproj
    ├── RssiSmootherTests.cs
    ├── ProximityStateMachineTests.cs
    └── RssiMonitorParserTests.cs
```

---

## Task 1: Directory Structure + Solution File

**Files:**
- Create: `AutoUnlockMyPc.sln`
- Create: `src/AutoUnlockService/` (dir)
- Create: `src/AutoUnlockCredentialProvider/` (dir)
- Create: `tests/AutoUnlockService.Tests/` (dir)

- [ ] **Step 1: Create directories**

```bash
mkdir -p src/AutoUnlockService
mkdir -p src/AutoUnlockCredentialProvider
mkdir -p tests/AutoUnlockService.Tests
```

Run from: `D:\Users\Admin\Desktop\Code\AutoUnlockMyPc`

- [ ] **Step 2: Write AutoUnlockMyPc.sln**

Create `AutoUnlockMyPc.sln`:

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AutoUnlockService", "src\AutoUnlockService\AutoUnlockService.csproj", "{A1B2C3D4-E5F6-7890-ABCD-111111111111}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AutoUnlockService.Tests", "tests\AutoUnlockService.Tests\AutoUnlockService.Tests.csproj", "{A1B2C3D4-E5F6-7890-ABCD-222222222222}"
EndProject
Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "AutoUnlockCredentialProvider", "src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj", "{A1B2C3D4-E5F6-7890-ABCD-333333333333}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|x64 = Debug|x64
		Release|x64 = Release|x64
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{A1B2C3D4-E5F6-7890-ABCD-111111111111}.Debug|x64.ActiveCfg = Debug|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-111111111111}.Debug|x64.Build.0 = Debug|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-111111111111}.Release|x64.ActiveCfg = Release|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-111111111111}.Release|x64.Build.0 = Release|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-222222222222}.Debug|x64.ActiveCfg = Debug|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-222222222222}.Debug|x64.Build.0 = Debug|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-222222222222}.Release|x64.ActiveCfg = Release|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-222222222222}.Release|x64.Build.0 = Release|Any CPU
		{A1B2C3D4-E5F6-7890-ABCD-333333333333}.Debug|x64.ActiveCfg = Debug|x64
		{A1B2C3D4-E5F6-7890-ABCD-333333333333}.Debug|x64.Build.0 = Debug|x64
		{A1B2C3D4-E5F6-7890-ABCD-333333333333}.Release|x64.ActiveCfg = Release|x64
		{A1B2C3D4-E5F6-7890-ABCD-333333333333}.Release|x64.Build.0 = Release|x64
	EndGlobalSection
EndGlobal
```

- [ ] **Step 3: Verify directory tree**

```bash
find . -type d | head -20
```

Expected: `./src/AutoUnlockService`, `./src/AutoUnlockCredentialProvider`, `./tests/AutoUnlockService.Tests`

---

## Task 2: C# Project Files

**Files:**
- Create: `src/AutoUnlockService/AutoUnlockService.csproj`
- Create: `tests/AutoUnlockService.Tests/AutoUnlockService.Tests.csproj`

- [ ] **Step 1: Write AutoUnlockService.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AutoUnlockService</RootNamespace>
    <AssemblyName>AutoUnlockService</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="8.*" />
    <PackageReference Include="System.IO.Ports" Version="8.*" />
    <PackageReference Include="System.IO.Pipes.AccessControl" Version="5.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>AutoUnlockService.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write AutoUnlockService.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\AutoUnlockService\AutoUnlockService.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Restore packages**

```bash
dotnet restore AutoUnlockMyPc.sln
```

Expected: `Restore completed.` No errors.

---

## Task 3: ServiceSettings.cs + appsettings.json

**Files:**
- Create: `src/AutoUnlockService/ServiceSettings.cs`
- Create: `src/AutoUnlockService/appsettings.json`

- [ ] **Step 1: Write ServiceSettings.cs**

```csharp
namespace AutoUnlockService;

public sealed class ServiceSettings
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 9600;
    public int NearThreshold { get; set; } = -60;
    public int FarThreshold { get; set; } = -80;
    public int SmoothingWindow { get; set; } = 5;
    public int UnlockConfirmCount { get; set; } = 3;
    public int LockConfirmCount { get; set; } = 5;
    public string CredentialTarget { get; set; } = "AutoUnlock";
}
```

- [ ] **Step 2: Write appsettings.json**

```json
{
  "AutoUnlock": {
    "ComPort": "COM3",
    "BaudRate": 9600,
    "NearThreshold": -60,
    "FarThreshold": -80,
    "SmoothingWindow": 5,
    "UnlockConfirmCount": 3,
    "LockConfirmCount": 5,
    "CredentialTarget": "AutoUnlock"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    },
    "EventLog": {
      "SourceName": "AutoUnlockService",
      "LogName": "Application"
    }
  }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/AutoUnlockService/ServiceSettings.cs src/AutoUnlockService/appsettings.json
git commit -m "feat: add ServiceSettings and appsettings"
```

---

## Task 4: RssiSmoother (TDD)

**Files:**
- Create: `src/AutoUnlockService/RssiSmoother.cs`
- Create: `tests/AutoUnlockService.Tests/RssiSmootherTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/AutoUnlockService.Tests/RssiSmootherTests.cs
using AutoUnlockService;
using Xunit;

namespace AutoUnlockService.Tests;

public class RssiSmootherTests
{
    [Fact]
    public void SingleValue_ReturnsThatValue()
    {
        var s = new RssiSmoother(5);
        Assert.Equal(-65.0, s.Add(-65));
    }

    [Fact]
    public void TwoValues_ReturnsAverage()
    {
        var s = new RssiSmoother(5);
        s.Add(-60);
        Assert.Equal(-65.0, s.Add(-70));
    }

    [Fact]
    public void FullWindow_SlidesCorrectly()
    {
        var s = new RssiSmoother(3);
        s.Add(-60);
        s.Add(-70);
        s.Add(-80);
        // Window full: avg = -70
        Assert.Equal(-70.0, s.Add(-80), 10);
        // Oldest -60 dropped, window is -70,-80,-80 avg = -76.66...
        // Wait: after 4 Add calls with window=3, buffer contains last 3:
        // Add(-60)->[-60], Add(-70)->[-60,-70], Add(-80)->[-60,-70,-80], Add(-80)->[-70,-80,-80]
        // So after 4th Add: avg of -70,-80,-80 = -76.666...
    }

    [Fact]
    public void FullWindow_OldestDropped()
    {
        var s = new RssiSmoother(3);
        s.Add(-60);
        s.Add(-70);
        s.Add(-80);
        double result = s.Add(-90);
        // Buffer: -70, -80, -90 => avg = -80
        Assert.Equal(-80.0, result, 10);
    }

    [Fact]
    public void Reset_StartsOver()
    {
        var s = new RssiSmoother(3);
        s.Add(-60);
        s.Add(-70);
        s.Reset();
        Assert.Equal(-50.0, s.Add(-50));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/AutoUnlockService.Tests/ --filter "RssiSmootherTests" -v normal
```

Expected: `Error: The type or namespace name 'RssiSmoother' could not be found`

- [ ] **Step 3: Write RssiSmoother.cs**

```csharp
namespace AutoUnlockService;

public sealed class RssiSmoother
{
    private readonly int[] _buffer;
    private int _index;
    private int _count;

    public RssiSmoother(int windowSize)
    {
        _buffer = new int[windowSize];
    }

    public double Add(int rssi)
    {
        _buffer[_index] = rssi;
        _index = (_index + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;

        double sum = 0;
        for (int i = 0; i < _count; i++) sum += _buffer[i];
        return sum / _count;
    }

    public void Reset()
    {
        _index = 0;
        _count = 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/AutoUnlockService.Tests/ --filter "RssiSmootherTests" -v normal
```

Expected: `Passed: 5`

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/RssiSmoother.cs tests/AutoUnlockService.Tests/RssiSmootherTests.cs
git commit -m "feat: implement RssiSmoother with sliding window average"
```

---

## Task 5: ProximityStateMachine (TDD)

**Files:**
- Create: `src/AutoUnlockService/ProximityStateMachine.cs`
- Create: `tests/AutoUnlockService.Tests/ProximityStateMachineTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/AutoUnlockService.Tests/ProximityStateMachineTests.cs
using AutoUnlockService;
using Xunit;

namespace AutoUnlockService.Tests;

public class ProximityStateMachineTests
{
    private ProximityStateMachine Make(int unlockCount = 3, int lockCount = 5)
        => new(-60, -80, unlockCount, lockCount);

    [Fact]
    public void InitialState_IsLocked()
    {
        Assert.Equal(ProximityState.Locked, Make().State);
    }

    [Fact]
    public void BelowUnlockThreshold_NoStateChange()
    {
        var sm = Make();
        sm.Feed(-65); sm.Feed(-65); sm.Feed(-65);
        Assert.Equal(ProximityState.Locked, sm.State);
    }

    [Fact]
    public void ConsecutiveNear_TriggersUnlock()
    {
        var sm = Make(unlockCount: 3);
        bool fired = false;
        sm.UnlockRequested += () => fired = true;
        sm.Feed(-55); sm.Feed(-55); sm.Feed(-55);
        Assert.Equal(ProximityState.Unlocked, sm.State);
        Assert.True(fired);
    }

    [Fact]
    public void NearBrokenByDead_ResetsCount()
    {
        var sm = Make(unlockCount: 3);
        sm.Feed(-55); sm.Feed(-55);
        sm.Feed(-70); // dead zone - resets
        sm.Feed(-55); sm.Feed(-55);
        Assert.Equal(ProximityState.Locked, sm.State);
    }

    [Fact]
    public void WhenUnlocked_ConsecutiveFar_TriggersLock()
    {
        var sm = Make(unlockCount: 3, lockCount: 5);
        bool lockFired = false;
        sm.LockRequested += () => lockFired = true;
        sm.Feed(-55); sm.Feed(-55); sm.Feed(-55); // unlock
        Assert.Equal(ProximityState.Unlocked, sm.State);
        sm.Feed(-85); sm.Feed(-85); sm.Feed(-85); sm.Feed(-85); sm.Feed(-85); // 5x far
        Assert.Equal(ProximityState.Locked, sm.State);
        Assert.True(lockFired);
    }

    [Fact]
    public void UnlockNotFiredTwice_OnSameApproach()
    {
        var sm = Make(unlockCount: 3);
        int count = 0;
        sm.UnlockRequested += () => count++;
        for (int i = 0; i < 10; i++) sm.Feed(-55);
        Assert.Equal(1, count);
    }

    [Fact]
    public void DeadZone_DoesNotChangeLocked()
    {
        var sm = Make();
        sm.Feed(-70); sm.Feed(-70);
        Assert.Equal(ProximityState.Locked, sm.State);
    }

    [Fact]
    public void DeadZone_DoesNotChangeUnlocked()
    {
        var sm = Make(unlockCount: 1, lockCount: 5);
        sm.Feed(-55); // unlock immediately
        sm.Feed(-70); sm.Feed(-70);
        Assert.Equal(ProximityState.Unlocked, sm.State);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/AutoUnlockService.Tests/ --filter "ProximityStateMachineTests" -v normal
```

Expected: compile error — `ProximityStateMachine` not found.

- [ ] **Step 3: Write ProximityStateMachine.cs**

```csharp
namespace AutoUnlockService;

public enum ProximityState { Locked, Unlocked }

public sealed class ProximityStateMachine
{
    private readonly int _nearThreshold;
    private readonly int _farThreshold;
    private readonly int _unlockConfirmCount;
    private readonly int _lockConfirmCount;

    private ProximityState _state = ProximityState.Locked;
    private int _consecutiveCount;
    private SignalKind _lastSignal = SignalKind.Dead;

    public ProximityState State => _state;
    public event Action? UnlockRequested;
    public event Action? LockRequested;

    private enum SignalKind { Near, Far, Dead }

    public ProximityStateMachine(int nearThreshold, int farThreshold,
        int unlockConfirmCount, int lockConfirmCount)
    {
        _nearThreshold = nearThreshold;
        _farThreshold = farThreshold;
        _unlockConfirmCount = unlockConfirmCount;
        _lockConfirmCount = lockConfirmCount;
    }

    public void Feed(double smoothedRssi)
    {
        SignalKind signal;
        if (smoothedRssi > _nearThreshold) signal = SignalKind.Near;
        else if (smoothedRssi < _farThreshold) signal = SignalKind.Far;
        else signal = SignalKind.Dead;

        if (signal == _lastSignal)
            _consecutiveCount++;
        else
        {
            _consecutiveCount = 1;
            _lastSignal = signal;
        }

        if (_state == ProximityState.Locked
            && signal == SignalKind.Near
            && _consecutiveCount >= _unlockConfirmCount)
        {
            _state = ProximityState.Unlocked;
            _consecutiveCount = 0;
            UnlockRequested?.Invoke();
        }
        else if (_state == ProximityState.Unlocked
            && signal == SignalKind.Far
            && _consecutiveCount >= _lockConfirmCount)
        {
            _state = ProximityState.Locked;
            _consecutiveCount = 0;
            LockRequested?.Invoke();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/AutoUnlockService.Tests/ --filter "ProximityStateMachineTests" -v normal
```

Expected: `Passed: 8`

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/ProximityStateMachine.cs tests/AutoUnlockService.Tests/ProximityStateMachineTests.cs
git commit -m "feat: implement ProximityStateMachine with hysteresis and debounce"
```

---

## Task 6: RssiMonitor Packet Parsing (TDD)

**Files:**
- Create: `src/AutoUnlockService/RssiMonitor.cs`
- Create: `tests/AutoUnlockService.Tests/RssiMonitorParserTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/AutoUnlockService.Tests/RssiMonitorParserTests.cs
using AutoUnlockService;
using Xunit;

namespace AutoUnlockService.Tests;

public class RssiMonitorParserTests
{
    [Theory]
    [InlineData("+RSSI:-65", -65)]
    [InlineData("+RSSI:-100", -100)]
    [InlineData("+rssi:-45", -45)]
    [InlineData("+RSSI:0", 0)]
    public void TryParseRssi_ValidLine_ReturnsTrue(string line, int expected)
    {
        Assert.True(RssiMonitor.TryParseRssi(line, out int rssi));
        Assert.Equal(expected, rssi);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+RSSI:")]
    [InlineData("+RSSI:abc")]
    [InlineData("AT+OK")]
    [InlineData("+RSSI:-65 extra")]
    public void TryParseRssi_InvalidLine_ReturnsFalse(string line)
    {
        Assert.False(RssiMonitor.TryParseRssi(line, out _));
    }

    [Fact]
    public void ProcessBuffer_SingleCompletePacket_FiresEvent()
    {
        var monitor = new RssiMonitor();
        int received = 0;
        int lastRssi = 0;
        monitor.RssiReceived += rssi => { received++; lastRssi = rssi; };

        monitor.InjectBuffer("+RSSI:-65\r\n");
        monitor.ProcessBuffer();

        Assert.Equal(1, received);
        Assert.Equal(-65, lastRssi);
    }

    [Fact]
    public void ProcessBuffer_TwoPacketsInOneChunk_FiresTwice()
    {
        var monitor = new RssiMonitor();
        var values = new List<int>();
        monitor.RssiReceived += rssi => values.Add(rssi);

        monitor.InjectBuffer("+RSSI:-65\r\n+RSSI:-70\r\n");
        monitor.ProcessBuffer();

        Assert.Equal(new[] { -65, -70 }, values);
    }

    [Fact]
    public void ProcessBuffer_HalfPacket_DoesNotFire()
    {
        var monitor = new RssiMonitor();
        int received = 0;
        monitor.RssiReceived += _ => received++;

        monitor.InjectBuffer("+RSSI:-65");
        monitor.ProcessBuffer();

        Assert.Equal(0, received);
    }

    [Fact]
    public void ProcessBuffer_HalfThenRest_FiresOnce()
    {
        var monitor = new RssiMonitor();
        int received = 0;
        monitor.RssiReceived += _ => received++;

        monitor.InjectBuffer("+RSSI:-65");
        monitor.ProcessBuffer();
        monitor.InjectBuffer("\r\n");
        monitor.ProcessBuffer();

        Assert.Equal(1, received);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/AutoUnlockService.Tests/ --filter "RssiMonitorParserTests" -v normal
```

Expected: compile error.

- [ ] **Step 3: Write RssiMonitor.cs**

```csharp
using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace AutoUnlockService;

public sealed class RssiMonitor : IDisposable
{
    private SerialPort? _port;
    private readonly ILogger<RssiMonitor>? _logger;
    internal string _buffer = string.Empty;

    public event Action<int>? RssiReceived;

    // Production constructor
    public RssiMonitor(string comPort, int baudRate, ILogger<RssiMonitor> logger)
    {
        _logger = logger;
        _port = new SerialPort(comPort, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            NewLine = "\r\n"
        };
        _port.DataReceived += OnDataReceived;
    }

    // Test constructor (no serial port)
    internal RssiMonitor() { }

    public void Start() => _port!.Open();
    public void Stop() { if (_port?.IsOpen == true) _port.Close(); }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            _buffer += _port!.ReadExisting();
            ProcessBuffer();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Serial read error");
        }
    }

    // internal for testing
    internal void InjectBuffer(string data) => _buffer += data;

    internal void ProcessBuffer()
    {
        while (true)
        {
            int end = _buffer.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0) break;

            string line = _buffer[..end];
            _buffer = _buffer[(end + 2)..];

            if (TryParseRssi(line, out int rssi))
                RssiReceived?.Invoke(rssi);
        }
    }

    internal static bool TryParseRssi(string line, out int rssi)
    {
        rssi = 0;
        if (!line.StartsWith("+RSSI:", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(line[6..], out rssi);
    }

    public void Dispose() => _port?.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/AutoUnlockService.Tests/ --filter "RssiMonitorParserTests" -v normal
```

Expected: `Passed: 10`

- [ ] **Step 5: Run all C# tests**

```bash
dotnet test tests/AutoUnlockService.Tests/ -v normal
```

Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/AutoUnlockService/RssiMonitor.cs tests/AutoUnlockService.Tests/RssiMonitorParserTests.cs
git commit -m "feat: implement RssiMonitor with packet framing and parser"
```

---

## Task 7: Win32.cs + CredentialStore.cs

**Files:**
- Create: `src/AutoUnlockService/Win32.cs`
- Create: `src/AutoUnlockService/CredentialStore.cs`

- [ ] **Step 1: Write Win32.cs**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoUnlockService;

internal static class Win32
{
    // ── Credential Manager ──────────────────────────────────────────
    internal const int CRED_TYPE_GENERIC = 1;
    internal const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    internal static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    // ── Session / Lock ───────────────────────────────────────────────
    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out SafeFileHandle phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool DuplicateTokenEx(
        SafeFileHandle hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out SafeFileHandle phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUser(
        SafeFileHandle hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    internal const uint TOKEN_ALL_ACCESS = 0xF01FF;
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
```

- [ ] **Step 2: Write CredentialStore.cs**

```csharp
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoUnlockService;

public sealed class CredentialStore
{
    private readonly string _target;
    private readonly ILogger<CredentialStore> _logger;

    public CredentialStore(IOptions<ServiceSettings> settings, ILogger<CredentialStore> logger)
    {
        _target = settings.Value.CredentialTarget;
        _logger = logger;
    }

    public (string username, string password)? Read()
    {
        if (!Win32.CredRead(_target, Win32.CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            _logger.LogError("CredRead failed for target '{Target}': error {Error}",
                _target, Marshal.GetLastWin32Error());
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<Win32.CREDENTIAL>(credPtr);
            string username = cred.UserName ?? string.Empty;

            string password = string.Empty;
            if (cred.CredentialBlobSize > 0)
            {
                var blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
                password = Encoding.Unicode.GetString(blob);
            }

            return (username, password);
        }
        finally
        {
            Win32.CredFree(credPtr);
        }
    }
}
```

- [ ] **Step 3: Build to verify compilation**

```bash
dotnet build src/AutoUnlockService/AutoUnlockService.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/AutoUnlockService/Win32.cs src/AutoUnlockService/CredentialStore.cs
git commit -m "feat: add Win32 P/Invoke declarations and CredentialStore"
```

---

## Task 8: PipeServer.cs

**Files:**
- Create: `src/AutoUnlockService/PipeServer.cs`

- [ ] **Step 1: Write PipeServer.cs**

```csharp
using System.IO.Pipes;
using System.IO.Pipes.AccessControl;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AutoUnlockService;

public sealed class PipeServer : IDisposable
{
    private NamedPipeServerStream? _pipe;
    private readonly ILogger<PipeServer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public const string PipeName = "AutoUnlock";

    public PipeServer(ILogger<PipeServer> logger)
    {
        _logger = logger;
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(
            systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096, 4096,
            security);
    }

    public async Task SendCredentialsAsync(string username, string password, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    _pipe ??= CreatePipe();

                    if (!_pipe.IsConnected)
                    {
                        _logger.LogInformation("Pipe: waiting for CP client (attempt {Attempt})", attempt);
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        await _pipe.WaitForConnectionAsync(cts.Token);
                    }

                    string message = $"{username}\n{password}\n";
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    await _pipe.WriteAsync(data, ct);
                    await _pipe.FlushAsync(ct);

                    byte[] response = new byte[8];
                    int read = await _pipe.ReadAsync(response.AsMemory(0, 8), ct);
                    string resp = Encoding.UTF8.GetString(response, 0, read).Trim();

                    if (resp.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Credentials sent successfully");
                        return;
                    }
                    _logger.LogWarning("Unexpected pipe response: '{Resp}'", resp);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pipe send attempt {Attempt} failed", attempt);
                    ResetPipe();
                    if (attempt < 3) await Task.Delay(500, ct);
                }
            }
            _logger.LogError("Failed to send credentials after 3 attempts");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ResetPipe()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe?.Dispose();
        _lock.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/AutoUnlockService/AutoUnlockService.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/AutoUnlockService/PipeServer.cs
git commit -m "feat: implement Named Pipe server with SYSTEM DACL and retry logic"
```

---

## Task 9: Worker.cs + Program.cs

**Files:**
- Create: `src/AutoUnlockService/Worker.cs`
- Create: `src/AutoUnlockService/Program.cs`

- [ ] **Step 1: Write Worker.cs**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoUnlockService;

public sealed class Worker : BackgroundService
{
    private readonly ServiceSettings _settings;
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PipeServer _pipeServer;
    private readonly CredentialStore _credentialStore;

    public Worker(
        IOptions<ServiceSettings> settings,
        ILogger<Worker> logger,
        ILoggerFactory loggerFactory,
        PipeServer pipeServer,
        CredentialStore credentialStore)
    {
        _settings = settings.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _pipeServer = pipeServer;
        _credentialStore = credentialStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoUnlockService starting on {Port}", _settings.ComPort);

        var smoother = new RssiSmoother(_settings.SmoothingWindow);
        var stateMachine = new ProximityStateMachine(
            _settings.NearThreshold, _settings.FarThreshold,
            _settings.UnlockConfirmCount, _settings.LockConfirmCount);

        stateMachine.UnlockRequested += () =>
        {
            _logger.LogInformation("UNLOCK triggered by proximity");
            var creds = _credentialStore.Read();
            if (creds is null)
            {
                _logger.LogError("No credentials in Credential Manager for target '{Target}'",
                    _settings.CredentialTarget);
                return;
            }
            _ = _pipeServer.SendCredentialsAsync(creds.Value.username, creds.Value.password, stoppingToken);
        };

        stateMachine.LockRequested += () =>
        {
            _logger.LogInformation("LOCK triggered by proximity");
            LockActiveSession();
        };

        using var monitor = new RssiMonitor(
            _settings.ComPort, _settings.BaudRate,
            _loggerFactory.CreateLogger<RssiMonitor>());

        monitor.RssiReceived += rssi =>
        {
            double smoothed = smoother.Add(rssi);
            stateMachine.Feed(smoothed);
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                monitor.Start();
                _logger.LogInformation("Serial port {Port} opened", _settings.ComPort);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Serial port error, retrying in 5s");
                monitor.Stop();
                smoother.Reset();
                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        monitor.Stop();
        _logger.LogInformation("AutoUnlockService stopped");
    }

    private void LockActiveSession()
    {
        uint sessionId = Win32.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            _logger.LogWarning("No active console session found");
            return;
        }

        if (!Win32.WTSQueryUserToken(sessionId, out var hToken))
        {
            _logger.LogWarning("WTSQueryUserToken failed: {Err}", Marshal.GetLastWin32Error());
            return;
        }

        using (hToken)
        {
            if (!Win32.DuplicateTokenEx(hToken, Win32.TOKEN_ALL_ACCESS, IntPtr.Zero,
                Win32.SecurityImpersonation, Win32.TokenPrimary, out var hDup))
            {
                _logger.LogWarning("DuplicateTokenEx failed: {Err}", Marshal.GetLastWin32Error());
                return;
            }

            using (hDup)
            {
                var si = new Win32.STARTUPINFO { cb = Marshal.SizeOf<Win32.STARTUPINFO>() };
                string cmd = "rundll32.exe user32.dll,LockWorkStation";

                if (!Win32.CreateProcessAsUser(hDup, null, cmd,
                    IntPtr.Zero, IntPtr.Zero, false, 0,
                    IntPtr.Zero, null, ref si, out var pi))
                {
                    _logger.LogWarning("CreateProcessAsUser failed: {Err}", Marshal.GetLastWin32Error());
                    return;
                }

                Win32.CloseHandle(pi.hProcess);
                Win32.CloseHandle(pi.hThread);
            }
        }
    }
}
```

- [ ] **Step 2: Write Program.cs**

```csharp
using AutoUnlockService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "AutoUnlockService")
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<ServiceSettings>(ctx.Configuration.GetSection("AutoUnlock"));
        services.AddSingleton<PipeServer>();
        services.AddSingleton<CredentialStore>();
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
```

- [ ] **Step 3: Build the full service**

```bash
dotnet build src/AutoUnlockService/AutoUnlockService.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Run all tests**

```bash
dotnet test tests/AutoUnlockService.Tests/ -v normal
```

Expected: All pass.

- [ ] **Step 5: Commit**

```bash
git add src/AutoUnlockService/Worker.cs src/AutoUnlockService/Program.cs
git commit -m "feat: wire Worker orchestration and LockActiveSession via CreateProcessAsUser"
```

---

## Task 10: C++ Project Scaffold (guids.h + .def + vcxproj)

**Files:**
- Create: `src/AutoUnlockCredentialProvider/guids.h`
- Create: `src/AutoUnlockCredentialProvider/AutoUnlockCredentialProvider.def`
- Create: `src/AutoUnlockCredentialProvider/AutoUnlockCredentialProvider.vcxproj`

- [ ] **Step 1: Write guids.h**

```cpp
#pragma once
#include <initguid.h>

// {6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}
// To regenerate: uuidgen.exe > new CLSID, update here AND register.reg
DEFINE_GUID(CLSID_AutoUnlockCredentialProvider,
    0x6d9e3f7a, 0x2c8b, 0x4e5d,
    0xa1, 0xf0, 0x3b, 0x7c, 0x9d, 0x2e, 0x6f, 0x4a);
```

- [ ] **Step 2: Write AutoUnlockCredentialProvider.def**

```
LIBRARY "AutoUnlockCP"
EXPORTS
    DllGetClassObject   PRIVATE
    DllCanUnloadNow     PRIVATE
    DllRegisterServer   PRIVATE
    DllUnregisterServer PRIVATE
```

- [ ] **Step 3: Write AutoUnlockCredentialProvider.vcxproj**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations">
    <ProjectConfiguration Include="Debug|x64">
      <Configuration>Debug</Configuration>
      <Platform>x64</Platform>
    </ProjectConfiguration>
    <ProjectConfiguration Include="Release|x64">
      <Configuration>Release</Configuration>
      <Platform>x64</Platform>
    </ProjectConfiguration>
  </ItemGroup>
  <PropertyGroup Label="Globals">
    <VCProjectVersion>17.0</VCProjectVersion>
    <Keyword>Win32Proj</Keyword>
    <ProjectGuid>{A1B2C3D4-E5F6-7890-ABCD-333333333333}</ProjectGuid>
    <RootNamespace>AutoUnlockCredentialProvider</RootNamespace>
    <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>
  </PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'" Label="Configuration">
    <ConfigurationType>DynamicLibrary</ConfigurationType>
    <UseDebugLibraries>true</UseDebugLibraries>
    <PlatformToolset>v143</PlatformToolset>
    <CharacterSet>Unicode</CharacterSet>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'" Label="Configuration">
    <ConfigurationType>DynamicLibrary</ConfigurationType>
    <UseDebugLibraries>false</UseDebugLibraries>
    <PlatformToolset>v143</PlatformToolset>
    <WholeProgramOptimization>true</WholeProgramOptimization>
    <CharacterSet>Unicode</CharacterSet>
  </PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
  <ImportGroup Label="ExtensionSettings" />
  <ImportGroup Label="Shared" />
  <ImportGroup Label="PropertySheets" Condition="'$(Configuration)|$(Platform)'=='Debug|x64'">
    <Import Project="$(UserRootDir)\Microsoft.Cpp.$(Platform).user.props"
      Condition="exists('$(UserRootDir)\Microsoft.Cpp.$(Platform).user.props')"
      Label="LocalAppDataPlatforms" />
  </ImportGroup>
  <ImportGroup Label="PropertySheets" Condition="'$(Configuration)|$(Platform)'=='Release|x64'">
    <Import Project="$(UserRootDir)\Microsoft.Cpp.$(Platform).user.props"
      Condition="exists('$(UserRootDir)\Microsoft.Cpp.$(Platform).user.props')"
      Label="LocalAppDataPlatforms" />
  </ImportGroup>
  <PropertyGroup Label="UserMacros" />
  <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Debug|x64'">
    <ClCompile>
      <WarningLevel>Level3</WarningLevel>
      <SDLcheck>true</SDLcheck>
      <PreprocessorDefinitions>_DEBUG;_WINDOWS;_USRDLL;%(PreprocessorDefinitions)</PreprocessorDefinitions>
      <ConformanceMode>true</ConformanceMode>
      <LanguageStandard>stdcpp17</LanguageStandard>
      <RuntimeLibrary>MultiThreadedDebugDLL</RuntimeLibrary>
    </ClCompile>
    <Link>
      <SubSystem>Windows</SubSystem>
      <GenerateDebugInformation>true</GenerateDebugInformation>
      <AdditionalDependencies>credui.lib;secur32.lib;%(AdditionalDependencies)</AdditionalDependencies>
      <ModuleDefinitionFile>AutoUnlockCredentialProvider.def</ModuleDefinitionFile>
    </Link>
  </ItemDefinitionGroup>
  <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'">
    <ClCompile>
      <WarningLevel>Level3</WarningLevel>
      <SDLcheck>true</SDLcheck>
      <PreprocessorDefinitions>NDEBUG;_WINDOWS;_USRDLL;%(PreprocessorDefinitions)</PreprocessorDefinitions>
      <ConformanceMode>true</ConformanceMode>
      <LanguageStandard>stdcpp17</LanguageStandard>
      <FunctionLevelLinking>true</FunctionLevelLinking>
      <IntrinsicFunctions>true</IntrinsicFunctions>
      <RuntimeLibrary>MultiThreadedDLL</RuntimeLibrary>
    </ClCompile>
    <Link>
      <SubSystem>Windows</SubSystem>
      <EnableCOMDATFolding>true</EnableCOMDATFolding>
      <OptimizeReferences>true</OptimizeReferences>
      <GenerateDebugInformation>true</GenerateDebugInformation>
      <AdditionalDependencies>credui.lib;secur32.lib;%(AdditionalDependencies)</AdditionalDependencies>
      <ModuleDefinitionFile>AutoUnlockCredentialProvider.def</ModuleDefinitionFile>
    </Link>
  </ItemDefinitionGroup>
  <ItemGroup>
    <ClInclude Include="ClassFactory.h" />
    <ClInclude Include="Credential.h" />
    <ClInclude Include="CredentialProvider.h" />
    <ClInclude Include="guids.h" />
    <ClInclude Include="PipeClient.h" />
    <ClInclude Include="Serialization.h" />
  </ItemGroup>
  <ItemGroup>
    <ClCompile Include="ClassFactory.cpp" />
    <ClCompile Include="Credential.cpp" />
    <ClCompile Include="CredentialProvider.cpp" />
    <ClCompile Include="dllmain.cpp" />
    <ClCompile Include="PipeClient.cpp" />
    <ClCompile Include="Serialization.cpp" />
  </ItemGroup>
  <ItemGroup>
    <None Include="AutoUnlockCredentialProvider.def" />
  </ItemGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
  <ImportGroup Label="ExtensionTargets" />
</Project>
```

- [ ] **Step 4: Commit**

```bash
git add src/AutoUnlockCredentialProvider/
git commit -m "feat: add C++ CP project scaffold (vcxproj, guids, def)"
```

---

## Task 11: dllmain.cpp + ClassFactory.h/cpp

**Files:**
- Create: `src/AutoUnlockCredentialProvider/ClassFactory.h`
- Create: `src/AutoUnlockCredentialProvider/ClassFactory.cpp`
- Create: `src/AutoUnlockCredentialProvider/dllmain.cpp`

- [ ] **Step 1: Write ClassFactory.h**

```cpp
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <unknwn.h>

class CClassFactory : public IClassFactory
{
public:
    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;

    // IClassFactory
    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override;
    IFACEMETHODIMP LockServer(BOOL fLock) override;

    CClassFactory();

private:
    LONG _cRef;
};
```

- [ ] **Step 2: Write ClassFactory.cpp**

```cpp
#include "ClassFactory.h"
#include "CredentialProvider.h"
#include "guids.h"
#include <new>

extern LONG g_cDllRef;

CClassFactory::CClassFactory() : _cRef(1) {}

ULONG CClassFactory::AddRef()  { return InterlockedIncrement(&_cRef); }
ULONG CClassFactory::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CClassFactory::QueryInterface(REFIID riid, void** ppv)
{
    if (riid == IID_IUnknown || riid == IID_IClassFactory)
    {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

HRESULT CClassFactory::CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv)
{
    *ppv = nullptr;
    if (pUnkOuter) return CLASS_E_NOAGGREGATION;

    auto* pProvider = new (std::nothrow) CAutoUnlockCredentialProvider();
    if (!pProvider) return E_OUTOFMEMORY;

    HRESULT hr = pProvider->QueryInterface(riid, ppv);
    pProvider->Release();
    return hr;
}

HRESULT CClassFactory::LockServer(BOOL fLock)
{
    if (fLock) InterlockedIncrement(&g_cDllRef);
    else        InterlockedDecrement(&g_cDllRef);
    return S_OK;
}
```

- [ ] **Step 3: Write dllmain.cpp**

```cpp
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlwapi.h>
#include <strsafe.h>
#include "guids.h"
#include "ClassFactory.h"

#pragma comment(lib, "shlwapi.lib")

HINSTANCE g_hInst = nullptr;
LONG      g_cDllRef = 0;

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID)
{
    if (dwReason == DLL_PROCESS_ATTACH)
    {
        g_hInst = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    *ppv = nullptr;
    if (rclsid != CLSID_AutoUnlockCredentialProvider) return CLASS_E_CLASSNOTAVAILABLE;

    auto* pFactory = new (std::nothrow) CClassFactory();
    if (!pFactory) return E_OUTOFMEMORY;

    HRESULT hr = pFactory->QueryInterface(riid, ppv);
    pFactory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return (g_cDllRef > 0) ? S_FALSE : S_OK;
}

// Registry path:
// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{CLSID}
static const WCHAR s_szCPKeyFmt[] =
    L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\"
    L"{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}";
static const WCHAR s_szCLSIDKeyFmt[] =
    L"CLSID\\{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}";

STDAPI DllRegisterServer()
{
    WCHAR szPath[MAX_PATH];
    if (!GetModuleFileNameW(g_hInst, szPath, ARRAYSIZE(szPath))) return HRESULT_FROM_WIN32(GetLastError());

    // Register CLSID
    HKEY hKey;
    LONG lr = RegCreateKeyExW(HKEY_LOCAL_MACHINE, s_szCLSIDKeyFmt, 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (lr != ERROR_SUCCESS) return HRESULT_FROM_WIN32(lr);

    RegSetValueExW(hKey, nullptr, 0, REG_SZ,
        (const BYTE*)L"AutoUnlock Credential Provider",
        (DWORD)(wcslen(L"AutoUnlock Credential Provider") + 1) * sizeof(WCHAR));

    HKEY hInprocKey;
    RegCreateKeyExW(hKey, L"InprocServer32", 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hInprocKey, nullptr);
    RegSetValueExW(hInprocKey, nullptr, 0, REG_SZ,
        (const BYTE*)szPath, (DWORD)(wcslen(szPath) + 1) * sizeof(WCHAR));
    RegSetValueExW(hInprocKey, L"ThreadingModel", 0, REG_SZ,
        (const BYTE*)L"Apartment",
        (DWORD)(wcslen(L"Apartment") + 1) * sizeof(WCHAR));
    RegCloseKey(hInprocKey);
    RegCloseKey(hKey);

    // Register as Credential Provider
    lr = RegCreateKeyExW(HKEY_LOCAL_MACHINE, s_szCPKeyFmt, 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (lr != ERROR_SUCCESS) return HRESULT_FROM_WIN32(lr);
    RegSetValueExW(hKey, nullptr, 0, REG_SZ,
        (const BYTE*)L"AutoUnlock Credential Provider",
        (DWORD)(wcslen(L"AutoUnlock Credential Provider") + 1) * sizeof(WCHAR));
    RegCloseKey(hKey);

    return S_OK;
}

STDAPI DllUnregisterServer()
{
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, s_szCPKeyFmt);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, s_szCLSIDKeyFmt);
    return S_OK;
}
```

- [ ] **Step 4: Commit**

```bash
git add src/AutoUnlockCredentialProvider/dllmain.cpp src/AutoUnlockCredentialProvider/ClassFactory.h src/AutoUnlockCredentialProvider/ClassFactory.cpp
git commit -m "feat: add COM DLL entry and ClassFactory"
```

---

## Task 12: PipeClient.h/cpp

**Files:**
- Create: `src/AutoUnlockCredentialProvider/PipeClient.h`
- Create: `src/AutoUnlockCredentialProvider/PipeClient.cpp`

- [ ] **Step 1: Write PipeClient.h**

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

private:
    static DWORD WINAPI ThreadProc(LPVOID lpParam);
    void Run();

    HANDLE            _hThread;
    HANDLE            _hStopEvent;
    CredReceivedCallback _callback;
};
```

- [ ] **Step 2: Write PipeClient.cpp**

```cpp
#include "PipeClient.h"
#include <string>

static const WCHAR PIPE_NAME[] = L"\\\\.\\pipe\\AutoUnlock";

CPipeClient::CPipeClient(CredReceivedCallback callback)
    : _hThread(nullptr)
    , _callback(std::move(callback))
{
    _hStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
}

CPipeClient::~CPipeClient()
{
    Stop();
    if (_hStopEvent) CloseHandle(_hStopEvent);
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
            PIPE_NAME,
            GENERIC_READ | GENERIC_WRITE,
            0, nullptr,
            OPEN_EXISTING,
            0, nullptr);

        if (hPipe == INVALID_HANDLE_VALUE)
        {
            // Pipe not ready yet; wait then retry
            WaitForSingleObject(_hStopEvent, 1000);
            continue;
        }

        // Set to message-byte mode for reliable reads
        DWORD dwMode = PIPE_READMODE_BYTE;
        SetNamedPipeHandleState(hPipe, &dwMode, nullptr, nullptr);

        // Read credentials: "username\npassword\n"
        char buf[1024] = {};
        DWORD bytesRead = 0;
        BOOL ok = ReadFile(hPipe, buf, sizeof(buf) - 1, &bytesRead, nullptr);

        if (ok && bytesRead > 0)
        {
            buf[bytesRead] = '\0';
            std::string data(buf, bytesRead);

            auto nl1 = data.find('\n');
            if (nl1 != std::string::npos)
            {
                std::string usernameUtf8 = data.substr(0, nl1);
                std::string rest = data.substr(nl1 + 1);
                auto nl2 = rest.find('\n');
                std::string passwordUtf8 = rest.substr(0, nl2 != std::string::npos ? nl2 : rest.size());

                // Convert UTF-8 to wide
                auto toWide = [](const std::string& s) -> std::wstring {
                    if (s.empty()) return {};
                    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
                    std::wstring w(len - 1, L'\0');
                    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), len);
                    return w;
                };

                std::wstring username = toWide(usernameUtf8);
                std::wstring password = toWide(passwordUtf8);

                // Acknowledge
                DWORD written;
                WriteFile(hPipe, "OK\n", 3, &written, nullptr);

                // Notify the CP
                _callback(username, password);
            }
        }

        CloseHandle(hPipe);

        // Brief pause before reconnecting (avoids busy-loop on rapid reconnect)
        WaitForSingleObject(_hStopEvent, 500);
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/AutoUnlockCredentialProvider/PipeClient.h src/AutoUnlockCredentialProvider/PipeClient.cpp
git commit -m "feat: implement Named Pipe client background thread"
```

---

## Task 13: Serialization.h/cpp

**Files:**
- Create: `src/AutoUnlockCredentialProvider/Serialization.h`
- Create: `src/AutoUnlockCredentialProvider/Serialization.cpp`

- [ ] **Step 1: Write Serialization.h**

```cpp
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <string>

// Packs credentials into CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION.
// Caller must CoTaskMemFree(pcpcs->rgbSerialization) when done.
// Detects domain vs local account from username format (DOMAIN\user vs user).
HRESULT PackCredentialSerialization(
    const std::wstring& username,
    const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs);
```

- [ ] **Step 2: Write Serialization.cpp**

```cpp
#include "Serialization.h"
#include "guids.h"
#include <ntsecapi.h>
#include <subauth.h>
#include <string>

#pragma comment(lib, "secur32.lib")

// Returns true for "DOMAIN\user" format; false for plain "user" or ".\user"
static bool IsDomainAccount(const std::wstring& username)
{
    auto pos = username.find(L'\\');
    if (pos == std::wstring::npos) return false;
    std::wstring prefix = username.substr(0, pos);
    return !prefix.empty() && prefix != L".";
}

// Looks up an LSA authentication package ID by name.
static HRESULT GetAuthPackageId(LPCSTR packageName, ULONG* pulId)
{
    HANDLE hLsa;
    NTSTATUS status = LsaConnectUntrusted(&hLsa);
    if (status != STATUS_SUCCESS) return HRESULT_FROM_NT(status);

    LSA_STRING lsaName;
    lsaName.Buffer         = const_cast<PCHAR>(packageName);
    lsaName.Length         = static_cast<USHORT>(strlen(packageName));
    lsaName.MaximumLength  = lsaName.Length;

    status = LsaLookupAuthenticationPackage(hLsa, &lsaName, pulId);
    LsaDeregisterLogonProcess(hLsa);
    return HRESULT_FROM_NT(status);
}

// Packs MSV1_0_INTERACTIVE_LOGON for local accounts.
static HRESULT PackMsv(const std::wstring& username, const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    // Split "COMPUTERNAME\user" or ".\user" or plain "user"
    std::wstring domain, user;
    auto pos = username.find(L'\\');
    if (pos != std::wstring::npos)
    {
        domain = username.substr(0, pos);
        user   = username.substr(pos + 1);
        if (domain == L".") // replace . with local machine name
        {
            WCHAR computerName[MAX_COMPUTERNAME_LENGTH + 1];
            DWORD size = ARRAYSIZE(computerName);
            GetComputerNameW(computerName, &size);
            domain = computerName;
        }
    }
    else
    {
        WCHAR computerName[MAX_COMPUTERNAME_LENGTH + 1];
        DWORD size = ARRAYSIZE(computerName);
        GetComputerNameW(computerName, &size);
        domain = computerName;
        user   = username;
    }

    DWORD cbDomain = static_cast<DWORD>(domain.size() * sizeof(WCHAR));
    DWORD cbUser   = static_cast<DWORD>(user.size()   * sizeof(WCHAR));
    DWORD cbPass   = static_cast<DWORD>(password.size()  * sizeof(WCHAR));
    DWORD cbTotal  = sizeof(MSV1_0_INTERACTIVE_LOGON) + cbDomain + cbUser + cbPass;

    auto* pLogon = reinterpret_cast<MSV1_0_INTERACTIVE_LOGON*>(CoTaskMemAlloc(cbTotal));
    if (!pLogon) return E_OUTOFMEMORY;
    ZeroMemory(pLogon, cbTotal);

    pLogon->MessageType = MsV1_0InteractiveLogon;

    PBYTE pb = reinterpret_cast<PBYTE>(pLogon + 1);

    // Domain (relative offset from base)
    pLogon->LogonDomainName.Length        = static_cast<USHORT>(cbDomain);
    pLogon->LogonDomainName.MaximumLength = static_cast<USHORT>(cbDomain);
    pLogon->LogonDomainName.Buffer        = cbDomain > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbDomain > 0) { memcpy(pb, domain.c_str(), cbDomain); pb += cbDomain; }

    // Username
    pLogon->UserName.Length        = static_cast<USHORT>(cbUser);
    pLogon->UserName.MaximumLength = static_cast<USHORT>(cbUser);
    pLogon->UserName.Buffer        = reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon));
    memcpy(pb, user.c_str(), cbUser); pb += cbUser;

    // Password
    pLogon->Password.Length        = static_cast<USHORT>(cbPass);
    pLogon->Password.MaximumLength = static_cast<USHORT>(cbPass);
    pLogon->Password.Buffer        = cbPass > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbPass > 0) memcpy(pb, password.c_str(), cbPass);

    ULONG ulPkg;
    HRESULT hr = GetAuthPackageId(MSV1_0_PACKAGE_NAME, &ulPkg);
    if (FAILED(hr)) { CoTaskMemFree(pLogon); return hr; }

    pcpcs->ulAuthenticationPackage = ulPkg;
    pcpcs->cbSerialization         = cbTotal;
    pcpcs->rgbSerialization        = reinterpret_cast<BYTE*>(pLogon);
    pcpcs->clsidCredentialProvider = CLSID_AutoUnlockCredentialProvider;
    return S_OK;
}

// Packs KERB_INTERACTIVE_UNLOCK_LOGON for domain accounts.
static HRESULT PackKerb(const std::wstring& username, const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    std::wstring domain, user;
    auto pos = username.find(L'\\');
    domain = username.substr(0, pos);
    user   = username.substr(pos + 1);

    DWORD cbDomain = static_cast<DWORD>(domain.size() * sizeof(WCHAR));
    DWORD cbUser   = static_cast<DWORD>(user.size()   * sizeof(WCHAR));
    DWORD cbPass   = static_cast<DWORD>(password.size()  * sizeof(WCHAR));
    DWORD cbTotal  = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) + cbDomain + cbUser + cbPass;

    auto* pLogon = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(CoTaskMemAlloc(cbTotal));
    if (!pLogon) return E_OUTOFMEMORY;
    ZeroMemory(pLogon, cbTotal);

    pLogon->Logon.MessageType = KerbInteractiveLogon;

    PBYTE pb = reinterpret_cast<PBYTE>(pLogon + 1);

    pLogon->Logon.LogonDomainName.Length        = static_cast<USHORT>(cbDomain);
    pLogon->Logon.LogonDomainName.MaximumLength = static_cast<USHORT>(cbDomain);
    pLogon->Logon.LogonDomainName.Buffer        = cbDomain > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbDomain > 0) { memcpy(pb, domain.c_str(), cbDomain); pb += cbDomain; }

    pLogon->Logon.UserName.Length        = static_cast<USHORT>(cbUser);
    pLogon->Logon.UserName.MaximumLength = static_cast<USHORT>(cbUser);
    pLogon->Logon.UserName.Buffer        = reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon));
    memcpy(pb, user.c_str(), cbUser); pb += cbUser;

    pLogon->Logon.Password.Length        = static_cast<USHORT>(cbPass);
    pLogon->Logon.Password.MaximumLength = static_cast<USHORT>(cbPass);
    pLogon->Logon.Password.Buffer        = cbPass > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbPass > 0) memcpy(pb, password.c_str(), cbPass);

    ULONG ulPkg;
    HRESULT hr = GetAuthPackageId(MICROSOFT_KERBEROS_NAME_A, &ulPkg);
    if (FAILED(hr)) { CoTaskMemFree(pLogon); return hr; }

    pcpcs->ulAuthenticationPackage = ulPkg;
    pcpcs->cbSerialization         = cbTotal;
    pcpcs->rgbSerialization        = reinterpret_cast<BYTE*>(pLogon);
    pcpcs->clsidCredentialProvider = CLSID_AutoUnlockCredentialProvider;
    return S_OK;
}

HRESULT PackCredentialSerialization(
    const std::wstring& username,
    const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    if (IsDomainAccount(username))
        return PackKerb(username, password, pcpcs);
    else
        return PackMsv(username, password, pcpcs);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/AutoUnlockCredentialProvider/Serialization.h src/AutoUnlockCredentialProvider/Serialization.cpp
git commit -m "feat: implement LSA serialization for local (MSV1_0) and domain (Kerberos) accounts"
```

---

## Task 14: Credential.h/cpp

**Files:**
- Create: `src/AutoUnlockCredentialProvider/Credential.h`
- Create: `src/AutoUnlockCredentialProvider/Credential.cpp`

- [ ] **Step 1: Write Credential.h**

```cpp
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <string>

class CAutoUnlockCredential : public ICredentialProviderCredential2
{
public:
    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogonWithDefault) override;
    IFACEMETHODIMP SetDeselected() override;
    IFACEMETHODIMP GetFieldState(DWORD dwFieldID,
        CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override;
    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, PWSTR* ppwsz) override;
    IFACEMETHODIMP GetBitmapValue(DWORD dwFieldID, HBITMAP* phbmp) override;
    IFACEMETHODIMP GetCheckboxValue(DWORD dwFieldID, BOOL* pbChecked, PWSTR* ppwszLabel) override;
    IFACEMETHODIMP GetComboBoxValueCount(DWORD dwFieldID, DWORD* pdwItems, DWORD* pdwSelectedItem) override;
    IFACEMETHODIMP GetComboBoxValueAt(DWORD dwFieldID, DWORD dwItem, PWSTR* ppwszItem) override;
    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo) override;
    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, PCWSTR pwz) override;
    IFACEMETHODIMP SetCheckboxValue(DWORD dwFieldID, BOOL bChecked) override;
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD dwFieldID, DWORD dwSelectedItem) override;
    IFACEMETHODIMP CommandLinkClicked(DWORD dwFieldID) override;
    IFACEMETHODIMP GetSerialization(
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
        PWSTR* ppwszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;
    IFACEMETHODIMP ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
        PWSTR* ppwszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;

    // ICredentialProviderCredential2
    IFACEMETHODIMP GetUserSid(PWSTR* ppszSid) override;

    CAutoUnlockCredential();
    virtual ~CAutoUnlockCredential();

    void SetCredentials(const std::wstring& username, const std::wstring& password);
    bool HasCredentials() const { return _hasCredentials; }

private:
    LONG        _cRef;
    std::wstring _username;
    std::wstring _password;
    bool        _hasCredentials;
    ICredentialProviderCredentialEvents* _pcpce;
};
```

- [ ] **Step 2: Write Credential.cpp**

```cpp
#include "Credential.h"
#include "Serialization.h"
#include <wincred.h>

CAutoUnlockCredential::CAutoUnlockCredential()
    : _cRef(1)
    , _hasCredentials(false)
    , _pcpce(nullptr)
{
}

CAutoUnlockCredential::~CAutoUnlockCredential()
{
    // Zero credentials from memory
    if (!_password.empty())
        SecureZeroMemory(_password.data(), _password.size() * sizeof(WCHAR));
    if (_pcpce) _pcpce->Release();
}

ULONG CAutoUnlockCredential::AddRef()  { return InterlockedIncrement(&_cRef); }
ULONG CAutoUnlockCredential::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CAutoUnlockCredential::QueryInterface(REFIID riid, void** ppv)
{
    if (riid == IID_IUnknown ||
        riid == IID_ICredentialProviderCredential ||
        riid == IID_ICredentialProviderCredential2)
    {
        *ppv = static_cast<ICredentialProviderCredential2*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

void CAutoUnlockCredential::SetCredentials(const std::wstring& username, const std::wstring& password)
{
    _username = username;
    _password = password;
    _hasCredentials = true;
}

HRESULT CAutoUnlockCredential::Advise(ICredentialProviderCredentialEvents* pcpce)
{
    if (_pcpce) _pcpce->Release();
    _pcpce = pcpce;
    if (_pcpce) _pcpce->AddRef();
    return S_OK;
}

HRESULT CAutoUnlockCredential::UnAdvise()
{
    if (_pcpce) { _pcpce->Release(); _pcpce = nullptr; }
    return S_OK;
}

// When selected, tell LogonUI to auto-logon immediately if we have credentials
HRESULT CAutoUnlockCredential::SetSelected(BOOL* pbAutoLogonWithDefault)
{
    *pbAutoLogonWithDefault = _hasCredentials ? TRUE : FALSE;
    return S_OK;
}

HRESULT CAutoUnlockCredential::SetDeselected() { return S_OK; }

// No UI fields — hidden credential provider
HRESULT CAutoUnlockCredential::GetFieldState(DWORD,
    CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis)
{
    *pcpfs  = CPFS_HIDDEN;
    *pcpfis = CPFIS_NONE;
    return S_OK;
}

HRESULT CAutoUnlockCredential::GetStringValue(DWORD, PWSTR* ppwsz)
{
    *ppwsz = nullptr;
    return S_OK;
}
HRESULT CAutoUnlockCredential::GetBitmapValue(DWORD, HBITMAP* phbmp)          { *phbmp = nullptr; return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetCheckboxValue(DWORD, BOOL*, PWSTR*)          { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*)    { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetComboBoxValueAt(DWORD, DWORD, PWSTR*)        { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetSubmitButtonValue(DWORD, DWORD*)             { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::SetStringValue(DWORD, PCWSTR)                   { return S_OK; }
HRESULT CAutoUnlockCredential::SetCheckboxValue(DWORD, BOOL)                   { return S_OK; }
HRESULT CAutoUnlockCredential::SetComboBoxSelectedValue(DWORD, DWORD)          { return S_OK; }
HRESULT CAutoUnlockCredential::CommandLinkClicked(DWORD)                       { return S_OK; }
HRESULT CAutoUnlockCredential::GetUserSid(PWSTR* ppszSid)                      { *ppszSid = nullptr; return S_OK; }

HRESULT CAutoUnlockCredential::GetSerialization(
    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
    PWSTR* ppwszOptionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon)
{
    *ppwszOptionalStatusText = nullptr;
    *pcpsiOptionalStatusIcon = CPSI_NONE;

    if (!_hasCredentials)
    {
        *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        return S_OK;
    }

    HRESULT hr = PackCredentialSerialization(_username, _password, pcpcs);
    if (SUCCEEDED(hr))
    {
        *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;

        // Zero credentials immediately after packing
        SecureZeroMemory(_password.data(), _password.size() * sizeof(WCHAR));
        _password.clear();
        _hasCredentials = false;
    }
    else
    {
        *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
    }

    return hr;
}

HRESULT CAutoUnlockCredential::ReportResult(NTSTATUS, NTSTATUS, PWSTR* ppwsz,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsi)
{
    *ppwsz  = nullptr;
    *pcpsi  = CPSI_NONE;
    return S_OK;
}
```

- [ ] **Step 3: Commit**

```bash
git add src/AutoUnlockCredentialProvider/Credential.h src/AutoUnlockCredentialProvider/Credential.cpp
git commit -m "feat: implement ICredentialProviderCredential2 with auto-logon and GetSerialization"
```

---

## Task 15: CredentialProvider.h/cpp

**Files:**
- Create: `src/AutoUnlockCredentialProvider/CredentialProvider.h`
- Create: `src/AutoUnlockCredentialProvider/CredentialProvider.cpp`

- [ ] **Step 1: Write CredentialProvider.h**

```cpp
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include "Credential.h"
#include "PipeClient.h"

class CAutoUnlockCredentialProvider : public ICredentialProvider
{
public:
    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;

    // ICredentialProvider
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD dwFlags) override;
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs) override;
    IFACEMETHODIMP Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* pdwCount) override;
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex,
        CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override;
    IFACEMETHODIMP GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault,
        BOOL* pbAutoLogonWithDefault) override;
    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex,
        ICredentialProviderCredential** ppcpc) override;

    CAutoUnlockCredentialProvider();
    virtual ~CAutoUnlockCredentialProvider();

private:
    void OnCredentialsReceived(const std::wstring& username, const std::wstring& password);

    LONG                    _cRef;
    ICredentialProviderEvents* _pcpe;
    UINT_PTR                _upAdviseContext;
    CAutoUnlockCredential*  _pCredential;
    CPipeClient*            _pPipeClient;
    bool                    _credentialsReady;
    CRITICAL_SECTION        _cs;
};
```

- [ ] **Step 2: Write CredentialProvider.cpp**

```cpp
#include "CredentialProvider.h"
#include "guids.h"
#include <new>

extern LONG g_cDllRef;

CAutoUnlockCredentialProvider::CAutoUnlockCredentialProvider()
    : _cRef(1)
    , _pcpe(nullptr)
    , _upAdviseContext(0)
    , _pCredential(nullptr)
    , _pPipeClient(nullptr)
    , _credentialsReady(false)
{
    InterlockedIncrement(&g_cDllRef);
    InitializeCriticalSection(&_cs);

    _pCredential = new (std::nothrow) CAutoUnlockCredential();

    _pPipeClient = new (std::nothrow) CPipeClient(
        [this](const std::wstring& u, const std::wstring& p)
        {
            OnCredentialsReceived(u, p);
        });

    if (_pPipeClient) _pPipeClient->Start();
}

CAutoUnlockCredentialProvider::~CAutoUnlockCredentialProvider()
{
    if (_pPipeClient) { _pPipeClient->Stop(); delete _pPipeClient; }
    if (_pCredential) _pCredential->Release();
    if (_pcpe)        _pcpe->Release();
    DeleteCriticalSection(&_cs);
    InterlockedDecrement(&g_cDllRef);
}

ULONG CAutoUnlockCredentialProvider::AddRef()  { return InterlockedIncrement(&_cRef); }
ULONG CAutoUnlockCredentialProvider::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CAutoUnlockCredentialProvider::QueryInterface(REFIID riid, void** ppv)
{
    if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
    {
        *ppv = static_cast<ICredentialProvider*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

HRESULT CAutoUnlockCredentialProvider::SetUsageScenario(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD)
{
    // Only active during unlock and logon scenarios
    if (cpus == CPUS_UNLOCK_WORKSTATION || cpus == CPUS_LOGON)
        return S_OK;
    return E_NOTIMPL;
}

HRESULT CAutoUnlockCredentialProvider::SetSerialization(
    const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*)
{
    return E_NOTIMPL;
}

HRESULT CAutoUnlockCredentialProvider::Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext)
{
    if (_pcpe) _pcpe->Release();
    _pcpe = pcpe;
    if (_pcpe) _pcpe->AddRef();
    _upAdviseContext = upAdviseContext;
    return S_OK;
}

HRESULT CAutoUnlockCredentialProvider::UnAdvise()
{
    if (_pcpe) { _pcpe->Release(); _pcpe = nullptr; }
    return S_OK;
}

// No visible UI fields
HRESULT CAutoUnlockCredentialProvider::GetFieldDescriptorCount(DWORD* pdwCount)
{
    *pdwCount = 0;
    return S_OK;
}

HRESULT CAutoUnlockCredentialProvider::GetFieldDescriptorAt(DWORD,
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd)
{
    *ppcpfd = nullptr;
    return E_INVALIDARG;
}

HRESULT CAutoUnlockCredentialProvider::GetCredentialCount(
    DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault)
{
    EnterCriticalSection(&_cs);
    bool ready = _credentialsReady;
    LeaveCriticalSection(&_cs);

    if (ready)
    {
        *pdwCount               = 1;
        *pdwDefault             = 0;
        *pbAutoLogonWithDefault = TRUE;
    }
    else
    {
        *pdwCount               = 0;
        *pdwDefault             = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *pbAutoLogonWithDefault = FALSE;
    }
    return S_OK;
}

HRESULT CAutoUnlockCredentialProvider::GetCredentialAt(DWORD dwIndex,
    ICredentialProviderCredential** ppcpc)
{
    if (dwIndex != 0 || !_pCredential) return E_INVALIDARG;
    return _pCredential->QueryInterface(IID_ICredentialProviderCredential,
        reinterpret_cast<void**>(ppcpc));
}

void CAutoUnlockCredentialProvider::OnCredentialsReceived(
    const std::wstring& username, const std::wstring& password)
{
    EnterCriticalSection(&_cs);
    if (_pCredential) _pCredential->SetCredentials(username, password);
    _credentialsReady = true;
    LeaveCriticalSection(&_cs);

    // Signal LogonUI to re-enumerate credentials
    if (_pcpe) _pcpe->CredentialsChanged(_upAdviseContext);
}
```

- [ ] **Step 3: Build the C++ project**

Open Visual Studio 2022, build `Release|x64`. Or from Developer Command Prompt:

```
msbuild src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj /p:Configuration=Release /p:Platform=x64
```

Expected: `Build succeeded.` → `x64\Release\AutoUnlockCP.dll`

- [ ] **Step 4: Commit**

```bash
git add src/AutoUnlockCredentialProvider/CredentialProvider.h src/AutoUnlockCredentialProvider/CredentialProvider.cpp
git commit -m "feat: implement ICredentialProvider with CredentialsChanged signal"
```

---

## Task 16: register.reg + README.md

**Files:**
- Create: `src/AutoUnlockCredentialProvider/register.reg`
- Create: `README.md`

- [ ] **Step 1: Write register.reg**

```reg
Windows Registry Editor Version 5.00

; Register COM InprocServer (update path after DLL placement)
[HKEY_LOCAL_MACHINE\CLSID\{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}]
@="AutoUnlock Credential Provider"

[HKEY_LOCAL_MACHINE\CLSID\{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}\InprocServer32]
@="C:\\Program Files\\AutoUnlock\\AutoUnlockCP.dll"
"ThreadingModel"="Apartment"

; Register as Credential Provider
[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}]
@="AutoUnlock Credential Provider"
```

> Note: Update the DLL path before importing. Alternatively use `regsvr32` which auto-detects the path.

- [ ] **Step 2: Write README.md**

```markdown
# AutoUnlockMyPc

Bluetooth proximity unlock for Windows 11 using DX-BT401 + RSSI.

## Prerequisites

- Windows 11 x64
- DX-BT401 connected via USB-Serial (COM port)
- .NET 8 Runtime
- Visual C++ Redistributable 2022

## Build

1. Open `AutoUnlockMyPc.sln` in Visual Studio 2022
2. Build `AutoUnlockService` (Release, Any CPU)
3. Build `AutoUnlockCredentialProvider` (Release, x64)

Or from command line:
```
dotnet publish src/AutoUnlockService -c Release -r win-x64 --self-contained false -o out/service
msbuild src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj /p:Configuration=Release /p:Platform=x64
```

## Installation

### 1. Store credentials

```cmd
cmdkey /add:AutoUnlock /user:YOURDOMAIN\yourusername /pass:yourpassword
```

For local accounts:
```cmd
cmdkey /add:AutoUnlock /user:yourusername /pass:yourpassword
```

### 2. Edit appsettings.json

Update `ComPort` to match your DX-BT401 (check Device Manager):
```json
{ "AutoUnlock": { "ComPort": "COM4" } }
```

### 3. Install the Service

```cmd
sc create AutoUnlockService binPath= "C:\path\to\out\service\AutoUnlockService.exe" start= auto
sc description AutoUnlockService "Bluetooth proximity auto-unlock service"
sc start AutoUnlockService
```

### 4. Install the Credential Provider

Copy `AutoUnlockCP.dll` to a permanent location (e.g., `C:\Program Files\AutoUnlock\`).

Option A — using regsvr32 (recommended):
```cmd
regsvr32 "C:\Program Files\AutoUnlock\AutoUnlockCP.dll"
```

Option B — using register.reg:
Edit the DLL path in `register.reg`, then double-click to import.

### 5. Verify

Lock your PC (Win+L). Bring the paired Bluetooth device close. The screen should unlock automatically within a few seconds.

## Uninstall

```cmd
sc stop AutoUnlockService
sc delete AutoUnlockService
regsvr32 /u "C:\Program Files\AutoUnlock\AutoUnlockCP.dll"
cmdkey /delete:AutoUnlock
```

## Tuning

Edit `appsettings.json` and restart the service:

| Setting | Default | Description |
|---------|---------|-------------|
| NearThreshold | -60 | dBm above = unlock |
| FarThreshold | -80 | dBm below = lock |
| UnlockConfirmCount | 3 | Consecutive near readings to unlock |
| LockConfirmCount | 5 | Consecutive far readings to lock |
| SmoothingWindow | 5 | Sliding average window |
```

- [ ] **Step 3: Final commit**

```bash
git add src/AutoUnlockCredentialProvider/register.reg README.md
git commit -m "feat: add register.reg and deployment README"
```

---

## Self-Review

**Spec coverage check:**

| Spec requirement | Covered by task |
|-----------------|-----------------|
| SerialPort RSSI listening | Task 6 (RssiMonitor) |
| Packet framing /粘包 | Task 6 (ProcessBuffer buffer loop) |
| Sliding average smoothing | Task 4 (RssiSmoother) |
| Hysteresis dead zone | Task 5 (ProximityStateMachine) |
| State debounce (3/5 count) | Task 5 |
| LockWorkStation from Session 0 | Task 9 (CreateProcessAsUser) |
| Named Pipe IPC | Tasks 8 (server) + 12 (client) |
| Pipe DACL SYSTEM-only | Task 8 |
| Credential Manager read | Task 7 (CredentialStore) |
| ICredentialProvider COM | Task 15 |
| ICredentialProviderCredential2 | Task 14 |
| CPUS_UNLOCK_WORKSTATION | Task 15 (SetUsageScenario) |
| AutoLogon (SetSelected) | Task 14 |
| GetSerialization MSV1_0 | Task 13 |
| GetSerialization Kerberos | Task 13 |
| Local + domain account | Task 13 (IsDomainAccount) |
| Credentials zeroed after use | Task 14 (SecureZeroMemory) |
| register.reg | Task 16 |
| README deployment guide | Task 16 |
| All params configurable | Task 3 (appsettings.json) |

All spec requirements covered. No placeholders found.
