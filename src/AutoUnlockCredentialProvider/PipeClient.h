#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <functional>

using CredReceivedCallback = std::function<void(const std::wstring&, const std::wstring&)>;

class CPipeClient
{
public:
    explicit CPipeClient(CredReceivedCallback callback);
    ~CPipeClient();

    void Start();
    void Stop();

private:
    static DWORD WINAPI ThreadProc(LPVOID lpParam);
    void Run();

    HANDLE            _hThread;
    HANDLE            _hStopEvent;
    CredReceivedCallback _callback;
};
