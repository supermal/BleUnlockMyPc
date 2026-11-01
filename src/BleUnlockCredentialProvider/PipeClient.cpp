#include "PipeClient.h"
#include <string>

static const WCHAR PIPE_NAME[] = L"\\\\.\\pipe\\BleUnlock";

// Cuanto esperamos el NTSTATUS antes de rendirnos: ReportResult no esta garantizado
static const DWORD RESULT_TIMEOUT_MS = 25000;

// Plazo maximo para que Stop() espere al hilo (solo puede tardar si un callback se bloquea)
static const DWORD STOP_JOIN_TIMEOUT_MS = 30000;

// Plazo de una escritura en el canal
static const DWORD WRITE_TIMEOUT_MS = 5000;

// Tope del buffer de acumulacion: el protocolo cabe de sobra en 8 KiB
static const size_t MAX_BUFFER_BYTES = 8192;

CPipeClient::CPipeClient(CredReceivedCallback callback)
    : _hThread(nullptr)
    , _callback(std::move(callback))
    , _pendingStatus(0)
    , _awaiting(0)
{
    InitializeCriticalSection(&_cbLock);
    _hStopEvent   = CreateEventW(nullptr, TRUE,  FALSE, nullptr);
    _hResultEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    _hIoEvent     = CreateEventW(nullptr, TRUE,  FALSE, nullptr);
}

CPipeClient::~CPipeClient()
{
    // Solo es seguro liberar si el hilo ya termino; si Stop() falla, el propietario no debe llegar aqui
    if (!Stop()) return;
    if (_hStopEvent)   CloseHandle(_hStopEvent);
    if (_hResultEvent) CloseHandle(_hResultEvent);
    if (_hIoEvent)     CloseHandle(_hIoEvent);
    DeleteCriticalSection(&_cbLock);
}

void CPipeClient::Start()
{
    ResetEvent(_hStopEvent);
    _hThread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
}

bool CPipeClient::Stop()
{
    // Un unico plazo cubre candado y join: nunca se espera sin limite en la ruta de logon
    ULONGLONG deadline = GetTickCount64() + STOP_JOIN_TIMEOUT_MS;

    // 1. El hilo ya no podra llamar al propietario. El candado se adquiere con plazo: el hilo
    //    puede estar dentro del callback (CredentialsChanged) esperando al hilo de LogonUI,
    //    que es quien esta aqui
    bool locked = false;
    while (!(locked = TryEnterCriticalSection(&_cbLock) != FALSE))
    {
        if (GetTickCount64() >= deadline) break;
        Sleep(10);
    }

    if (!locked)
    {
        // No se toca _callback (no se pudo bloquear). Solo se senala la parada, que es seguro:
        // el evento pertenece a este objeto, que el llamador va a filtrar. El hilo vera la
        // parada antes de invocar de nuevo el callback
        if (_hStopEvent) SetEvent(_hStopEvent);
        return false;
    }
    _callback = nullptr;
    LeaveCriticalSection(&_cbLock);

    // 2. Despierta al hilo: toda espera (lectura, escritura, resultado, reintento) incluye _hStopEvent
    if (_hStopEvent) SetEvent(_hStopEvent);

    // 3. Join: nada se cierra ni se libera hasta que el hilo haya salido
    if (_hThread)
    {
        ULONGLONG now = GetTickCount64();
        DWORD remaining = (now >= deadline) ? 0 : static_cast<DWORD>(deadline - now);
        if (WaitForSingleObject(_hThread, remaining) != WAIT_OBJECT_0)
            return false;
        CloseHandle(_hThread);
        _hThread = nullptr;
    }
    return true;
}

void CPipeClient::ReportResult(NTSTATUS status)
{
    // Un resultado fuera de un intento en espera (rezagado de uno ya vencido) se descarta
    if (InterlockedCompareExchange(&_awaiting, 1, 1) != 1) return;
    InterlockedExchange(&_pendingStatus, static_cast<LONG>(status));
    if (_hResultEvent) SetEvent(_hResultEvent);
}

