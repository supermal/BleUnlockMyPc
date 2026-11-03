#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdarg>
#include <cwchar>

// Registro de depuracion del proveedor de credenciales, visible con DebugView («Capture Global
// Win32»). Desactivado salvo que exista HKLM\SOFTWARE\BleUnlock  CpDebugLog (DWORD) = 1; se lee
// una sola vez por proceso. NUNCA se pasa aqui una clave, solo longitudes
inline bool CpLogEnabled()
{
    static const bool enabled = []
    {
        DWORD value = 0;
        DWORD size = sizeof(value);
        return RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\BleUnlock", L"CpDebugLog",
                            RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS && value != 0;
    }();
    return enabled;
}

inline void CpLog(const wchar_t* format, ...)
{
    if (!CpLogEnabled()) return;

    SYSTEMTIME now;
    GetLocalTime(&now);
    wchar_t line[768];
    int prefix = swprintf_s(line, L"[BLECP] %02u:%02u:%02u.%03u ",
        now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
    if (prefix < 0) return;

    va_list args;
    va_start(args, format);
    _vsnwprintf_s(line + prefix, ARRAYSIZE(line) - prefix, _TRUNCATE, format, args);
    va_end(args);

    wcscat_s(line, L"\n");
    OutputDebugStringW(line);
}
