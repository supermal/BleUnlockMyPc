#include "CredentialProvider.h"
#include "guids.h"
#include <new>

extern LONG g_cDllRef;

CAutoUnlockCredentialProvider::CAutoUnlockCredentialProvider()
    : _cRef(1)
    , _pcpe(nullptr)
    , _upAdviseContext(0)
    , _pCredential(nullptr)
    , _pPipeClient(nullptr)
{
    InterlockedIncrement(&g_cDllRef);
    InitializeCriticalSection(&_cs);

    _pCredential = new (std::nothrow) CAutoUnlockCredential();

    if (_pCredential)
    {
        _pCredential->SetAuthResultCallback([this](NTSTATUS status)
        {
            if (_pPipeClient) _pPipeClient->ReportResult(status);
        });
    }

    _pPipeClient = new (std::nothrow) CPipeClient(
        [this](const std::wstring& u, const std::wstring& p)
        {
            OnCredentialsReceived(u, p);
        });

    if (_pPipeClient) _pPipeClient->Start();
}

CAutoUnlockCredentialProvider::~CAutoUnlockCredentialProvider()
{
    // LogonUI puede conservar la credencial tras destruir el proveedor: se corta el
    // callback que apunta a este objeto antes de liberar nada
    if (_pCredential) _pCredential->SetAuthResultCallback(nullptr);

    bool leakedClient = false;
    if (_pPipeClient)
    {
        // Stop() solo devuelve true con el hilo ya terminado; si no, se filtra el objeto
        // a proposito en lugar de liberarlo con el hilo vivo
        if (_pPipeClient->Stop()) delete _pPipeClient;
        else leakedClient = true;
        _pPipeClient = nullptr;
    }
    if (_pCredential) _pCredential->Release();
    if (_pcpe)        _pcpe->Release();
    DeleteCriticalSection(&_cs);

    // ASIMETRIA INTENCIONADA: si se filtro el cliente, su hilo sigue ejecutando codigo de esta
    // DLL, asi que NO se decrementa g_cDllRef y el modulo queda fijado hasta que acabe el
    // proceso. No "arreglar": descargar la DLL con el hilo vivo tumbaria winlogon
    if (!leakedClient) InterlockedDecrement(&g_cDllRef);
}

ULONG CAutoUnlockCredentialProvider::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

ULONG CAutoUnlockCredentialProvider::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CAutoUnlockCredentialProvider::QueryInterface(REFIID riid, void** ppv)
{
    if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
    {
        *ppv = static_cast<ICredentialProvider*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

HRESULT CAutoUnlockCredentialProvider::SetUsageScenario(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD)
{
    if (cpus == CPUS_UNLOCK_WORKSTATION || cpus == CPUS_LOGON)
        return S_OK;
    return E_NOTIMPL;
}

HRESULT CAutoUnlockCredentialProvider::SetSerialization(
    const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*)
{
    return E_NOTIMPL;
}

HRESULT CAutoUnlockCredentialProvider::Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext)
{
    if (_pcpe) _pcpe->Release();
    _pcpe = pcpe;
    if (_pcpe) _pcpe->AddRef();
    _upAdviseContext = upAdviseContext;
    return S_OK;
}

HRESULT CAutoUnlockCredentialProvider::UnAdvise()
{
    if (_pcpe) { _pcpe->Release(); _pcpe = nullptr; }
    return S_OK;
}

HRESULT CAutoUnlockCredentialProvider::GetFieldDescriptorCount(DWORD* pdwCount)
{
    *pdwCount = 0;
    return S_OK;
}

HRESULT CAutoUnlockCredentialProvider::GetFieldDescriptorAt(DWORD,
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd)
{
    *ppcpfd = nullptr;
    return E_INVALIDARG;
}

HRESULT CAutoUnlockCredentialProvider::GetCredentialCount(
    DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault)
{
    EnterCriticalSection(&_cs);
    bool ready = _pCredential && _pCredential->HasCredentials();
    LeaveCriticalSection(&_cs);

    if (ready)
    {
        *pdwCount               = 1;
        *pdwDefault             = 0;
        *pbAutoLogonWithDefault = TRUE;
    }
    else
    {
        *pdwCount               = 0;
        *pdwDefault             = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *pbAutoLogonWithDefault = FALSE;
    }
    return S_OK;
}

HRESULT CAutoUnlockCredentialProvider::GetCredentialAt(DWORD dwIndex,
    ICredentialProviderCredential** ppcpc)
{
    if (dwIndex != 0 || !_pCredential) return E_INVALIDARG;
    return _pCredential->QueryInterface(IID_ICredentialProviderCredential,
        reinterpret_cast<void**>(ppcpc));
}

void CAutoUnlockCredentialProvider::OnCredentialsReceived(
    const std::wstring& username, const std::wstring& password)
{
    ICredentialProviderEvents* pcpe = nullptr;
    UINT_PTR ctx = 0;

    EnterCriticalSection(&_cs);
    if (_pCredential)
    {
        _pCredential->SetCredentials(username, password);
        if (_pcpe) { pcpe = _pcpe; pcpe->AddRef(); ctx = _upAdviseContext; }
    }
    LeaveCriticalSection(&_cs);

    if (pcpe) { pcpe->CredentialsChanged(ctx); pcpe->Release(); }
}
