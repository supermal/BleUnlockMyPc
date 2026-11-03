#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>   // trae la definicion de NTSTATUS de forma coherente con el resto del proyecto
#include <string>
#include <functional>

using CredReceivedCallback = std::function<void(const std::wstring&, const std::wstring&)>;

class CPipeClient
{
public:
    explicit CPipeClient(CredReceivedCallback callback);
    ~CPipeClient();

    void Start();

    // Anula el callback y detiene el hilo. Solo devuelve true cuando el hilo ha
    // terminado de verdad; si devuelve false NO se debe liberar el objeto
    bool Stop();

    // La llama la credencial desde ReportResult; despierta al hilo del canal
    void ReportResult(NTSTATUS status, NTSTATUS substatus);

private:
    static DWORD WINAPI ThreadProc(LPVOID lpParam);
    void Run();
    bool HandleConnection(HANDLE hPipe);
    bool VerifyServer(HANDLE hPipe);
    bool ReadSome(HANDLE hPipe, char* buf, DWORD len, DWORD* got);
    bool WriteAll(HANDLE hPipe, const std::string& text);

    HANDLE               _hThread;
    HANDLE               _hStopEvent;
    HANDLE               _hResultEvent;   // senalizado cuando llega el NTSTATUS
    HANDLE               _hIoEvent;       // evento de E/S solapada, solo lo usa el hilo del canal
    CredReceivedCallback _callback;       // protegido por _cbLock
    CRITICAL_SECTION     _cbLock;
    volatile LONG        _pendingStatus;  // NTSTATUS publicado antes de senalizar el evento
    volatile LONG        _pendingSubstatus;
    volatile LONG        _awaiting;       // 1 mientras se espera el resultado de un intento
};
