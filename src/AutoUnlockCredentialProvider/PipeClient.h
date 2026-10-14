#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <functional>

// NTSTATUS no viene con windows.h en modo WIN32_LEAN_AND_MEAN; la redeclaracion identica es legal
typedef LONG NTSTATUS;

using CredReceivedCallback = std::function<void(const std::wstring&, const std::wstring&)>;

class CPipeClient
{
public:
    explicit CPipeClient(CredReceivedCallback callback);
    ~CPipeClient();

    void Start();
    void Stop();

    // La llama la credencial desde ReportResult; despierta al hilo del canal
    void ReportResult(NTSTATUS status);

private:
    static DWORD WINAPI ThreadProc(LPVOID lpParam);
    void Run();
    bool HandleConnection(HANDLE hPipe);
    bool WriteLine(HANDLE hPipe, const std::string& text);

    HANDLE               _hThread;
    HANDLE               _hStopEvent;
    HANDLE               _hResultEvent;   // senalizado cuando llega el NTSTATUS
    CredReceivedCallback _callback;
    volatile LONG        _pendingStatus;  // NTSTATUS publicado antes de senalizar el evento
};
