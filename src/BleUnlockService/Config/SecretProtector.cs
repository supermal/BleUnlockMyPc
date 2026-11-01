using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BleUnlockService.Config;

/// <summary>
/// appsettings.json 敏感值（凭据密码、设备 IRK）的静态加密：Windows DPAPI 机器范围
/// （<c>CRYPTPROTECT_LOCAL_MACHINE</c>），密文格式 <c>dpapi:v1:{Base64}</c>。
/// 同一文件由服务端与管理端共享编译（<c>Link</c> 进 BleUnlockManager.Core），格式与熵单一实现、不会漂移。
/// 机器绑定：密文拷到其他机器无法解开；明文视为未加密（兼容旧配置，由管理端保存时自动加密）。
/// 注意：本加密只防配置文件被拷贝/外发后的明文泄露；本机管理员或 SYSTEM 权限程序仍可调用 DPAPI 解开。
/// </summary>
public static class SecretProtector
{
    /// <summary>密文前缀（格式标识，冻结不得改动；解密只认此前缀，其他值按明文处理）。</summary>
    public const string Prefix = "dpapi:v1:";

    // 应用熵：把本应用的 DPAPI blob 与其他用途隔离（他人拿到 blob 也无法用空熵解开）
    // Conserva el nombre anterior del producto a propósito: cambiarlo dejaría sin poder descifrar
    // las contraseñas ya guardadas (incluidas las que se migran desde AutoUnlock). No renombrar.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AutoUnlock.MyPc.Secrets.v1");

    private const int CryptProtectLocalMachine = 0x4; // CRYPTPROTECT_LOCAL_MACHINE
    private const int CryptProtectUiForbidden = 0x1;  // CRYPTPROTECT_UI_FORBIDDEN

    /// <summary>值是否为本格式的密文（只看前缀；不解密、不校验内容）。</summary>
    public static bool IsProtected(string? value)
        => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 加密为 <c>dpapi:v1:</c> 密文；空串原样返回（未设置不加密）。DPAPI 调用失败抛 <see cref="CryptographicException"/>。
    /// </summary>
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return plaintext;
        }

        var blob = Transform(Encoding.UTF8.GetBytes(plaintext), protect: true)
            // Sin texto propio: este fichero se comparte con el gestor y no ve sus recursos;
            // quien lo captura muestra el mensaje en su idioma
            ?? throw new CryptographicException();
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// 解密：非密文（明文/空串）原样返回 <c>true</c>；密文解密失败（跨机器/损坏/他机熵）返回 <c>false</c>。
    /// </summary>
    public static bool TryUnprotect(string value, out string plaintext)
    {
        if (!IsProtected(value))
        {
            plaintext = value;
            return true;
        }

        plaintext = "";
        try
        {
            var blob = Convert.FromBase64String(value[Prefix.Length..]);
            if (blob.Length == 0)
            {
                return false;
            }

            var bytes = Transform(blob, protect: false);
            if (bytes is null)
            {
                return false;
            }

            plaintext = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false; // Base64 非法：视为不可解密，不抛出
        }
    }

    // DPAPI 调用（protect=true 用 CryptProtectData，否则 CryptUnprotectData）；失败返回 null。
    private static byte[]? Transform(byte[] data, bool protect)
    {
        var input = default(DataBlob);
        var entropy = default(DataBlob);
        var output = default(DataBlob);
        var inputPtr = IntPtr.Zero;
        var entropyPtr = IntPtr.Zero;
        try
        {
            inputPtr = CopyToUnmanaged(data);
            input = new DataBlob { cbData = data.Length, pbData = inputPtr };
            entropyPtr = CopyToUnmanaged(Entropy);
            entropy = new DataBlob { cbData = Entropy.Length, pbData = entropyPtr };

            var ok = protect
                ? CryptProtectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectLocalMachine | CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output);
            if (!ok)
            {
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }

            if (inputPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPtr);
            }

            if (entropyPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyPtr);
            }
        }
    }

    private static IntPtr CopyToUnmanaged(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, IntPtr szDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
