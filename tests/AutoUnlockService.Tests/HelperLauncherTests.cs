using AutoUnlockService.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests;

public class HelperLauncherTests
{
    private sealed class FakeLink : IHelperLink
    {
        private volatile bool _connected;
        public bool IsConnected => _connected;
        public event Action<int>? Connected;

        public void Connect()
        {
            // El evento va primero: asi quien vea IsConnected ya ve tambien el contador reiniciado
            Connected?.Invoke(1);
            _connected = true;
        }

        public void Disconnect() => _connected = false;
    }

    // Lanzador con guion: cada llamada consume el siguiente comportamiento; al agotarse repite el ultimo
    private sealed class Rig : IDisposable
    {
        private readonly Queue<Func<(UserSessionStartResult, int)>> _script = new();
        private Func<(UserSessionStartResult, int)> _last = () => (UserSessionStartResult.Failed, 0);
        private int _launches;
        private int _degrades;
        private int _terminated;

        public FakeTimeProvider Time { get; } = new();
        public FakeLink Link { get; } = new();
        public HelperLauncher Launcher { get; }
        public int Launches => Volatile.Read(ref _launches);
        public int Degrades => Volatile.Read(ref _degrades);
        public int Terminated => Volatile.Read(ref _terminated);
        public List<int> TerminatedPids { get; } = [];
        public List<string> Events { get; } = [];

        public Rig()
        {
            Launcher = new HelperLauncher(Link, NullLogger.Instance, Time, _ =>
            {
                Interlocked.Increment(ref _launches);
                lock (Events) Events.Add("launch");
                if (_script.Count > 0) _last = _script.Dequeue();
                return _last();
            }, pid =>
            {
                lock (TerminatedPids) TerminatedPids.Add(pid);
                lock (Events) Events.Add($"terminate:{pid}");
                Interlocked.Increment(ref _terminated);
            });
            Launcher.DegradeRequested += _ => Interlocked.Increment(ref _degrades);
        }

        public Rig Then(Func<(UserSessionStartResult, int)> step)
        {
            _script.Enqueue(step);
            return this;
        }

        public Rig ThenFail() => Then(() => (UserSessionStartResult.Failed, 0));
        public Rig ThenNoUser() => Then(() => (UserSessionStartResult.NoUserSession, 0));
        public Rig ThenStartedNeverConnects(int pid = 4242) => Then(() => (UserSessionStartResult.Started, pid));

        // Arranca y el ayudante conecta enseguida, como en el caso sano
        public Rig ThenStartedAndConnects() => Then(() =>
        {
            Link.Connect();
            return (UserSessionStartResult.Started, 100);
        });

        public void Start() => Launcher.Start(CancellationToken.None);

        public async Task AdvanceFixedAsync(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Yield();
            }
        }

        public void Dispose() => Launcher.Dispose();
    }

    [Fact]
    public async Task Arranque_que_siempre_falla_pide_degradar_una_sola_vez_tras_tres_intentos()
    {
        using var rig = new Rig().ThenFail();
        rig.Start();

        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Degrades >= 1);
        Assert.Equal(3, rig.Launches);

        await rig.AdvanceFixedAsync(200);
        Assert.Equal(1, rig.Degrades);
    }

    [Fact]
    public async Task Tras_degradar_el_lanzador_sigue_reintentando_mas_despacio()
    {
        using var rig = new Rig().ThenFail();
        rig.Start();
        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Degrades >= 1);
        var atDegrade = rig.Launches;

        // 600 s simulados: con el reintento lento (60 s) caben varios intentos, pero no cientos
        await rig.AdvanceFixedAsync(600);

        Assert.InRange(rig.Launches - atDegrade, 5, 15);
        Assert.Equal(1, rig.Degrades);
    }

    [Fact]
    public async Task Un_arranque_lento_puntual_no_desactiva_el_ayudante_para_siempre()
    {
        using var rig = new Rig().ThenFail().ThenFail().ThenFail().ThenFail().ThenStartedAndConnects();
        rig.Start();

        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Link.IsConnected);

        Assert.Equal(1, rig.Degrades);
        Assert.Equal(0, rig.Launcher.ConsecutiveFailures);
    }

    [Fact]
    public async Task Ayudante_que_arranca_pero_nunca_conecta_cuenta_como_fallo_y_se_retira()
    {
        using var rig = new Rig().ThenStartedNeverConnects(4242);
        rig.Start();

        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Degrades >= 1);

        Assert.Equal(3, rig.Launches);
        Assert.Equal(new[] { 4242, 4242, 4242 }, rig.TerminatedPids);
    }

    [Fact]
    public async Task Si_la_tuberia_cae_pero_el_proceso_sigue_vivo_se_retira_antes_de_relanzar()
    {
        using var rig = new Rig().ThenStartedAndConnects();
        rig.Start();
        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Link.IsConnected);

        rig.Link.Disconnect();
        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Launches >= 2);

        Assert.Equal(new[] { "launch", "terminate:100", "launch" }, rig.Events.Take(3));
    }

    [Fact]
    public async Task Start_llamado_dos_veces_no_duplica_el_guardian()
    {
        using var rig = new Rig().ThenStartedAndConnects();
        rig.Start();
        rig.Start();

        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Link.IsConnected);
        await rig.AdvanceFixedAsync(100);

        Assert.Equal(1, rig.Launches);
    }

    [Fact]
    public async Task Matar_el_ayudante_repetidamente_no_dispara_la_degradacion()
    {
        using var rig = new Rig().ThenStartedAndConnects();
        rig.Start();

        // El usuario mata el ayudante cinco veces; cada vez el relanzamiento conecta
        for (int kill = 1; kill <= 5; kill++)
        {
            await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Launches >= kill && rig.Link.IsConnected);
            rig.Link.Disconnect();
        }
        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Launches >= 6 && rig.Link.IsConnected);
        await rig.AdvanceFixedAsync(100);

        Assert.Equal(0, rig.Degrades);
        Assert.Equal(0, rig.Launcher.ConsecutiveFailures);
    }

    [Fact]
    public async Task Conectar_reinicia_el_contador_de_fallos()
    {
        using var rig = new Rig().ThenFail().ThenFail().ThenStartedAndConnects();
        rig.Start();

        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Launches >= 3 && rig.Link.IsConnected);
        Assert.Equal(0, rig.Launcher.ConsecutiveFailures);

        // Dos fallos nuevos no bastan: los anteriores ya no cuentan
        rig.ThenFail();
        rig.Link.Disconnect();
        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Launcher.ConsecutiveFailures >= 2);
        await rig.AdvanceFixedAsync(1);

        Assert.Equal(0, rig.Degrades);
    }

    [Fact]
    public async Task Sin_usuario_con_sesion_iniciada_no_cuenta_como_fallo_y_sigue_reintentando()
    {
        using var rig = new Rig().ThenNoUser();
        rig.Start();

        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Launches >= 10);
        Assert.Equal(0, rig.Degrades);
        Assert.Equal(0, rig.Launcher.ConsecutiveFailures);

        // Cuando por fin hay usuario, el ayudante arranca y conecta
        rig.ThenStartedAndConnects();
        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Link.IsConnected);
        Assert.Equal(0, rig.Degrades);
    }

    [Fact]
    public async Task Mientras_el_ayudante_esta_conectado_no_se_lanza_otro()
    {
        using var rig = new Rig().ThenStartedAndConnects();
        rig.Start();

        await TestSupport.AdvanceUntilAsync(rig.Time, () => rig.Link.IsConnected);
        await rig.AdvanceFixedAsync(200);

        Assert.Equal(1, rig.Launches);
    }
}
