#include "Serialization.h"
#include "guids.h"
#include "CpLog.h"
#include <ntsecapi.h>
#include <subauth.h>
#include <wincred.h>
#include <string>

#pragma comment(lib, "secur32.lib")
#pragma comment(lib, "advapi32.lib")

// Paquete que usa el proveedor de contrasenas de Windows: Negotiate elige Kerberos o NTLM y
// tambien atiende cuentas Microsoft («MicrosoftAccount\correo») y la cache de inicio sin red
static const char NEGOTIATE_PACKAGE[] = "Negotiate";

namespace
{
    // Partes del nombre de inicio de sesion
    struct LogonName
    {
        std::wstring domain;   // vacio para un UPN
        std::wstring user;
        bool         local;    // cuenta de esta maquina: va por MSV1_0, el camino ya probado
    };

    std::wstring ComputerName()
    {
        WCHAR name[MAX_COMPUTERNAME_LENGTH + 1] = {};
        DWORD size = ARRAYSIZE(name);
        if (!GetComputerNameW(name, &size)) return L"";
        return std::wstring(name, size);
    }

    bool EqualsIgnoreCase(const std::wstring& a, const std::wstring& b)
    {
        return CompareStringOrdinal(a.c_str(), static_cast<int>(a.size()),
                                    b.c_str(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
    }

    LogonName SplitName(const std::wstring& username)
    {
        LogonName result;
        auto pos = username.find(L'\\');
        if (pos != std::wstring::npos)
        {
            result.domain = username.substr(0, pos);
            result.user   = username.substr(pos + 1);
        }
        else
        {
            result.user = username;
        }

        // UPN («usuario@dominio» o el correo de una cuenta Microsoft): Negotiate lo resuelve
        // entero, sin dominio aparte
        if (result.domain.empty() && result.user.find(L'@') != std::wstring::npos)
        {
            result.local = false;
            return result;
        }

        std::wstring computer = ComputerName();
        if (result.domain.empty() || result.domain == L"." || EqualsIgnoreCase(result.domain, computer))
        {
            result.domain = computer;
            result.local  = true;
        }
        else
        {
            result.local = false;
        }
        return result;
    }

    HRESULT GetAuthPackageId(LPCSTR packageName, ULONG* pulId)
    {
        HANDLE hLsa;
        NTSTATUS status = LsaConnectUntrusted(&hLsa);
        if (status != STATUS_SUCCESS) return HRESULT_FROM_NT(status);

        LSA_STRING lsaName;
        lsaName.Buffer        = const_cast<PCHAR>(packageName);
        lsaName.Length        = static_cast<USHORT>(strlen(packageName));
        lsaName.MaximumLength = lsaName.Length;

        status = LsaLookupAuthenticationPackage(hLsa, &lsaName, pulId);
        LsaDeregisterLogonProcess(hLsa);
        return HRESULT_FROM_NT(status);
    }

    void WipeWide(std::wstring& s)
    {
        if (!s.empty()) SecureZeroMemory(s.data(), s.size() * sizeof(WCHAR));
        s.clear();
    }

    // Copia de la clave protegida con CredProtect, como hace el ejemplo de Microsoft.
    // Una clave ya protegida o vacia se copia tal cual
    HRESULT ProtectPassword(const std::wstring& password, std::wstring& out)
    {
        out.clear();
        if (password.empty()) return S_OK;

        std::wstring copy = password;   // CredProtectW pide un buffer escribible
        CRED_PROTECTION_TYPE type;
        if (CredIsProtectedW(copy.data(), &type) && type != CredUnprotected)
        {
            out = std::move(copy);
            return S_OK;
        }

        DWORD cch = 0;
        DWORD cchIn = static_cast<DWORD>(copy.size() + 1);
        if (!CredProtectW(FALSE, copy.data(), cchIn, nullptr, &cch, nullptr))
        {
            DWORD err = GetLastError();
            if (err != ERROR_INSUFFICIENT_BUFFER || cch == 0)
            {
                WipeWide(copy);
                return HRESULT_FROM_WIN32(err);
            }
        }

        std::wstring protectedPwd(cch, L'\0');
        if (!CredProtectW(FALSE, copy.data(), cchIn, protectedPwd.data(), &cch, nullptr))
        {
            DWORD err = GetLastError();
            WipeWide(copy);
            WipeWide(protectedPwd);
            return HRESULT_FROM_WIN32(err);
        }
        WipeWide(copy);

        // El resultado termina en nulo; se recorta a su longitud real
        protectedPwd.resize(wcsnlen(protectedPwd.c_str(), protectedPwd.size()));
        out = std::move(protectedPwd);
        return S_OK;
    }

    // Copia una cadena tras la estructura y deja en el UNICODE_STRING el desplazamiento relativo
    // al inicio del buffer, que es lo que espera una serializacion
    void PackString(UNICODE_STRING& target, const std::wstring& value, PBYTE base, PBYTE& cursor)
    {
        DWORD cb = static_cast<DWORD>(value.size() * sizeof(WCHAR));
        target.Length        = static_cast<USHORT>(cb);
        target.MaximumLength = static_cast<USHORT>(cb);
        target.Buffer        = cb > 0 ? reinterpret_cast<PWSTR>(cursor - base) : nullptr;
        if (cb > 0)
        {
            memcpy(cursor, value.c_str(), cb);
            cursor += cb;
        }
    }

    HRESULT PackMsv(const LogonName& name, const std::wstring& password,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
    {
        DWORD cbTotal = sizeof(MSV1_0_INTERACTIVE_LOGON)
            + static_cast<DWORD>((name.domain.size() + name.user.size() + password.size()) * sizeof(WCHAR));

        auto* pLogon = reinterpret_cast<MSV1_0_INTERACTIVE_LOGON*>(CoTaskMemAlloc(cbTotal));
        if (!pLogon) return E_OUTOFMEMORY;
        ZeroMemory(pLogon, cbTotal);

        pLogon->MessageType = MsV1_0InteractiveLogon;
        PBYTE base = reinterpret_cast<PBYTE>(pLogon);
        PBYTE cursor = reinterpret_cast<PBYTE>(pLogon + 1);
        PackString(pLogon->LogonDomainName, name.domain, base, cursor);
        PackString(pLogon->UserName, name.user, base, cursor);
        PackString(pLogon->Password, password, base, cursor);

        ULONG ulPkg;
        HRESULT hr = GetAuthPackageId(MSV1_0_PACKAGE_NAME, &ulPkg);
        if (FAILED(hr))
        {
            SecureZeroMemory(pLogon, cbTotal);
            CoTaskMemFree(pLogon);
            return hr;
        }

        pcpcs->ulAuthenticationPackage = ulPkg;
        pcpcs->cbSerialization         = cbTotal;
        pcpcs->rgbSerialization        = reinterpret_cast<BYTE*>(pLogon);
        pcpcs->clsidCredentialProvider = CLSID_BleUnlockCredentialProvider;
        return S_OK;
    }

    // Igual que KerbInteractiveUnlockLogonPack del ejemplo de Microsoft: KERB_INTERACTIVE_UNLOCK_LOGON
    // con el paquete Negotiate y la clave protegida con CredProtect
    HRESULT PackNegotiate(const LogonName& name, const std::wstring& password,
        CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
    {
        std::wstring protectedPwd;
        HRESULT hr = ProtectPassword(password, protectedPwd);
        if (FAILED(hr)) return hr;

        DWORD cbTotal = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON)
            + static_cast<DWORD>((name.domain.size() + name.user.size() + protectedPwd.size()) * sizeof(WCHAR));

        auto* pLogon = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(CoTaskMemAlloc(cbTotal));
        if (!pLogon)
        {
            WipeWide(protectedPwd);
            return E_OUTOFMEMORY;
        }
        ZeroMemory(pLogon, cbTotal);

        pLogon->Logon.MessageType = cpus == CPUS_UNLOCK_WORKSTATION
            ? KerbWorkstationUnlockLogon
            : KerbInteractiveLogon;

        PBYTE base = reinterpret_cast<PBYTE>(pLogon);
        PBYTE cursor = reinterpret_cast<PBYTE>(pLogon + 1);
        PackString(pLogon->Logon.LogonDomainName, name.domain, base, cursor);
        PackString(pLogon->Logon.UserName, name.user, base, cursor);
        PackString(pLogon->Logon.Password, protectedPwd, base, cursor);
        WipeWide(protectedPwd);

        ULONG ulPkg;
        hr = GetAuthPackageId(NEGOTIATE_PACKAGE, &ulPkg);
        if (FAILED(hr))
        {
            SecureZeroMemory(pLogon, cbTotal);
            CoTaskMemFree(pLogon);
            return hr;
        }

        pcpcs->ulAuthenticationPackage = ulPkg;
        pcpcs->cbSerialization         = cbTotal;
        pcpcs->rgbSerialization        = reinterpret_cast<BYTE*>(pLogon);
        pcpcs->clsidCredentialProvider = CLSID_BleUnlockCredentialProvider;
        return S_OK;
    }
}

HRESULT PackCredentialSerialization(
    const std::wstring& username,
    const std::wstring& password,
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    LogonName name = SplitName(username);
    HRESULT hr = name.local
        ? PackMsv(name, password, pcpcs)
        : PackNegotiate(name, password, cpus, pcpcs);

    // Nunca se registra la clave, solo su longitud
    CpLog(L"Pack: path=%s cpus=%d domain='%s' user='%s' pwdLen=%u hr=0x%08X",
        name.local ? L"MSV1_0" : L"Negotiate", static_cast<int>(cpus),
        name.domain.c_str(), name.user.c_str(), static_cast<unsigned>(password.size()),
        static_cast<unsigned>(hr));
    return hr;
}
