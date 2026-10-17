#include "PipeServer.h"
#include <thread>
#include <atomic>
#include <iostream>
#include <mutex>
#include <set>
#include <vector>

// The pipe we listen on. The UI connects to this exact name.
static const wchar_t* kPipeName = L"\\\\.\\pipe\\BlueprintEngine";

static HANDLE            g_hStopEvent = nullptr;
static std::thread       g_serverThread;
static std::atomic<bool> g_running{ false };
static HWND              g_hNotifyWnd = nullptr;
static std::mutex        g_pendingMutex;
static std::set<PendingRequest*> g_pendingRequests;

static bool UnregisterPendingRequest(PendingRequest* req) {
    std::lock_guard<std::mutex> lock(g_pendingMutex);
    auto it = g_pendingRequests.find(req);
    if (it == g_pendingRequests.end()) return false;
    g_pendingRequests.erase(it);
    return true;
}

static void ClosePendingRequest(PendingRequest* req) {
    if (!req) return;
    if (req->pipeHandle && req->pipeHandle != INVALID_HANDLE_VALUE) {
        DisconnectNamedPipe(req->pipeHandle);
        CloseHandle(req->pipeHandle);
    }
    delete req;
}

static void ReleasePendingRequest(PendingRequest* req) {
    if (UnregisterPendingRequest(req))
        ClosePendingRequest(req);
}

// Simple loop that accepts one connection at a time and forwards the
// request to the main thread via PostMessage.
static void ServerLoop() {
    while (g_running.load()) {
        HANDLE hPipe = CreateNamedPipeW(
            kPipeName,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            65536, 65536, 0, nullptr);

        if (hPipe == INVALID_HANDLE_VALUE) {
            Sleep(500);
            continue;
        }

        // Wait for a client to connect.
        BOOL connected = ConnectNamedPipe(hPipe, nullptr);
        if (!g_running.load()) {
            CloseHandle(hPipe);
            break;
        }
        if (!connected && GetLastError() != ERROR_PIPE_CONNECTED) {
            CloseHandle(hPipe);
            continue;
        }

        // Read one message from the client.
        char  buffer[65536] = {};
        DWORD bytesRead = 0;
        if (!ReadFile(hPipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr)
            || bytesRead == 0) {
            DisconnectNamedPipe(hPipe);
            CloseHandle(hPipe);
            continue;
        }
        buffer[bytesRead] = '\0';

        // Hand off to the main thread. It will write the response and
        // close the pipe. We do NOT close hPipe here.
        PendingRequest* req = new PendingRequest();
        req->pipeHandle = hPipe;
        req->requestJson = std::string(buffer, bytesRead);
        std::lock_guard<std::mutex> lock(g_pendingMutex);
        g_pendingRequests.insert(req);
        if (!PostMessageW(g_hNotifyWnd, WM_APP_IPC_REQUEST, 0,
            reinterpret_cast<LPARAM>(req))) {
            // PostMessage failed — clean up ourselves while ownership is held.
            g_pendingRequests.erase(req);
            ClosePendingRequest(req);
        }
    }
}

bool PipeServer_Start(HWND hNotifyWnd) {
    g_hNotifyWnd = hNotifyWnd;
    g_hStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_hStopEvent) return false;

    g_running.store(true);
    g_serverThread = std::thread(ServerLoop);
    std::cout << "[IPC ] Listening on \\\\.\\pipe\\BlueprintEngine\n";
    return true;
}

void PipeServer_Stop() {
    if (!g_running.exchange(false)) return;
    if (g_hStopEvent) SetEvent(g_hStopEvent);
    if (g_serverThread.joinable())
        CancelSynchronousIo(g_serverThread.native_handle());

    // Unblock ConnectNamedPipe by connecting once ourselves.
    HANDLE hSelf = CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE,
        0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (hSelf != INVALID_HANDLE_VALUE) CloseHandle(hSelf);

    if (g_serverThread.joinable()) g_serverThread.join();
    if (g_hStopEvent) { CloseHandle(g_hStopEvent); g_hStopEvent = nullptr; }
}

void PipeServer_RejectPendingRequests() {
    MSG msg = {};
    while (PeekMessageW(&msg, nullptr, WM_APP_IPC_REQUEST,
        WM_APP_IPC_REQUEST, PM_REMOVE)) {
        ReleasePendingRequest(reinterpret_cast<PendingRequest*>(msg.lParam));
    }

    std::vector<PendingRequest*> pending;
    {
        std::lock_guard<std::mutex> lock(g_pendingMutex);
        pending.assign(g_pendingRequests.begin(), g_pendingRequests.end());
    }

    for (PendingRequest* req : pending)
        ReleasePendingRequest(req);
}

void PipeServer_CompleteRequest(PendingRequest* req,
    const std::string& responseJson) {
    if (!req) return;
    if (!UnregisterPendingRequest(req)) return;
    if (req->pipeHandle && req->pipeHandle != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(req->pipeHandle, responseJson.c_str(),
            (DWORD)responseJson.size(), &written, nullptr);
        FlushFileBuffers(req->pipeHandle);
        DisconnectNamedPipe(req->pipeHandle);
        CloseHandle(req->pipeHandle);
    }
    delete req;
}