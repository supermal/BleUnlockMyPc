using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AutoUnlockService.Config;
using AutoUnlockService.Management;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AutoUnlockService.Tests.Management;

// 使用真实命名管道（名称每用例唯一）：hello 帧、连接回溯、250ms 节拍、
// 断连重连与"第二客户端必须等待"的现状。生产 DACL 只放行 SYSTEM+Administrators：
// 带 UAC 过滤管理员令牌的测试进程会被拒绝（Administrators 组仅用于 deny），
// 因此回环用例注入测试 DACL，使回环与账户状态无关；生产 DACL 另由专门用例锁定
public class ManagementPipeServerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TestGuard _guard = new();
    private CancellationToken Guard => _guard.Token;

    public void Dispose() => _guard.Dispose();

    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    private static PipeSecurity TestSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static TelemetryHub NewHub()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(T0);
        return new TelemetryHub(TestGates.New(clock), clock, NullLogger<TelemetryHub>.Instance);
    }

    private static ManagementPipeServer NewServer(TelemetryHub hub, string pipeName) =>
        new(hub, NullLogger<ManagementPipeServer>.Instance, TimeProvider.System, pipeName, TestSecurity());

    private static async Task<T> ReadFrameAsync<T>(FakeManagementClient client, CancellationToken ct)
    {
        var line = await client.ReadLineAsync(ct);
        return JsonSerializer.Deserialize<T>(line, ManagementProtocol.JsonOptions)!;
    }

    [Fact]
    public void La_DACL_de_produccion_solo_permite_a_SYSTEM_y_Administrators()
    {
        var security = ManagementPipeServer.CreateDefaultSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>().ToList();

        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(PipeAccessRights.FullControl, r.PipeAccessRights);
        });
        Assert.Contains(rules, r =>
            r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);
        Assert.Contains(rules, r =>
            r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value);
    }

    [Fact]
    public async Task Al_conectar_se_recibe_hello_y_la_retrospectiva_y_luego_incrementos()
    {
        var hub = NewHub();
        hub.OnSample(Ms(T0.AddSeconds(-2)), -61);
        hub.OnSample(Ms(T0.AddSeconds(-1)), -59);

        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(hub, pipeName);
        await server.StartAsync(Guard);
        try
        {
            await using var client = await FakeManagementClient.ConnectAsync(pipeName);

            var hello = await ReadFrameAsync<HelloFrame>(client, Guard);
            Assert.Equal("hello", hello.T);
            Assert.Equal(1, hello.V);
            Assert.False(string.IsNullOrWhiteSpace(hello.Svc));

            var snapshot = await ReadFrameAsync<SnapshotFrame>(client, Guard);
            Assert.Equal("snapshot", snapshot.T);
            Assert.Equal(2, snapshot.Samples.Count);
            Assert.Equal(-61, snapshot.Samples[0][1]);

            // 节拍持续推送：下一帧是增量（连接后没有新样本）
            var next = await ReadFrameAsync<SnapshotFrame>(client, Guard);
            Assert.Equal(snapshot.Seq + 1, next.Seq);
            Assert.Empty(next.Samples);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Tras_la_desconexion_una_nueva_conexion_recibe_retrospectiva_con_el_buffer_mantenido()
    {
        var hub = NewHub();
        hub.OnSample(Ms(T0.AddSeconds(-2)), -61);

        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(hub, pipeName);
        await server.StartAsync(Guard);
        try
        {
            var first = await FakeManagementClient.ConnectAsync(pipeName);
            Assert.Equal("hello", (await ReadFrameAsync<HelloFrame>(first, Guard)).T);
            Assert.Single((await ReadFrameAsync<SnapshotFrame>(first, Guard)).Samples);
            await first.DisposeAsync();

            // 无客户端期间：环形缓冲继续维护
            hub.OnSample(Ms(T0.AddSeconds(-1)), -55);

            await using var second = await FakeManagementClient.ConnectAsync(pipeName);
            var hello = await ReadFrameAsync<HelloFrame>(second, Guard);
            Assert.Equal("hello", hello.T);

            // 新连接拿到的仍是回溯：包含旧样本和无客户端期间的新样本
            var snapshot = await ReadFrameAsync<SnapshotFrame>(second, Guard);
            Assert.Equal(2, snapshot.Samples.Count);
            Assert.Equal(-61, snapshot.Samples[0][1]);
            Assert.Equal(-55, snapshot.Samples[1][1]);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Con_un_cliente_activo_el_segundo_debe_esperar_y_el_primero_no_se_ve_afectado()
    {
        var hub = NewHub();
        var pipeName = TestSupport.UniquePipeName();
        var server = NewServer(hub, pipeName);
        await server.StartAsync(Guard);
        try
        {
            await using var first = await FakeManagementClient.ConnectAsync(pipeName);
            Assert.Equal("hello", (await ReadFrameAsync<HelloFrame>(first, Guard)).T);

            // 现状如实断言：单实例 + 无主动顶替，第二个客户端在首客户端在线期间
            // 只能等待（ConnectAsync 阻塞），短超时后以取消收场
            using var second = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.ConnectAsync(cts.Token));

            // 首客户端不受影响：节拍继续
            Assert.Equal("snapshot", (await ReadFrameAsync<SnapshotFrame>(first, Guard)).T);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }
}
