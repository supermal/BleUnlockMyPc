using System.Diagnostics;
using Windows.Devices.Bluetooth;
using BleUnlockService.Resources;

namespace BleUnlockService.Ble;

public interface IBluetoothAdapterRestarter
{
    // true si el reinicio se pidió con éxito; los fallos se registran y no se lanzan
    Task<bool> RestartAsync(CancellationToken ct);
}

// Reinicia el adaptador Bluetooth predeterminado (equivale a desactivarlo y activarlo en el
// Administrador de dispositivos). Los dispositivos conectados (auriculares, ratón) se desconectan
// unos segundos y vuelven solos. El servicio corre como LocalSystem, así que pnputil no pide UAC.
public sealed class BluetoothAdapterRestarter(ILogger<BluetoothAdapterRestarter> log) : IBluetoothAdapterRestarter
{
    private static readonly TimeSpan PnpUtilTimeout = TimeSpan.FromSeconds(60);

    public async Task<bool> RestartAsync(CancellationToken ct)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            var instanceId = adapter is null ? null : ToInstanceId(adapter.DeviceId);
            if (instanceId is null)
            {
                log.LogWarning(ServiceStrings.Log_AdapterRestartFailed, ServiceStrings.Scan_NoAdapter);
                return false;
            }

            log.LogWarning(ServiceStrings.Log_AdapterRestarting, instanceId);
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/restart-device");
            psi.ArgumentList.Add(instanceId);

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PnpUtilTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch { /* ya terminó */ }
                log.LogWarning(ServiceStrings.Log_AdapterRestartFailed, $"pnputil timeout {PnpUtilTimeout.TotalSeconds} s");
                return false;
            }

            if (process.ExitCode != 0)
            {
                var text = (await output).Trim();
                log.LogWarning(ServiceStrings.Log_AdapterRestartFailed, $"pnputil exit {process.ExitCode}: {text}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            log.LogWarning(ServiceStrings.Log_AdapterRestartFailed, ex.Message);
            return false;
        }
    }

    // DeviceId de WinRT («\\?\USB#VID_8087&PID_0026#5&261d42b0&0&10#{guid}») → id de instancia
    // PnP («USB\VID_8087&PID_0026\5&261d42b0&0&10»), que es lo que acepta pnputil
    internal static string? ToInstanceId(string deviceId)
    {
        var id = deviceId.StartsWith(@"\\?\", StringComparison.Ordinal) ? deviceId[4..] : deviceId;
        var guid = id.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid >= 0) id = id[..guid];
        id = id.Replace('#', '\\');
        return id.Length == 0 ? null : id;
    }
}
