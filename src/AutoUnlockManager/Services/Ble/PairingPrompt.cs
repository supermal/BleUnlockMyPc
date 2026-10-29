namespace AutoUnlockManager.Services.Ble;

/// <summary>配对过程中需要用户确认/输入的类型。</summary>
public enum PairingPromptKind
{
    /// <summary>设备请求配对，只需用户确认。</summary>
    ConfirmOnly,

    /// <summary>设备显示 PIN，用户需要在设备上输入。</summary>
    DisplayPin,

    /// <summary>用户在设备上看到 PIN，需要在管理端输入。</summary>
    ProvidePin,

    /// <summary>设备和管理端显示同一个 PIN，用户确认是否一致。</summary>
    ConfirmPinMatch,
}

/// <summary>交给管理端界面显示的配对提示。</summary>
public sealed record PairingPrompt(PairingPromptKind Kind, string DeviceName, string? Pin);

/// <summary>用户在配对提示中的选择。</summary>
public sealed record PairingPromptResult(bool Accepted, string? Pin = null)
{
    public static PairingPromptResult Accept(string? pin = null) => new(true, pin);

    public static PairingPromptResult Reject() => new(false);
}
