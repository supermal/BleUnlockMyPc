using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoUnlockService;

public sealed class CredentialStore
{
    private readonly string _target;
    private readonly ILogger<CredentialStore> _logger;

    public CredentialStore(IOptions<ServiceSettings> settings, ILogger<CredentialStore> logger)
    {
        _target = settings.Value.CredentialTarget;
        _logger = logger;
    }

    public (string username, string password)? Read()
    {
        if (!Win32.CredRead(_target, Win32.CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            _logger.LogError("CredRead failed for target '{Target}': error {Error}",
                _target, Marshal.GetLastWin32Error());
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<Win32.CREDENTIAL>(credPtr);
            string username = cred.UserName ?? string.Empty;

            string password = string.Empty;
            if (cred.CredentialBlobSize > 0)
            {
                var blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
                password = Encoding.Unicode.GetString(blob);
            }

            return (username, password);
        }
        finally
        {
            Win32.CredFree(credPtr);
        }
    }
}
