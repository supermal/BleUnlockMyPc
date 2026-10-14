#include "PipeClient.h"
#include <string>
#include <vector>

static const WCHAR PIPE_NAME[] = L"\\\\.\\pipe\\AutoUnlock";

// Cuanto esperamos el NTSTATUS antes de rendirnos: ReportResult no esta garantizado
static const DWORD RESULT_TIMEOUT_MS = 25000;

CPipeClient::CPipeClient(CredReceivedCallback callback)
    : _hThread(nullptr)
    , _callback(std::move(callback))
    , _pendingStatus(0)
{
    _hStopEvent   = CreateEventW(nullptr, TRUE,  FALSE, nullptr);
    _hResultEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
}

CPipeClient::~CPipeClient()
{
    Stop();
    if (_hStopEvent)   CloseHandle(_hStopEvent);
    if (_hResultEvent) CloseHandle(_hResultEvent);
}

void CPipeClient::Start()
{
    ResetEvent(_hStopEvent);
    _hThread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
}

void CPipeClient::Stop()
{
    if (_hStopEvent) SetEvent(_hStopEvent);
    if (_hThread)
    {
        WaitForSingleObject(_hThread, 5000);
        CloseHandle(_hThread);
        _hThread = nullptr;
    }
}

void CPipeClient::ReportResult(NTSTATUS status)
{
    InterlockedExchange(&_pendingStatus, static_cast<LONG>(status));
    if (_hResultEvent) SetEvent(_hResultEvent);
}

DWORD WINAPI CPipeClient::ThreadProc(LPVOID lpParam)
{
    reinterpret_cast<CPipeClient*>(lpParam)->Run();
    return 0;
}

void CPipeClient::Run()
{
    while (WaitForSingleObject(_hStopEvent, 0) != WAIT_OBJECT_0)
    {
        HANDLE hPipe = CreateFileW(
            PIPE_NAME, GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, 0, nullptr);

        if (hPipe == INVALID_HANDLE_VALUE)
        {
            WaitForSingleObject(_hStopEvent, 1000);
            continue;
        }

        DWORD dwMode = PIPE_READMODE_BYTE;
        SetNamedPipeHandleState(hPipe, &dwMode, nullptr, nullptr);

        // La conexion se mantiene viva hasta que falle: el servicio decide cuando enviar
        HandleConnection(hPipe);
        CloseHandle(hPipe);

        WaitForSingleObject(_hStopEvent, 500);
    }
}

bool CPipeClient::WriteLine(HANDLE hPipe, const std::string& text)
{
    DWORD written = 0;
    return WriteFile(hPipe, text.c_str(), static_cast<DWORD>(text.size()), &written, nullptr) != FALSE;
}

// Borra el contenido completo de una cadena (incluida la capacidad sobrante)
static void WipeString(std::string& s)
{
    if (!s.empty()) SecureZeroMemory(s.data(), s.size());
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

        if (!ReadFile(hPipe, chunk, sizeof(chunk), &bytesRead, nullptr) || bytesRead == 0)
        {
            SecureZeroMemory(chunk, sizeof(chunk));
            break;   // el servicio cerro o fallo el canal: se reconectara
        }

        buffer.append(chunk, bytesRead);
        SecureZeroMemory(chunk, sizeof(chunk));

        // El canal es de bytes: un mensaje CREDS son tres lineas completas
        for (;;)
        {
            size_t n1 = buffer.find('\n');
            if (n1 == std::string::npos) break;

            if (buffer.compare(0, n1, "CREDS") != 0)
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

            auto toWide = [](const std::string& s) -> std::wstring {
                if (s.empty()) return {};
                int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
                std::wstring w(len - 1, L'\0');
                MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), len);
                return w;
            };

            std::wstring username = toWide(usernameUtf8);
            std::wstring password = toWide(passwordUtf8);

            WriteLine(hPipe, "ACK\n");

            ResetEvent(_hResultEvent);
            _callback(username, password);

            // Las copias locales se borran tras cada mensaje (el buffer se reutiliza)
            if (!password.empty()) SecureZeroMemory(password.data(), password.size() * sizeof(wchar_t));
            if (!username.empty()) SecureZeroMemory(username.data(), username.size() * sizeof(wchar_t));
            WipeString(passwordUtf8);
            WipeString(usernameUtf8);

            HANDLE waits[2] = { _hResultEvent, _hStopEvent };
            DWORD waited = WaitForMultipleObjects(2, waits, FALSE, RESULT_TIMEOUT_MS);

            if (waited == WAIT_OBJECT_0)
            {
                char line[64] = {};
                NTSTATUS status = static_cast<NTSTATUS>(InterlockedCompareExchange(&_pendingStatus, 0, 0));
                if (status == 0)
                    WriteLine(hPipe, "RESULT OK\n");
                else
                {
                    sprintf_s(line, "RESULT FAIL %08X\n", static_cast<unsigned>(status));
                    WriteLine(hPipe, line);
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
