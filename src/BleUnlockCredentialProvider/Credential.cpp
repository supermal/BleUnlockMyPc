#include "Credential.h"
#include "Serialization.h"
#include <wincred.h>

#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0x00000000L)
#endif

CBleUnlockCredential::CBleUnlockCredential()
    : _cRef(1)
    , _hasCredentials(false)
    , _pcpce(nullptr)
{
    InitializeCriticalSection(&_cbLock);
}

CBleUnlockCredential::~CBleUnlockCredential()
{
    if (!_password.empty())
        SecureZeroMemory(_password.data(), _password.size() * sizeof(WCHAR));
    if (_pcpce) _pcpce->Release();
    DeleteCriticalSection(&_cbLock);
}

ULONG CBleUnlockCredential::AddRef()  { return InterlockedIncrement(&_cRef); }
ULONG CBleUnlockCredential::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CBleUnlockCredential::QueryInterface(REFIID riid, void** ppv)
{
    if (riid == IID_IUnknown ||
        riid == IID_ICredentialProviderCredential ||
        riid == IID_ICredentialProviderCredential2)
    {
        *ppv = static_cast<ICredentialProviderCredential2*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

void CBleUnlockCredential::SetAuthResultCallback(AuthResultCallback cb)
{
    EnterCriticalSection(&_cbLock);
    _authResultCb = std::move(cb);
    LeaveCriticalSection(&_cbLock);
}

void CBleUnlockCredential::SetCredentials(const std::wstring& username, const std::wstring& password)
{
    // Si quedaba una contrasena sin consumir, se borra antes de sustituirla
    if (!_password.empty()) SecureZeroMemory(_password.data(), _password.size() * sizeof(WCHAR));
    _username = username;
    _password = password;
    _hasCredentials = true;
}

HRESULT CBleUnlockCredential::Advise(ICredentialProviderCredentialEvents* pcpce)
{
    if (_pcpce) _pcpce->Release();
    _pcpce = pcpce;
    if (_pcpce) _pcpce->AddRef();
    return S_OK;
}

HRESULT CBleUnlockCredential::UnAdvise()
{
    if (_pcpce) { _pcpce->Release(); _pcpce = nullptr; }
    return S_OK;
}

HRESULT CBleUnlockCredential::SetSelected(BOOL* pbAutoLogonWithDefault)
{
    *pbAutoLogonWithDefault = _hasCredentials ? TRUE : FALSE;
    return S_OK;
}

HRESULT CBleUnlockCredential::SetDeselected() { return S_OK; }

HRESULT CBleUnlockCredential::GetFieldState(DWORD,
    CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis)
{
    *pcpfs  = CPFS_HIDDEN;
    *pcpfis = CPFIS_NONE;
    return S_OK;
}

HRESULT CBleUnlockCredential::GetStringValue(DWORD, PWSTR* ppwsz)
{
    *ppwsz = nullptr;
    return S_OK;
}
HRESULT CBleUnlockCredential::GetBitmapValue(DWORD, HBITMAP* phbmp)          { *phbmp = nullptr; return E_NOTIMPL; }
HRESULT CBleUnlockCredential::GetCheckboxValue(DWORD, BOOL*, PWSTR*)          { return E_NOTIMPL; }
HRESULT CBleUnlockCredential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*)    { return E_NOTIMPL; }
HRESULT CBleUnlockCredential::GetComboBoxValueAt(DWORD, DWORD, PWSTR*)        { return E_NOTIMPL; }
HRESULT CBleUnlockCredential::GetSubmitButtonValue(DWORD, DWORD*)             { return E_NOTIMPL; }
HRESULT CBleUnlockCredential::SetStringValue(DWORD, PCWSTR)                   { return S_OK; }
HRESULT CBleUnlockCredential::SetCheckboxValue(DWORD, BOOL)                   { return S_OK; }
HRESULT CBleUnlockCredential::SetComboBoxSelectedValue(DWORD, DWORD)          { return S_OK; }
HRESULT CBleUnlockCredential::CommandLinkClicked(DWORD)                       { return S_OK; }
HRESULT CBleUnlockCredential::GetUserSid(PWSTR* ppszSid)                      { *ppszSid = nullptr; return S_OK; }

HRESULT CBleUnlockCredential::GetSerialization(
    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
    PWSTR* ppwszOptionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon)
{
    *ppwszOptionalStatusText = nullptr;
    *pcpsiOptionalStatusIcon = CPSI_NONE;

    if (!_hasCredentials)
    {
        *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        return S_OK;
    }

    HRESULT hr = PackCredentialSerialization(_username, _password, pcpcs);
    if (SUCCEEDED(hr))
    {
        *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;
        SecureZeroMemory(_password.data(), _password.size() * sizeof(WCHAR));
        _password.clear();
        _hasCredentials = false;
    }
    else
    {
        *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
    }

    return hr;
}

HRESULT CBleUnlockCredential::ReportResult(NTSTATUS ntsStatus, NTSTATUS, PWSTR* ppwsz,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsi)
{
    *ppwsz = nullptr;
    *pcpsi = CPSI_NONE;

    // El servicio decide que hacer con el resultado; aqui solo se informa
    // Bajo candado: el proveedor puede anular el callback mientras LogonUI conserva la credencial
    EnterCriticalSection(&_cbLock);
    if (_authResultCb) _authResultCb(ntsStatus);
    LeaveCriticalSection(&_cbLock);

    return S_OK;
}
