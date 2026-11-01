#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlwapi.h>
#include <new>
#include "guids.h"
#include "ClassFactory.h"

#pragma comment(lib, "shlwapi.lib")

HINSTANCE g_hInst = nullptr;
LONG      g_cDllRef = 0;

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID)
{
    if (dwReason == DLL_PROCESS_ATTACH)
    {
        g_hInst = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    *ppv = nullptr;
    if (rclsid != CLSID_BleUnlockCredentialProvider) return CLASS_E_CLASSNOTAVAILABLE;

    auto* pFactory = new (std::nothrow) CClassFactory();
    if (!pFactory) return E_OUTOFMEMORY;

    HRESULT hr = pFactory->QueryInterface(riid, ppv);
    pFactory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return (g_cDllRef > 0) ? S_FALSE : S_OK;
}

static const WCHAR s_szCPKeyFmt[] =
    L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\"
    L"{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}";
static const WCHAR s_szCLSIDKeyFmt[] =
    L"SOFTWARE\\Classes\\CLSID\\{6D9E3F7A-2C8B-4E5D-A1F0-3B7C9D2E6F4A}";

STDAPI DllRegisterServer()
{
    WCHAR szPath[MAX_PATH];
    if (!GetModuleFileNameW(g_hInst, szPath, ARRAYSIZE(szPath)))
        return HRESULT_FROM_WIN32(GetLastError());

    HKEY hKey;
    LONG lr = RegCreateKeyExW(HKEY_LOCAL_MACHINE, s_szCLSIDKeyFmt, 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (lr != ERROR_SUCCESS) return HRESULT_FROM_WIN32(lr);

    const WCHAR szFriendly[] = L"BleUnlock Credential Provider";
    RegSetValueExW(hKey, nullptr, 0, REG_SZ,
        (const BYTE*)szFriendly, (DWORD)(wcslen(szFriendly) + 1) * sizeof(WCHAR));

    HKEY hInprocKey;
    RegCreateKeyExW(hKey, L"InprocServer32", 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hInprocKey, nullptr);
    RegSetValueExW(hInprocKey, nullptr, 0, REG_SZ,
        (const BYTE*)szPath, (DWORD)(wcslen(szPath) + 1) * sizeof(WCHAR));
    const WCHAR szApartment[] = L"Apartment";
    RegSetValueExW(hInprocKey, L"ThreadingModel", 0, REG_SZ,
        (const BYTE*)szApartment, (DWORD)(wcslen(szApartment) + 1) * sizeof(WCHAR));
    RegCloseKey(hInprocKey);
    RegCloseKey(hKey);

    lr = RegCreateKeyExW(HKEY_LOCAL_MACHINE, s_szCPKeyFmt, 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (lr != ERROR_SUCCESS) return HRESULT_FROM_WIN32(lr);
    RegSetValueExW(hKey, nullptr, 0, REG_SZ,
        (const BYTE*)szFriendly, (DWORD)(wcslen(szFriendly) + 1) * sizeof(WCHAR));
    RegCloseKey(hKey);

    return S_OK;
}

STDAPI DllUnregisterServer()
{
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, s_szCPKeyFmt);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, s_szCLSIDKeyFmt);
    return S_OK;
}
