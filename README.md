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

```cmd
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