DWORD WINAPI CPipeClient::ThreadProc(LPVOID lpParam)
{
    // Ninguna excepcion debe escapar: terminaria el proceso LogonUI
    try { reinterpret_cast<CPipeClient*>(lpParam)->Run(); }
    catch (...) {}
    return 0;
}

// Comprueba que quien creo la tuberia es el servicio (SYSTEM) o, para la herramienta
// de pruebas, un proceso elevado de administrador. No se comprueba la ruta del
// ejecutable ni su firma, ni la DACL de la tuberia, ni se protege contra reutilizacion de PID
bool CPipeClient::VerifyServer(HANDLE hPipe)
{
    ULONG pid = 0;
    if (!GetNamedPipeServerProcessId(hPipe, &pid) || pid == 0) return false;

    HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!hProc) return false;

    HANDLE hToken = nullptr;
    bool ok = false;
    if (OpenProcessToken(hProc, TOKEN_QUERY, &hToken))
    {
        BYTE userBuf[SECURITY_MAX_SID_SIZE + sizeof(TOKEN_USER)] = {};
        DWORD len = 0;
        BYTE sysSid[SECURITY_MAX_SID_SIZE] = {};
        DWORD sysLen = sizeof(sysSid);

        if (GetTokenInformation(hToken, TokenUser, userBuf, sizeof(userBuf), &len) &&
            CreateWellKnownSid(WinLocalSystemSid, nullptr, sysSid, &sysLen) &&
            EqualSid(reinterpret_cast<TOKEN_USER*>(userBuf)->User.Sid, sysSid))
        {
            ok = true;
        }
        else
        {
            // Herramienta de pruebas: token elevado con el grupo Administradores activo
            TOKEN_ELEVATION elev = {};
            if (GetTokenInformation(hToken, TokenElevation, &elev, sizeof(elev), &len) && elev.TokenIsElevated)
            {
                BYTE admSid[SECURITY_MAX_SID_SIZE] = {};
                DWORD admLen = sizeof(admSid);
                DWORD need = 0;
                GetTokenInformation(hToken, TokenGroups, nullptr, 0, &need);
                std::string groups(need, '\0');
                if (need && CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, admSid, &admLen) &&
                    GetTokenInformation(hToken, TokenGroups, groups.data(), need, &need))
                {
                    auto* tg = reinterpret_cast<TOKEN_GROUPS*>(groups.data());
                    for (DWORD i = 0; i < tg->GroupCount; i++)
                    {
                        if ((tg->Groups[i].Attributes & SE_GROUP_ENABLED) && EqualSid(tg->Groups[i].Sid, admSid))
                        {
                            ok = true;
                            break;
                        }
                    }
                }
            }
        }
        CloseHandle(hToken);
    }
    CloseHandle(hProc);
    return ok;
}

void CPipeClient::Run()
{
    while (WaitForSingleObject(_hStopEvent, 0) != WAIT_OBJECT_0)
    {
        HANDLE hPipe = CreateFileW(
            PIPE_NAME, GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);

        if (hPipe == INVALID_HANDLE_VALUE)
        {
            WaitForSingleObject(_hStopEvent, 1000);
            continue;
        }

        if (!VerifyServer(hPipe))
        {
            // No se envia ni se acepta nada de un servidor no verificado
            CloseHandle(hPipe);
            WaitForSingleObject(_hStopEvent, 1000);
            continue;
        }

        DWORD dwMode = PIPE_READMODE_BYTE;
        SetNamedPipeHandleState(hPipe, &dwMode, nullptr, nullptr);

        // La conexion se mantiene viva hasta que falle: el servicio decide cuando enviar
        try { HandleConnection(hPipe); }
        catch (...) {}   // una excepcion solo cuesta la conexion; se reconecta
        InterlockedExchange(&_awaiting, 0);
        CloseHandle(hPipe);

        WaitForSingleObject(_hStopEvent, 500);
    }
}

