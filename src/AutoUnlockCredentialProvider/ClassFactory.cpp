#include "ClassFactory.h"
#include "CredentialProvider.h"
#include "guids.h"
#include <new>

extern LONG g_cDllRef;

CClassFactory::CClassFactory() : _cRef(1) {}

ULONG CClassFactory::AddRef()  { return InterlockedIncrement(&_cRef); }
ULONG CClassFactory::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0) delete this;
    return cRef;
}

HRESULT CClassFactory::QueryInterface(REFIID riid, void** ppv)
{
    if (riid == IID_IUnknown || riid == IID_IClassFactory)
    {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

HRESULT CClassFactory::CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv)
{
    *ppv = nullptr;
    if (pUnkOuter) return CLASS_E_NOAGGREGATION;

    auto* pProvider = new (std::nothrow) CAutoUnlockCredentialProvider();
    if (!pProvider) return E_OUTOFMEMORY;

    HRESULT hr = pProvider->QueryInterface(riid, ppv);
    pProvider->Release();
    return hr;
}

HRESULT CClassFactory::LockServer(BOOL fLock)
{
    if (fLock) InterlockedIncrement(&g_cDllRef);
    else        InterlockedDecrement(&g_cDllRef);
    return S_OK;
}
