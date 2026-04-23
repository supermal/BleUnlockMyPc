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
