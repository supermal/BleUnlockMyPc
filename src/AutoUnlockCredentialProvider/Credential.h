#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <functional>
#include <string>

class CAutoUnlockCredential : public ICredentialProviderCredential2
{
public:
    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogonWithDefault) override;
    IFACEMETHODIMP SetDeselected() override;
    IFACEMETHODIMP GetFieldState(DWORD dwFieldID,
        CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override;
    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, PWSTR* ppwsz) override;
    IFACEMETHODIMP GetBitmapValue(DWORD dwFieldID, HBITMAP* phbmp) override;
    IFACEMETHODIMP GetCheckboxValue(DWORD dwFieldID, BOOL* pbChecked, PWSTR* ppwszLabel) override;
    IFACEMETHODIMP GetComboBoxValueCount(DWORD dwFieldID, DWORD* pdwItems, DWORD* pdwSelectedItem) override;
    IFACEMETHODIMP GetComboBoxValueAt(DWORD dwFieldID, DWORD dwItem, PWSTR* ppwszItem) override;
    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo) override;
    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, PCWSTR pwz) override;
    IFACEMETHODIMP SetCheckboxValue(DWORD dwFieldID, BOOL bChecked) override;
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD dwFieldID, DWORD dwSelectedItem) override;
    IFACEMETHODIMP CommandLinkClicked(DWORD dwFieldID) override;
    IFACEMETHODIMP GetSerialization(
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
        PWSTR* ppwszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;
    IFACEMETHODIMP ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
        PWSTR* ppwszOptionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;

    // ICredentialProviderCredential2
    IFACEMETHODIMP GetUserSid(PWSTR* ppszSid) override;

    CAutoUnlockCredential();
    virtual ~CAutoUnlockCredential();

    void SetCredentials(const std::wstring& username, const std::wstring& password);
    bool HasCredentials() const { return _hasCredentials; }

    using AuthResultCallback = std::function<void(NTSTATUS)>;
    void SetAuthResultCallback(AuthResultCallback cb);

private:
    LONG        _cRef;
    std::wstring _username;
    std::wstring _password;
    bool        _hasCredentials;
    ICredentialProviderCredentialEvents* _pcpce;
    AuthResultCallback _authResultCb;   // protegido por _cbLock
    CRITICAL_SECTION   _cbLock;
};
