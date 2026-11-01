using System.Globalization;
using System.Text;
using BleUnlockService.Core;

namespace BleUnlockService.Credentials;

// Protocolo de texto con el proveedor de credenciales C++ (verificado contra PipeClient.cpp):
//   servicio -> CP   CREDS\n<usuario>\n<clave>\n
//   CP -> servicio   ACK\n  y despues  RESULT OK\n  o  RESULT FAIL <8 hex mayusculas>\n
// El CP delimita por '\n': solo tolera '\r' en el verbo, nunca en usuario ni clave
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

        var hex = trimmed[ResultFailPrefix.Length..].Trim();
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var status))
            return false;

        result = new AuthResult(false, status);
        return true;
    }
}
