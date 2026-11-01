namespace BleUnlockService.Session;

public interface IScreenLocker
{
    Task<bool> LockAsync(CancellationToken ct);
}
