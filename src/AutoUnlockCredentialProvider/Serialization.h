#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <string>

HRESULT PackCredentialSerialization(
    const std::wstring& username,
    const std::wstring& password,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs);
