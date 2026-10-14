namespace AutoUnlockService.Credentials;

public readonly record struct LogonName(string? Domain, string User);

// Las tres formas que acepta Windows: local, dominio y cuenta Microsoft.
// El UPN se pasa entero con dominio nulo, que es como LogonUser espera resolverlo
public static class UserNameParser
{
    public static LogonName Split(string userName)
    {
        var trimmed = userName.Trim();

        int slash = trimmed.IndexOf('\\');
        if (slash > 0)
            return new LogonName(trimmed[..slash], trimmed[(slash + 1)..]);

        // Una barra al principio no aporta dominio: "\Admin" es una cuenta local
        if (slash == 0) trimmed = trimmed[1..];

        return trimmed.Contains('@')
            ? new LogonName(null, trimmed)
            : new LogonName(".", trimmed);
    }
}
