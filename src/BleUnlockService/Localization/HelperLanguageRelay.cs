using BleUnlock.Shared;
using BleUnlockService.Session;
using BleUnlockService.Resources;

namespace BleUnlockService.Localization;

// Mantiene al ayudante en el idioma del servicio: al conectarse, su cultura entra en el cálculo
// (modo «system») y recibe LANG; cada cambio posterior de idioma se le reenvía
public sealed class HelperLanguageRelay(
    HelperPipeServer helper, ServiceLanguage language, ILogger<HelperLanguageRelay> log) : IHostedService, IDisposable
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        helper.Connected += OnConnected;
        language.Changed += OnLanguageChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        helper.Connected -= OnConnected;
        language.Changed -= OnLanguageChanged;
    }

    private void OnConnected(int sessionId)
    {
        // Si la cultura cambia el idioma, OnLanguageChanged ya lo envía; si no, hay que enviarlo aquí
        var before = language.Current;
        language.SetUserCulture(helper.HelperCulture);
        if (language.Current == before) _ = SendAsync(before);
    }

    private void OnLanguageChanged(AppLanguage value)
    {
        if (helper.IsConnected) _ = SendAsync(value);
    }

    private async Task SendAsync(AppLanguage value)
    {
        try
        {
            await helper.SendAsync(HelperProtocol.BuildLanguage(value), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ServiceStrings.Log_SendLanguageFailed, ex.Message);
        }
    }
}
