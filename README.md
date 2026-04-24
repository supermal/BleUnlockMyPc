# AutoUnlockMyPc

基于 DX-BT401 蓝牙模块 + RSSI 的 Windows 11 自动近距离解锁工具。

## 前提条件

- Windows 11 x64
- DX-BT401 通过 USB 转串口连接（COM 端口）
- .NET 8 运行时
- Visual C++ 运行库 2022

## 构建

1. 用 Visual Studio 打开 `AutoUnlockMyPc.sln`
2. 编译 `AutoUnlockService`（Release，Any CPU）
3. 编译 `AutoUnlockCredentialProvider`（Release，x64）

或通过命令行：

```cmd
dotnet publish src/AutoUnlockService -c Release -r win-x64 --self-contained false -o out/service
msbuild src\AutoUnlockCredentialProvider\AutoUnlockCredentialProvider.vcxproj /p:Configuration=Release /p:Platform=x64
```

## 安装步骤

### 1. 存储凭据

域账户：
```cmd
cmdkey /add:AutoUnlock /user:域名\用户名 /pass:密码
```

本地账户：
```cmd
cmdkey /add:AutoUnlock /user:用户名 /pass:密码
```

### 2. 修改 appsettings.json

将 `ComPort` 改为你的 DX-BT401 对应的串口号（在设备管理器中查看）：
```json
{ "AutoUnlock": { "ComPort": "COM4" } }
```

### 3. 安装服务

```cmd
sc create AutoUnlockService binPath= "C:\路径\to\out\service\AutoUnlockService.exe" start= auto
sc description AutoUnlockService "蓝牙近距离自动解锁服务"
sc start AutoUnlockService
```

### 4. 安装凭据提供程序

将 `AutoUnlockCP.dll` 复制到永久路径（如 `C:\Program Files\AutoUnlock\`）。

方式 A — 使用 regsvr32（推荐）：
```cmd
regsvr32 "C:\Program Files\AutoUnlock\AutoUnlockCP.dll"
```

方式 B — 使用 register.reg：
编辑 `register.reg` 中的 DLL 路径，然后双击导入。

### 5. 验证

按 Win+L 锁屏，将已配对的蓝牙设备靠近电脑，几秒内屏幕应自动解锁。

## 卸载

```cmd
sc stop AutoUnlockService
sc delete AutoUnlockService
regsvr32 /u "C:\Program Files\AutoUnlock\AutoUnlockCP.dll"
cmdkey /delete:AutoUnlock
```

## 参数调整

修改 `appsettings.json` 后重启服务生效：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| NearThreshold | -60 | 高于此 dBm 判定为靠近，触发解锁 |
| FarThreshold | -80 | 低于此 dBm 判定为远离，触发锁定 |
| UnlockConfirmCount | 3 | 连续 N 次靠近才触发解锁 |
| LockConfirmCount | 5 | 连续 N 次远离才触发锁定 |
| SmoothingWindow | 5 | 滑动平均窗口大小 |
