namespace BleUnlockManager.Core;

/// <summary>
/// Arranque automático del servicio al abrir el gestor (2026-11-10). Al iniciar sesión el gestor suele
/// abrirse antes que el servicio; si lo encuentra parado lo arranca, salvo que el usuario lo detuviera a
/// propósito desde el gestor: esa decisión se recuerda con un fichero marcador hasta que vuelva a
/// arrancarlo o reiniciarlo él mismo.
/// </summary>
public static class ServiceAutoStart
{
    /// <summary>Arrancar solo si el servicio está instalado y parado, y nadie lo detuvo a mano.</summary>
    public static bool ShouldStart(bool stopped, bool stoppedByUser) => stopped && !stoppedByUser;

    public static bool StoppedByUser(string markerPath)
    {
        try
        {
            return File.Exists(markerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>El usuario detuvo el servicio (true) o lo volvió a arrancar o reiniciar (false).</summary>
    public static void RememberStoppedByUser(string markerPath, bool stopped)
    {
        try
        {
            if (stopped)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
                File.WriteAllText(markerPath, DateTimeOffset.Now.ToString("O"));
            }
            else if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin poder recordarlo, como mucho el servicio se arrancará la próxima vez: nada que romper
        }
    }
}
