using AutoUnlockManager.Core.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

/// <summary>
/// Confirmación de la recarga en caliente leyendo el fichero de estado del servicio
/// (reload-status.json): independiente del idioma del registro.
/// </summary>
public sealed class ConfigReloadWatcherTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);
    private readonly string _dir = Directory.CreateTempSubdirectory("aum-reload-").FullName;

    private string StatusFile => Path.Combine(_dir, "reload-status.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteStatus(string id, string result, bool device = false, bool credentials = false, string? message = null) =>
        File.WriteAllText(StatusFile,
            $$"""{"id":"{{id}}","result":"{{result}}","deviceChanged":{{(device ? "true" : "false")}},"credentialsChanged":{{(credentials ? "true" : "false")}},"message":{{(message is null ? "null" : $"\"{message}\"")}}}""");

    // Escribe tras un breve retardo, como haría el servicio después de su antirrebote
    private void WriteLater(Action write) => _ = Task.Run(async () =>
    {
        await Task.Delay(250);
        write();
    });

    [Fact]
    public void CaptureCheckpoint_missing_file_has_null_id()
        => Assert.Null(ConfigReloadWatcher.CaptureCheckpoint(StatusFile).Id);

    [Fact]
    public void CaptureCheckpoint_reads_current_id()
    {
        WriteStatus("abc", "applied");

        Assert.Equal("abc", ConfigReloadWatcher.CaptureCheckpoint(StatusFile).Id);
    }

    [Fact]
    public async Task WaitAsync_returns_applied_with_flags_on_new_id()
    {
        WriteStatus("old", "applied");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(StatusFile);
        WriteLater(() => WriteStatus("new", "applied", device: true));

        var outcome = await ConfigReloadWatcher.WaitAsync(StatusFile, checkpoint, Long);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
        Assert.True(outcome.DeviceChanged);
        Assert.False(outcome.CredentialsChanged);
    }

    [Fact]
    public async Task WaitAsync_unchanged_counts_as_reloaded()
    {
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(StatusFile);
        WriteLater(() => WriteStatus("n1", "unchanged"));

        var outcome = await ConfigReloadWatcher.WaitAsync(StatusFile, checkpoint, Long);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
    }

    [Fact]
    public async Task WaitAsync_returns_rejected_with_message()
    {
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(StatusFile);
        WriteLater(() => WriteStatus("n1", "rejected", message: "IRK invalid"));

        var outcome = await ConfigReloadWatcher.WaitAsync(StatusFile, checkpoint, Long);

        Assert.Equal(ConfigReloadStatus.Rejected, outcome.Status);
        Assert.Equal("IRK invalid", outcome.Message);
    }

    [Fact]
    public async Task WaitAsync_ignores_checkpoint_id()
    {
        WriteStatus("same", "rejected", message: "old");
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(StatusFile);

        var outcome = await ConfigReloadWatcher.WaitAsync(StatusFile, checkpoint, Short);

        Assert.Equal(ConfigReloadStatus.TimedOut, outcome.Status);
    }

    [Fact]
    public async Task WaitAsync_times_out_when_file_missing()
    {
        var outcome = await ConfigReloadWatcher.WaitAsync(StatusFile, new ConfigReloadCheckpoint(null), Short);

        Assert.Equal(ConfigReloadStatus.TimedOut, outcome.Status);
    }

    [Fact]
    public async Task WaitAsync_tolerates_corrupt_json_until_valid()
    {
        var checkpoint = ConfigReloadWatcher.CaptureCheckpoint(StatusFile);
        File.WriteAllText(StatusFile, "{\"id\":\"half");
        WriteLater(() => WriteStatus("n1", "applied"));

        var outcome = await ConfigReloadWatcher.WaitAsync(StatusFile, checkpoint, Long);

        Assert.Equal(ConfigReloadStatus.Reloaded, outcome.Status);
    }
}
