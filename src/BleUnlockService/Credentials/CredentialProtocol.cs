using System.Globalization;
using System.Text;
using BleUnlockService.Core;

namespace BleUnlockService.Credentials;

// Protocolo de texto con el proveedor de credenciales C++ (verificado contra PipeClient.cpp):
//   servicio -> CP   CREDS\n<usuario>\n<clave>\n
//   CP -> servicio   ACK\n  y despues  RESULT OK\n  o  RESULT FAIL <estado> [<subestado>]\n
//   (8 hex mayusculas cada uno; el subestado lo anade el CP desde 2026-11-03 y es opcional)
// El CP delimita por '\n' y quita un '\r' final de cada linea; aqui se manda solo '\n'
public static class CredentialProtocol
{
    private const string ResultOk = "RESULT OK";
    private const string ResultFailPrefix = "RESULT FAIL ";

    public static byte[] BuildCreds(string userName, string password)
    {
        // Un salto de linea dentro de un campo desplazaria el encuadre del CP.
        // Los mensajes no incluyen los valores: la clave no debe salir en excepciones
        if (userName.Contains('\n')) throw new ArgumentException("El usuario contiene un salto de linea.", nameof(userName));
        if (password.Contains('\n')) throw new ArgumentException("La clave contiene un salto de linea.", nameof(password));

        // Se escribe directo al arreglo para no crear otra copia de la clave en una cadena intermedia
        var encoding = Encoding.UTF8;
        var verb = encoding.GetBytes("CREDS\n");
        var buffer = new byte[verb.Length + encoding.GetByteCount(userName) + 1 + encoding.GetByteCount(password) + 1];

        var pos = 0;
        verb.CopyTo(buffer, pos);
        pos += verb.Length;
        pos += encoding.GetBytes(userName, 0, userName.Length, buffer, pos);
        buffer[pos++] = (byte)'\n';
        pos += encoding.GetBytes(password, 0, password.Length, buffer, pos);
        buffer[pos] = (byte)'\n';
        return buffer;
    }

    public static bool TryParseResult(string line, out AuthResult result)
    {
        result = new AuthResult(false, 0);
        var trimmed = line.Trim();

        if (trimmed == ResultOk)
        {
            result = new AuthResult(true, 0);
            return true;
        }

        if (!trimmed.StartsWith(ResultFailPrefix, StringComparison.Ordinal)) return false;

        var parts = trimmed[ResultFailPrefix.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2) return false;
        if (!TryParseCode(parts[0], out var status)) return false;

        uint substatus = 0;
        if (parts.Length == 2 && !TryParseCode(parts[1], out substatus)) return false;

        result = new AuthResult(false, status, substatus);
        return true;
    }

    // El CP escribe siempre %08X: exactamente 8 digitos hexadecimales
    private static bool TryParseCode(string text, out uint code)
    {
        code = 0;
        return text.Length == 8 &&
               uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }
}
