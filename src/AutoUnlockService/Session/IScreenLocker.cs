namespace AutoUnlockService.Session;

public interface IScreenLocker
{
    Task<bool> LockAsync(CancellationToken ct);
}
