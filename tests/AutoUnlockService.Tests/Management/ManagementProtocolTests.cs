using System.Text;
using System.Text.Json;
using AutoUnlockService.Management;
using Xunit;

namespace AutoUnlockService.Tests.Management;

// 锁定 Management v1 的线格式：单行 UTF-8 JSON、\n 结尾、camelCase 字段名（设计 §3.3）
public class ManagementProtocolTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static SnapshotFrame SampleFrame(string? scanStatus = "BLE 扫描已启动") => new(
        "snapshot",
        1,
        Seq: 813,
        Gen: 2,
        TsMs: T0.ToUnixTimeMilliseconds(),
        UptimeSec: 532.4,
        State: new SnapshotState("LockedArmed", T0.AddSeconds(-88).ToUnixTimeMilliseconds()),
        Scan: new SnapshotScan(scanStatus, null),
        Helper: new SnapshotConnection(true),
        Cred: new SnapshotConnection(false),
        Thr: new SnapshotThresholds(-65, -45, 10, 5, 3),
        Avg: new SnapshotAverages(-57.3, -58.1, 6, 14),
        LastMatchMs: 420,
        Samples: [[T0.ToUnixTimeMilliseconds(), -61], [T0.AddMilliseconds(1250).ToUnixTimeMilliseconds(), -59]]);

    private static JsonDocument Parse(string line) =>
        JsonDocument.Parse(Encoding.UTF8.GetBytes(line));

    [Fact]
    public void Hello_es_una_sola_linea_con_los_tres_campos()
    {
        var line = ManagementProtocol.SerializeLine(ManagementProtocol.CreateHello("1.2.3.4"));

        Assert.EndsWith("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.Equal(1, line.Count(c => c == '\n'));

        using var doc = Parse(line);
        var root = doc.RootElement;
        Assert.Equal(["t", "v", "svc"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("hello", root.GetProperty("t").GetString());
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("1.2.3.4", root.GetProperty("svc").GetString());
    }

    [Fact]
    public void Snapshot_es_una_sola_linea_utf8_sin_BOM()
    {
        var line = ManagementProtocol.SerializeLine(SampleFrame());

        Assert.EndsWith("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.Equal(1, line.Count(c => c == '\n'));
        Assert.DoesNotContain('\uFEFF', line);
    }

    [Fact]
    public void Snapshot_tiene_los_nombres_de_campo_estables()
    {
        var line = ManagementProtocol.SerializeLine(SampleFrame());
        using var doc = Parse(line);
        var root = doc.RootElement;

        Assert.Equal(
            ["t", "v", "seq", "gen", "tsMs", "uptimeSec", "state", "scan", "helper", "cred", "thr", "avg",
             "lastMatchMs", "samples"],
            root.EnumerateObject().Select(p => p.Name));

        Assert.Equal(["name", "sinceMs"],
            root.GetProperty("state").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["status", "detail"],
            root.GetProperty("scan").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["connected"],
            root.GetProperty("helper").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["connected"],
            root.GetProperty("cred").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["lockDbm", "unlockDbm", "lockWinSec", "unlockWinSec", "minSamples"],
            root.GetProperty("thr").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["w5", "w10", "n5", "n10"],
            root.GetProperty("avg").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Snapshot_transporta_los_valores_del_ejemplo_de_diseno()
    {
        var line = ManagementProtocol.SerializeLine(SampleFrame());
        using var doc = Parse(line);
        var root = doc.RootElement;

        Assert.Equal("snapshot", root.GetProperty("t").GetString());
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(813, root.GetProperty("seq").GetInt64());
        Assert.Equal(2, root.GetProperty("gen").GetInt64());
        Assert.Equal(532.4, root.GetProperty("uptimeSec").GetDouble(), 6);
        Assert.Equal("LockedArmed", root.GetProperty("state").GetProperty("name").GetString());
        Assert.True(root.GetProperty("helper").GetProperty("connected").GetBoolean());
        Assert.False(root.GetProperty("cred").GetProperty("connected").GetBoolean());
        Assert.Equal(-65, root.GetProperty("thr").GetProperty("lockDbm").GetDouble());
        Assert.Equal(-45, root.GetProperty("thr").GetProperty("unlockDbm").GetDouble());
        Assert.Equal(10, root.GetProperty("thr").GetProperty("lockWinSec").GetDouble());
        Assert.Equal(5, root.GetProperty("thr").GetProperty("unlockWinSec").GetDouble());
        Assert.Equal(3, root.GetProperty("thr").GetProperty("minSamples").GetInt32());
        Assert.Equal(-57.3, root.GetProperty("avg").GetProperty("w5").GetDouble(), 6);
        Assert.Equal(-58.1, root.GetProperty("avg").GetProperty("w10").GetDouble(), 6);
        Assert.Equal(6, root.GetProperty("avg").GetProperty("n5").GetInt32());
        Assert.Equal(14, root.GetProperty("avg").GetProperty("n10").GetInt32());
        Assert.Equal(420, root.GetProperty("lastMatchMs").GetInt64());

        var samples = root.GetProperty("samples");
        Assert.Equal(2, samples.GetArrayLength());
        Assert.Equal(T0.ToUnixTimeMilliseconds(), samples[0][0].GetInt64());
        Assert.Equal(-61, samples[0][1].GetInt64());
        Assert.Equal(-59, samples[1][1].GetInt64());
    }

    [Fact]
    public void Los_campos_opcionales_se_serializan_como_null_sin_omitirse()
    {
        var line = ManagementProtocol.SerializeLine(SampleFrame(scanStatus: null) with
        {
            Avg = new SnapshotAverages(null, null, 0, 0),
            LastMatchMs = null
        });
        using var doc = Parse(line);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("scan").GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("scan").GetProperty("detail").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("avg").GetProperty("w5").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("avg").GetProperty("w10").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastMatchMs").ValueKind);
    }

    [Fact]
    public void El_estado_textual_chino_sobrevive_al_ida_y_vuelta()
    {
        const string status = "未找到支持 BLE 的蓝牙适配器";
        var line = ManagementProtocol.SerializeLine(SampleFrame(scanStatus: status));

        var frame = JsonSerializer.Deserialize<SnapshotFrame>(line, ManagementProtocol.JsonOptions)!;

        Assert.Equal(status, frame.Scan.Status);
        Assert.Equal("LockedArmed", frame.State.Name);
        Assert.Equal(813, frame.Seq);
        Assert.Equal(2, frame.Gen);
        Assert.Equal(2, frame.Samples.Count);
        Assert.Equal(T0.ToUnixTimeMilliseconds(), frame.Samples[0][0]);
        Assert.Equal(-61, frame.Samples[0][1]);
        Assert.Equal(420, frame.LastMatchMs);
    }
}
