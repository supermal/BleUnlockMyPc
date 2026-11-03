#include "CredentialProvider.h"
#include "guids.h"
#include "CpLog.h"
#include <new>

extern LONG g_cDllRef;

CBleUnlockCredentialProvider::CBleUnlockCredentialProvider()
    : _cRef(1)
    , _pcpe(nullptr)
    , _upAdviseContext(0)
    , _pCredential(nullptr)
    , _pPipeClient(nullptr)
{
    InterlockedIncrement(&g_cDllRef);
    InitializeCriticalSection(&_cs);

    _pCredential = new (std::nothrow) CBleUnlockCredential();

    if (_pCredential)
    {
        _pCredential->SetAuthResultCallback([this](NTSTATUS status, NTSTATUS substatus)
        {
            if (_pPipeClient) _pPipeClient->ReportResult(status, substatus);
        });
    }

    _pPipeClient = new (std::nothrow) CPipeClient(
        [this](const std::wstring& u, const std::wstring& p)
        {
            OnCredentialsReceived(u, p);
        });

    if (_pPipeClient) _pPipeClient->Start();
}

CBleUnlockCredentialProvider::~CBleUnlockCredentialProvider()
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

ULONG CBleUnlockCredentialProvider::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

ULONG CBleUnlockCredentialProvider::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CBleUnlockCredentialProvider::QueryInterface(REFIID riid, void** ppv)
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

HRESULT CBleUnlockCredentialProvider::SetUsageScenario(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD)
{
    CpLog(L"SetUsageScenario: cpus=%d", static_cast<int>(cpus));
    if (cpus == CPUS_UNLOCK_WORKSTATION || cpus == CPUS_LOGON)
    {
        // El tipo de mensaje depende del escenario: KerbWorkstationUnlockLogon solo vale al desbloquear
        if (_pCredential) _pCredential->SetUsageScenario(cpus);
        return S_OK;
    }
    return E_NOTIMPL;
}

HRESULT CBleUnlockCredentialProvider::SetSerialization(
    const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*)
{
    return E_NOTIMPL;
}

// Bajo _cs, igual que OnCredentialsReceived (hilo del canal), que lee _pcpe y el contexto
HRESULT CBleUnlockCredentialProvider::Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext)
{
    ICredentialProviderEvents* previous = nullptr;
    if (pcpe) pcpe->AddRef();

    EnterCriticalSection(&_cs);
    previous = _pcpe;
    _pcpe = pcpe;
    _upAdviseContext = upAdviseContext;
    LeaveCriticalSection(&_cs);

    // Release fuera del candado: puede reentrar en LogonUI
    if (previous) previous->Release();
    CpLog(L"Advise");
    return S_OK;
}

HRESULT CBleUnlockCredentialProvider::UnAdvise()
{
    EnterCriticalSection(&_cs);
    ICredentialProviderEvents* previous = _pcpe;
    _pcpe = nullptr;
    _upAdviseContext = 0;
    LeaveCriticalSection(&_cs);

    if (previous) previous->Release();
    CpLog(L"UnAdvise");
    return S_OK;
}

HRESULT CBleUnlockCredentialProvider::GetFieldDescriptorCount(DWORD* pdwCount)
{
    *pdwCount = 0;
    return S_OK;
}

HRESULT CBleUnlockCredentialProvider::GetFieldDescriptorAt(DWORD,
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd)
{
    *ppcpfd = nullptr;
    return E_INVALIDARG;
}

HRESULT CBleUnlockCredentialProvider::GetCredentialCount(
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
    CpLog(L"GetCredentialCount: %u", static_cast<unsigned>(*pdwCount));
    return S_OK;
}

HRESULT CBleUnlockCredentialProvider::GetCredentialAt(DWORD dwIndex,
    ICredentialProviderCredential** ppcpc)
{
    if (dwIndex != 0 || !_pCredential) return E_INVALIDARG;
    return _pCredential->QueryInterface(IID_ICredentialProviderCredential,
        reinterpret_cast<void**>(ppcpc));
}

void CBleUnlockCredentialProvider::OnCredentialsReceived(
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
