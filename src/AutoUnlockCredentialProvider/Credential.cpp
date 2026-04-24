#include "Credential.h"
#include "Serialization.h"
#include <wincred.h>

CAutoUnlockCredential::CAutoUnlockCredential()
    : _cRef(1)
    , _hasCredentials(false)
    , _pcpce(nullptr)
{
}

CAutoUnlockCredential::~CAutoUnlockCredential()
{
    if (!_password.empty())
        SecureZeroMemory(_password.data(), _password.size() * sizeof(WCHAR));
    if (_pcpce) _pcpce->Release();
}

ULONG CAutoUnlockCredential::AddRef()  { return InterlockedIncrement(&_cRef); }
ULONG CAutoUnlockCredential::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CAutoUnlockCredential::QueryInterface(REFIID riid, void** ppv)
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

void CAutoUnlockCredential::SetCredentials(const std::wstring& username, const std::wstring& password)
{
    _username = username;
    _password = password;
    _hasCredentials = true;
}

HRESULT CAutoUnlockCredential::Advise(ICredentialProviderCredentialEvents* pcpce)
{
    if (_pcpce) _pcpce->Release();
    _pcpce = pcpce;
    if (_pcpce) _pcpce->AddRef();
    return S_OK;
}

HRESULT CAutoUnlockCredential::UnAdvise()
{
    if (_pcpce) { _pcpce->Release(); _pcpce = nullptr; }
    return S_OK;
}

HRESULT CAutoUnlockCredential::SetSelected(BOOL* pbAutoLogonWithDefault)
{
    *pbAutoLogonWithDefault = _hasCredentials ? TRUE : FALSE;
    return S_OK;
}

HRESULT CAutoUnlockCredential::SetDeselected() { return S_OK; }

HRESULT CAutoUnlockCredential::GetFieldState(DWORD,
    CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis)
{
    *pcpfs  = CPFS_HIDDEN;
    *pcpfis = CPFIS_NONE;
    return S_OK;
}

HRESULT CAutoUnlockCredential::GetStringValue(DWORD, PWSTR* ppwsz)
{
    *ppwsz = nullptr;
    return S_OK;
}
HRESULT CAutoUnlockCredential::GetBitmapValue(DWORD, HBITMAP* phbmp)          { *phbmp = nullptr; return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetCheckboxValue(DWORD, BOOL*, PWSTR*)          { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*)    { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetComboBoxValueAt(DWORD, DWORD, PWSTR*)        { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::GetSubmitButtonValue(DWORD, DWORD*)             { return E_NOTIMPL; }
HRESULT CAutoUnlockCredential::SetStringValue(DWORD, PCWSTR)                   { return S_OK; }
HRESULT CAutoUnlockCredential::SetCheckboxValue(DWORD, BOOL)                   { return S_OK; }
HRESULT CAutoUnlockCredential::SetComboBoxSelectedValue(DWORD, DWORD)          { return S_OK; }
HRESULT CAutoUnlockCredential::CommandLinkClicked(DWORD)                       { return S_OK; }
HRESULT CAutoUnlockCredential::GetUserSid(PWSTR* ppszSid)                      { *ppszSid = nullptr; return S_OK; }

HRESULT CAutoUnlockCredential::GetSerialization(
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

HRESULT CAutoUnlockCredential::ReportResult(NTSTATUS, NTSTATUS, PWSTR* ppwsz,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsi)
{
    *ppwsz  = nullptr;
    *pcpsi  = CPSI_NONE;
    return S_OK;
}