// Lectura interrumpible: espera {fin de lectura, parada}; en parada cancela la E/S
bool CPipeClient::ReadSome(HANDLE hPipe, char* buf, DWORD len, DWORD* got)
{
    *got = 0;
    OVERLAPPED ov = {};
    ov.hEvent = _hIoEvent;
    ResetEvent(_hIoEvent);

    if (!ReadFile(hPipe, buf, len, nullptr, &ov))
    {
        if (GetLastError() != ERROR_IO_PENDING) return false;

        HANDLE waits[2] = { _hIoEvent, _hStopEvent };
        if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0)
        {
            CancelIoEx(hPipe, &ov);
            DWORD dummy = 0;
            GetOverlappedResult(hPipe, &ov, &dummy, TRUE);   // ov y buf deben seguir vivos hasta aqui
            return false;
        }
    }
    return GetOverlappedResult(hPipe, &ov, got, FALSE) && *got > 0;
}

// Escritura completa con plazo y cancelable por la parada
bool CPipeClient::WriteAll(HANDLE hPipe, const std::string& text)
{
    size_t offset = 0;
    while (offset < text.size())
    {
        OVERLAPPED ov = {};
        ov.hEvent = _hIoEvent;
        ResetEvent(_hIoEvent);

        DWORD chunk = static_cast<DWORD>(text.size() - offset);
        if (!WriteFile(hPipe, text.data() + offset, chunk, nullptr, &ov))
        {
            if (GetLastError() != ERROR_IO_PENDING) return false;

            HANDLE waits[2] = { _hIoEvent, _hStopEvent };
            if (WaitForMultipleObjects(2, waits, FALSE, WRITE_TIMEOUT_MS) != WAIT_OBJECT_0)
            {
                CancelIoEx(hPipe, &ov);
                DWORD dummy = 0;
                GetOverlappedResult(hPipe, &ov, &dummy, TRUE);
                return false;
            }
        }

        DWORD written = 0;
        if (!GetOverlappedResult(hPipe, &ov, &written, FALSE) || written == 0) return false;
        offset += written;
    }
    return true;
}

// Borra el contenido de una cadena
static void WipeString(std::string& s)
{
    if (!s.empty()) SecureZeroMemory(s.data(), s.size());
}

static void WipeWide(std::wstring& s)
{
    if (!s.empty()) SecureZeroMemory(s.data(), s.size() * sizeof(wchar_t));
}

// Quita n bytes del principio del buffer sin dejar copias residuales de la
// contrasena: se borra lo consumido y tambien la cola que erase() deja atras
static void ConsumeAndWipe(std::string& buffer, size_t n)
{
    size_t oldSize = buffer.size();
    SecureZeroMemory(buffer.data(), n);
    buffer.erase(0, n);
    buffer.resize(oldSize, '\0');   // sobrescribe los bytes viejos que quedaron tras el desplazamiento
    buffer.resize(oldSize - n);
}

// UTF-8 a ancho; false si la entrada no es UTF-8 valido (no se lanza nunca)
static bool ToWide(const std::string& s, std::wstring& out)
{
    out.clear();
    if (s.empty()) return true;
    int len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.c_str(), -1, nullptr, 0);
    if (len <= 1) return false;
    out.assign(static_cast<size_t>(len - 1), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.c_str(), -1, out.data(), len) == 0)
    {
        out.clear();
        return false;
    }
    return true;
}

