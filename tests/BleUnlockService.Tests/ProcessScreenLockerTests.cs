using BleUnlockService.Session;
using Xunit;

namespace BleUnlockService.Tests;

public class ProcessScreenLockerTests
{
    [Fact]
    public void El_comando_de_bloqueo_no_tiene_caracteres_de_control_y_apunta_a_un_fichero_existente()
    {
        var command = ProcessScreenLocker.BuildCommand();

        Assert.DoesNotContain(command, char.IsControl);

        // Formato esperado: "ruta" argumentos
        Assert.StartsWith("\"", command);
        var end = command.IndexOf('"', 1);
        var exe = command[1..end];
        Assert.True(File.Exists(exe), $"No existe: {exe}");
        Assert.EndsWith("rundll32.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(" user32.dll,LockWorkStation", command[(end + 1)..]);
    }
}
