#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <string>

// Empaqueta usuario y clave para LogonUI. El servicio manda el nombre ya resuelto como
// «DOMINIO\usuario» (cuenta local, de dominio o «MicrosoftAccount\correo»); un UPN suelto
// tambien se acepta. cpus decide el tipo de mensaje (inicio de sesion o desbloqueo)
HRESULT PackCredentialSerialization(
    const std::wstring& username,
    const std::wstring& password,
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs);
