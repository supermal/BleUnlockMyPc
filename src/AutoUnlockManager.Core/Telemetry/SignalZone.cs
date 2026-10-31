using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Core.Telemetry;

/// <summary>Zona de la señal respecto a los umbrales: cerca, mantener o lejos.</summary>
public enum SignalZone
{
    Near,
    Hold,
    Away,
}

public static class SignalZones
{
    // Los umbrales son inclusivos, igual que en la máquina de estados del servicio
    public static SignalZone Classify(double dbm, double lockDbm, double unlockDbm) =>
        dbm >= unlockDbm ? SignalZone.Near
        : dbm <= lockDbm ? SignalZone.Away
        : SignalZone.Hold;

    public static string DisplayName(SignalZone zone) => zone switch
    {
        SignalZone.Near => CoreStrings.Zone_Near,
        SignalZone.Away => CoreStrings.Zone_Away,
        _ => CoreStrings.Zone_Hold,
    };
}
