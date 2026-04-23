#include "PipeClient.h"
#include <string>

static const WCHAR PIPE_NAME[] = L"\\\\.\\pipe\\AutoUnlock";

CPipeClient::CPipeClient(CredReceivedCallback callback)
    : _hThread(nullptr)
    , _callback(std::move(callback))
{
    _hStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
}

CPipeClient::~CPipeClient()
{
    Stop();
    if (_hStopEvent) CloseHandle(_hStopEvent);
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
            PIPE_NAME,
            GENERIC_READ | GENERIC_WRITE,
            0, nullptr,
            OPEN_EXISTING,
            0, nullptr);

        if (hPipe == INVALID_HANDLE_VALUE)
        {
            WaitForSingleObject(_hStopEvent, 1000);
            continue;
        }

        DWORD dwMode = PIPE_READMODE_BYTE;
        SetNamedPipeHandleState(hPipe, &dwMode, nullptr, nullptr);

        char buf[1024] = {};
        DWORD bytesRead = 0;
        BOOL ok = ReadFile(hPipe, buf, sizeof(buf) - 1, &bytesRead, nullptr);

        if (ok && bytesRead > 0)
        {
            buf[bytesRead] = '\0';
            std::string data(buf, bytesRead);

            auto nl1 = data.find('\n');
            if (nl1 != std::string::npos)
            {
                std::string usernameUtf8 = data.substr(0, nl1);
                std::string rest = data.substr(nl1 + 1);
                auto nl2 = rest.find('\n');
                std::string passwordUtf8 = rest.substr(0, nl2 != std::string::npos ? nl2 : rest.size());

                auto toWide = [](const std::string& s) -> std::wstring {
                    if (s.empty()) return {};
                    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
                    std::wstring w(len - 1, L'\0');
                    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), len);
                    return w;
                };

                std::wstring username = toWide(usernameUtf8);
                std::wstring password = toWide(passwordUtf8);

                DWORD written;
                WriteFile(hPipe, "OK\n", 3, &written, nullptr);

                _callback(username, password);
            }
        }

        CloseHandle(hPipe);
        WaitForSingleObject(_hStopEvent, 500);
    }
}
