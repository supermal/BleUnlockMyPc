namespace BleUnlockManager.Services.Ble;

/// <summary>配对/解除配对的轻量结果（不向 UI 抛异常，消息可直接展示）。</summary>
public sealed record BlePairingResult(bool Success, string Message)
{
    public static BlePairingResult Ok(string message) => new(true, message);
    public static BlePairingResult Fail(string message) => new(false, message);
}
