using BleUnlockService.Core;
using Xunit;

namespace BleUnlockService.Tests;

public class CredentialAttemptBudgetTests
{
    [Fact]
    public void Se_agota_solo_al_llegar_al_maximo()
    {
        var budget = new CredentialAttemptBudget(3);

        Assert.False(budget.IsExhausted);
        Assert.False(budget.NoteAttempt());
        Assert.False(budget.NoteAttempt());
        Assert.False(budget.IsExhausted);
        Assert.True(budget.NoteAttempt());   // tercer envio: alcanza el tope
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void El_aviso_de_agotamiento_es_unico()
    {
        var budget = new CredentialAttemptBudget(1);

        Assert.True(budget.NoteAttempt());
        Assert.False(budget.NoteAttempt());  // ya avisado, no se repite
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void Reset_reabre_el_presupuesto_y_vuelve_a_avisar()
    {
        var budget = new CredentialAttemptBudget(2);
        budget.NoteAttempt();
        budget.NoteAttempt();
        Assert.True(budget.IsExhausted);

        budget.Reset();

        Assert.False(budget.IsExhausted);
        Assert.False(budget.NoteAttempt());
        Assert.True(budget.NoteAttempt());   // alcanza el tope de nuevo: vuelve a avisar
    }

    [Fact]
    public void Tope_minimo_es_uno()
    {
        var budget = new CredentialAttemptBudget(0);

        Assert.True(budget.NoteAttempt());
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void Concurrencia_no_produce_mas_de_un_aviso()
    {
        var budget = new CredentialAttemptBudget(10);
        int notices = 0;

        Parallel.For(0, 100, _ =>
        {
            if (budget.NoteAttempt()) Interlocked.Increment(ref notices);
        });

        Assert.Equal(1, notices);
        Assert.True(budget.IsExhausted);
    }
}
