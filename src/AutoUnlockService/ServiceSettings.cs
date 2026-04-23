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
