using AutoUnlockService.Config;
using AutoUnlockService.Core;
using AutoUnlockService.Credentials;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class ProximityStateMachineUnlockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static (ProximityStateMachine Sm, FakeTimeProvider Time, RssiHistory History) Build()
    {
        var time = new FakeTimeProvider(T0);
        var history = new RssiHistory(TimeSpan.FromSeconds(120), time);
        // Arrancamos con la pantalla ya bloqueada: es el escenario de esta bateria
        var sm = new ProximityStateMachine(time, new ProximityOptions(), new NotificationOptions(), true);
        return (sm, time, history);
    }

    private static void Fill(RssiHistory h, FakeTimeProvider time, short rssi, int count)
    {
        var now = time.GetUtcNow();
        for (int i = 0; i < count; i++)
            h.Add(rssi, now.AddSeconds(-i));
    }

    // Lleva la maquina hasta LockedArmed: el usuario se fue de verdad
    private static void Arm(ProximityStateMachine sm, FakeTimeProvider time, RssiHistory history)
    {
        history.Add(-50, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(30));
        sm.Evaluate(history);
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }

    [Fact]
    public void Pegado_al_ordenador_tras_bloquear_no_desbloquea_nunca()
    {
        var (sm, time, history) = Build();

        // El usuario pulso Win+L y sigue sentado: senal fortisima, pero nunca se fue
        for (int i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Fill(history, time, -30, 5);
            var change = sm.Evaluate(history);
            Assert.NotEqual(ProximityAction.SendCredentials, change?.Action ?? ProximityAction.None);
        }

        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Tras_detectar_la_salida_queda_armado()
    {
        var (sm, time, history) = Build();
        history.Add(-50, T0);
        time.Advance(TimeSpan.FromSeconds(30));   // ventana sin senal

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedArmed, change?.To);
        Assert.Equal(ProximityAction.None, change!.Action);
    }

    [Fact]
    public void Armado_y_cerca_envia_las_credenciales_una_sola_vez()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);

        var change = sm.Evaluate(history);
        Assert.Equal(ProximityState.UnlockPending, change?.To);
        Assert.Equal(ProximityAction.SendCredentials, change!.Action);

        // Mientras espera resultado no vuelve a enviar, aunque siga cerca
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        Assert.Null(sm.Evaluate(history));
    }

    [Fact]
    public void El_resultado_correcto_devuelve_a_Unlocked()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(true, 0));

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Un_desbloqueo_manual_durante_la_espera_tambien_cierra_el_ciclo()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnSessionEvent(SessionEvent.Unlock);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Clave_erronea_deja_el_servicio_parado_y_avisa()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        Assert.Equal(ProximityState.Suspended, change?.To);
        Assert.Equal(ProximityAction.NotifySuspended, change!.Action);
    }

    [Fact]
    public void Un_fallo_que_no_es_de_credencial_solo_reintenta()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        var change = sm.OnAuthResult(new AuthResult(false, 0xC0000022));

        Assert.Equal(ProximityState.LockedArmed, change?.To);
    }

    [Fact]
    public void Si_nunca_llega_el_resultado_vuelve_a_quedar_armado()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        // ReportResult puede no llegar nunca si LogonUI se destruye
        time.Advance(TimeSpan.FromSeconds(21));   // UnlockPendingTimeoutSeconds = 20

        var change = sm.Evaluate(history);

        Assert.Equal(ProximityState.LockedArmed, change?.To);
        Assert.NotEqual(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Suspended_no_se_sale_con_un_desbloqueo_manual()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        // El usuario entra escribiendo la clave a mano; eso no arregla el fichero
        Assert.Null(sm.OnSessionEvent(SessionEvent.Unlock));
        Assert.Equal(ProximityState.Suspended, sm.State);

        Assert.Null(sm.OnSessionEvent(SessionEvent.Lock));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Suspended_ignora_las_muestras()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        time.Advance(TimeSpan.FromSeconds(5));
        Fill(history, time, -40, 5);

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Corregir_la_clave_reanuda_el_servicio_en_el_estado_correcto()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        // La pantalla sigue bloqueada, asi que hay que volver a exigir la salida
        var change = sm.OnCredentialsChanged(preValidationPassed: true);

        Assert.Equal(ProximityState.LockedWaitingDeparture, change?.To);
    }

    [Fact]
    public void Corregir_la_clave_con_la_pantalla_abierta_reanuda_en_Unlocked()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        sm.OnSessionEvent(SessionEvent.Unlock);   // el usuario entro a mano
        var change = sm.OnCredentialsChanged(preValidationPassed: true);

        Assert.Equal(ProximityState.Unlocked, change?.To);
    }

    [Fact]
    public void Una_clave_que_sigue_sin_validar_no_reanuda()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));

        Assert.Null(sm.OnCredentialsChanged(preValidationPassed: false));
        Assert.Equal(ProximityState.Suspended, sm.State);
    }

    [Fact]
    public void Una_credencial_que_no_valida_para_el_servicio_aunque_no_hubiera_fallado_antes()
    {
        var (sm, _, _) = Build();

        var change = sm.OnCredentialsChanged(preValidationPassed: false);

        Assert.Equal(ProximityState.Suspended, change?.To);
        Assert.Equal(ProximityAction.NotifySuspended, change!.Action);
    }

    [Fact]
    public void Armado_pero_con_senal_debil_no_envia_credenciales()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);

        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -60, 5);   // entre -80 y -45: zona muerta

        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.LockedArmed, sm.State);
    }

    [Fact]
    public void Pegado_al_ordenador_tras_bloquear_no_desbloquea_nunca_ni_con_senal_maxima_y_mucho_tiempo()
    {
        var (sm, time, history) = Build();

        // Mas de dos minutos con senal altisima: la guardia de salida no caduca por tiempo
        for (int i = 0; i < 150; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Fill(history, time, -20, 5);
            Assert.Null(sm.Evaluate(history));
            Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
        }
    }

    [Fact]
    public void Tras_irse_y_volver_el_ciclo_completo_envia_credenciales()
    {
        var (sm, time, history) = Build();
        Fill(history, time, -30, 5);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(sm.Evaluate(history));                 // sigue sentado
        time.Advance(TimeSpan.FromSeconds(30));            // se va
        Assert.Equal(ProximityState.LockedArmed, sm.Evaluate(history)?.To);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -35, 5);                       // vuelve
        Assert.Equal(ProximityAction.SendCredentials, sm.Evaluate(history)?.Action);
    }

    [Theory]
    [InlineData(0xC000006Du)]
    [InlineData(0xC0000071u)]
    [InlineData(0xC0000224u)]
    [InlineData(0xC0000234u)]
    public void Los_cuatro_fallos_de_credencial_paran_el_servicio(uint status)
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        Assert.Equal(ProximityState.Suspended, sm.OnAuthResult(new AuthResult(false, status))?.To);
    }

    [Fact]
    public void Un_resultado_fuera_de_UnlockPending_se_ignora()
    {
        var (sm, _, _) = Build();

        Assert.Null(sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure)));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Antes_del_plazo_UnlockPending_sigue_esperando()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        time.Advance(TimeSpan.FromSeconds(19));
        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.UnlockPending, sm.State);
    }

    [Fact]
    public void Tras_el_timeout_si_sigue_cerca_reintenta_el_envio()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);

        time.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(ProximityState.LockedArmed, sm.Evaluate(history)?.To);

        Fill(history, time, -40, 5);
        Assert.Equal(ProximityAction.SendCredentials, sm.Evaluate(history)?.Action);
    }

    [Fact]
    public void Suspended_recuerda_el_bloqueo_ocurrido_mientras_estaba_parado()
    {
        var (sm, _, _) = Build(); // pantalla bloqueada al arrancar
        sm.OnCredentialsChanged(preValidationPassed: false);
        sm.OnSessionEvent(SessionEvent.Unlock);
        sm.OnSessionEvent(SessionEvent.Lock);

        Assert.Equal(ProximityState.LockedWaitingDeparture,
            sm.OnCredentialsChanged(preValidationPassed: true)?.To);
    }

    [Fact]
    public void Una_credencial_valida_fuera_de_Suspended_no_cambia_nada()
    {
        var (sm, _, _) = Build();

        Assert.Null(sm.OnCredentialsChanged(preValidationPassed: true));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }

    [Fact]
    public void Tras_reanudar_hay_que_volver_a_exigir_la_salida_antes_de_enviar()
    {
        var (sm, time, history) = Build();
        Arm(sm, time, history);
        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -40, 5);
        sm.Evaluate(history);
        sm.OnAuthResult(new AuthResult(false, NtStatusPolicy.StatusLogonFailure));
        sm.OnCredentialsChanged(preValidationPassed: true);

        time.Advance(TimeSpan.FromSeconds(1));
        Fill(history, time, -30, 5);
        Assert.Null(sm.Evaluate(history));
        Assert.Equal(ProximityState.LockedWaitingDeparture, sm.State);
    }
}