bool CPipeClient::HandleConnection(HANDLE hPipe)
{
    std::string buffer;

    // Garantiza que el buffer de acumulacion se borra en cualquier salida
    struct Wiper
    {
        std::string& s;
        ~Wiper() { WipeString(s); }
    } wiper{ buffer };

    while (WaitForSingleObject(_hStopEvent, 0) != WAIT_OBJECT_0)
    {
        char chunk[512] = {};
        DWORD bytesRead = 0;

        if (!ReadSome(hPipe, chunk, sizeof(chunk), &bytesRead))
        {
            SecureZeroMemory(chunk, sizeof(chunk));
            break;   // parada, el servicio cerro o fallo el canal: se reconectara
        }

        buffer.append(chunk, bytesRead);
        SecureZeroMemory(chunk, sizeof(chunk));

        if (buffer.size() > MAX_BUFFER_BYTES)
            return false;   // emisor que no manda saltos de linea: se corta la conexion

        // El canal es de bytes: un mensaje CREDS son tres lineas completas
        for (;;)
        {
            size_t n1 = buffer.find('\n');
            if (n1 == std::string::npos) break;

            size_t verbLen = n1;
            if (verbLen > 0 && buffer[verbLen - 1] == '\r') verbLen--;   // tolera CRLF en el verbo

            if (buffer.compare(0, verbLen, "CREDS") != 0)
            {
                ConsumeAndWipe(buffer, n1 + 1);   // mensaje desconocido: se descarta
                continue;
            }

            size_t n2 = buffer.find('\n', n1 + 1);
            if (n2 == std::string::npos) break;
            size_t n3 = buffer.find('\n', n2 + 1);
            if (n3 == std::string::npos) break;

            std::string usernameUtf8 = buffer.substr(n1 + 1, n2 - n1 - 1);
            std::string passwordUtf8 = buffer.substr(n2 + 1, n3 - n2 - 1);
            ConsumeAndWipe(buffer, n3 + 1);

            std::wstring username, password;
            bool valid = ToWide(usernameUtf8, username) && ToWide(passwordUtf8, password);

            WipeString(passwordUtf8);
            WipeString(usernameUtf8);

            if (!valid)
            {
                // UTF-8 invalido: se descarta sin ACK; el servicio lo tratara como sin respuesta
                WipeWide(password);
                WipeWide(username);
                continue;
            }

            if (!WriteAll(hPipe, "ACK\n"))
            {
                WipeWide(password);
                WipeWide(username);
                return false;
            }

            // Se arma la espera antes de entregar la credencial para no perder un resultado rapido
            ResetEvent(_hResultEvent);
            InterlockedExchange(&_awaiting, 1);

            EnterCriticalSection(&_cbLock);
            // Si Stop() no pudo anular el callback (plazo vencido), la parada senalada impide
            // volver a invocar un callback cuyo propietario ya pudo haberse liberado
            bool stopping = WaitForSingleObject(_hStopEvent, 0) == WAIT_OBJECT_0;
            if (!stopping && _callback) _callback(username, password);
            LeaveCriticalSection(&_cbLock);
            if (stopping)
            {
                WipeWide(password);
                WipeWide(username);
                return false;
            }

            // Las copias locales se borran tras cada mensaje (el buffer se reutiliza)
            WipeWide(password);
            WipeWide(username);

            HANDLE waits[2] = { _hResultEvent, _hStopEvent };
            DWORD waited = WaitForMultipleObjects(2, waits, FALSE, RESULT_TIMEOUT_MS);
            InterlockedExchange(&_awaiting, 0);

            if (waited == WAIT_OBJECT_0)
            {
                NTSTATUS status = static_cast<NTSTATUS>(InterlockedCompareExchange(&_pendingStatus, 0, 0));
                if (status == 0)
                {
                    if (!WriteAll(hPipe, "RESULT OK\n")) return false;
                }
                else
                {
                    char line[64] = {};
                    sprintf_s(line, "RESULT FAIL %08X\n", static_cast<unsigned>(status));
                    if (!WriteAll(hPipe, line)) return false;
                }
            }
            else if (waited == WAIT_OBJECT_0 + 1)
            {
                return false;   // parada solicitada
            }
            // En caso de timeout no se escribe nada: el servicio tiene su propio plazo
        }
    }

    return true;
}
