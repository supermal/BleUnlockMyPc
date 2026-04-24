#include "Serialization.h"
#include "guids.h"
#include <ntsecapi.h>
#include <subauth.h>
#include <string>

#pragma comment(lib, "secur32.lib")

static bool IsDomainAccount(const std::wstring& username)
{
    auto pos = username.find(L'\\');
    if (pos == std::wstring::npos) return false;
    std::wstring prefix = username.substr(0, pos);
    return !prefix.empty() && prefix != L".";
}

static HRESULT GetAuthPackageId(LPCSTR packageName, ULONG* pulId)
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

static HRESULT PackMsv(const std::wstring& username, const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    std::wstring domain, user;
    auto pos = username.find(L'\\');
    if (pos != std::wstring::npos)
    {
        domain = username.substr(0, pos);
        user   = username.substr(pos + 1);
        if (domain == L".")
        {
            WCHAR computerName[MAX_COMPUTERNAME_LENGTH + 1];
            DWORD size = ARRAYSIZE(computerName);
            GetComputerNameW(computerName, &size);
            domain = computerName;
        }
    }
    else
    {
        WCHAR computerName[MAX_COMPUTERNAME_LENGTH + 1];
        DWORD size = ARRAYSIZE(computerName);
        GetComputerNameW(computerName, &size);
        domain = computerName;
        user   = username;
    }

    DWORD cbDomain = static_cast<DWORD>(domain.size()   * sizeof(WCHAR));
    DWORD cbUser   = static_cast<DWORD>(user.size()     * sizeof(WCHAR));
    DWORD cbPass   = static_cast<DWORD>(password.size() * sizeof(WCHAR));
    DWORD cbTotal  = sizeof(MSV1_0_INTERACTIVE_LOGON) + cbDomain + cbUser + cbPass;

    auto* pLogon = reinterpret_cast<MSV1_0_INTERACTIVE_LOGON*>(CoTaskMemAlloc(cbTotal));
    if (!pLogon) return E_OUTOFMEMORY;
    ZeroMemory(pLogon, cbTotal);

    pLogon->MessageType = MsV1_0InteractiveLogon;
    PBYTE pb = reinterpret_cast<PBYTE>(pLogon + 1);

    pLogon->LogonDomainName.Length        = static_cast<USHORT>(cbDomain);
    pLogon->LogonDomainName.MaximumLength = static_cast<USHORT>(cbDomain);
    pLogon->LogonDomainName.Buffer        = cbDomain > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbDomain > 0) { memcpy(pb, domain.c_str(), cbDomain); pb += cbDomain; }

    pLogon->UserName.Length        = static_cast<USHORT>(cbUser);
    pLogon->UserName.MaximumLength = static_cast<USHORT>(cbUser);
    pLogon->UserName.Buffer        = reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon));
    memcpy(pb, user.c_str(), cbUser); pb += cbUser;

    pLogon->Password.Length        = static_cast<USHORT>(cbPass);
    pLogon->Password.MaximumLength = static_cast<USHORT>(cbPass);
    pLogon->Password.Buffer        = cbPass > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbPass > 0) memcpy(pb, password.c_str(), cbPass);

    ULONG ulPkg;
    HRESULT hr = GetAuthPackageId(MSV1_0_PACKAGE_NAME, &ulPkg);
    if (FAILED(hr)) { CoTaskMemFree(pLogon); return hr; }

    pcpcs->ulAuthenticationPackage = ulPkg;
    pcpcs->cbSerialization         = cbTotal;
    pcpcs->rgbSerialization        = reinterpret_cast<BYTE*>(pLogon);
    pcpcs->clsidCredentialProvider = CLSID_AutoUnlockCredentialProvider;
    return S_OK;
}

static HRESULT PackKerb(const std::wstring& username, const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    auto pos = username.find(L'\\');
    std::wstring domain = username.substr(0, pos);
    std::wstring user   = username.substr(pos + 1);

    DWORD cbDomain = static_cast<DWORD>(domain.size()   * sizeof(WCHAR));
    DWORD cbUser   = static_cast<DWORD>(user.size()     * sizeof(WCHAR));
    DWORD cbPass   = static_cast<DWORD>(password.size() * sizeof(WCHAR));
    DWORD cbTotal  = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) + cbDomain + cbUser + cbPass;

    auto* pLogon = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(CoTaskMemAlloc(cbTotal));
    if (!pLogon) return E_OUTOFMEMORY;
    ZeroMemory(pLogon, cbTotal);

    pLogon->Logon.MessageType = KerbWorkstationUnlockLogon;
    PBYTE pb = reinterpret_cast<PBYTE>(pLogon + 1);

    pLogon->Logon.LogonDomainName.Length        = static_cast<USHORT>(cbDomain);
    pLogon->Logon.LogonDomainName.MaximumLength = static_cast<USHORT>(cbDomain);
    pLogon->Logon.LogonDomainName.Buffer        = cbDomain > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbDomain > 0) { memcpy(pb, domain.c_str(), cbDomain); pb += cbDomain; }

    pLogon->Logon.UserName.Length        = static_cast<USHORT>(cbUser);
    pLogon->Logon.UserName.MaximumLength = static_cast<USHORT>(cbUser);
    pLogon->Logon.UserName.Buffer        = reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon));
    memcpy(pb, user.c_str(), cbUser); pb += cbUser;

    pLogon->Logon.Password.Length        = static_cast<USHORT>(cbPass);
    pLogon->Logon.Password.MaximumLength = static_cast<USHORT>(cbPass);
    pLogon->Logon.Password.Buffer        = cbPass > 0
        ? reinterpret_cast<PWSTR>(pb - reinterpret_cast<PBYTE>(pLogon)) : nullptr;
    if (cbPass > 0) memcpy(pb, password.c_str(), cbPass);

    ULONG ulPkg;
    HRESULT hr = GetAuthPackageId(MICROSOFT_KERBEROS_NAME_A, &ulPkg);
    if (FAILED(hr)) { CoTaskMemFree(pLogon); return hr; }

    pcpcs->ulAuthenticationPackage = ulPkg;
    pcpcs->cbSerialization         = cbTotal;
    pcpcs->rgbSerialization        = reinterpret_cast<BYTE*>(pLogon);
    pcpcs->clsidCredentialProvider = CLSID_AutoUnlockCredentialProvider;
    return S_OK;
}

HRESULT PackCredentialSerialization(
    const std::wstring& username,
    const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    if (IsDomainAccount(username))
        return PackKerb(username, password, pcpcs);
    else
        return PackMsv(username, password, pcpcs);
}
