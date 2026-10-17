#pragma once
#pragma once

#include <windows.h>
#include <string>

// Custom message the pipe thread posts to the hidden window.
// The main thread handles it in HiddenWndProc.
#define WM_APP_IPC_REQUEST (WM_APP + 42)

// One incoming request, handed off from the pipe thread to the main thread.
struct PendingRequest {
    HANDLE pipeHandle;         // open pipe to write the response on
    std::string requestJson;   // the raw JSON request
};

// Starts a background thread that listens on \\.\pipe\BlueprintEngine.
// Every received request is PostMessage'd to hNotifyWnd as WM_APP_IPC_REQUEST
// with lParam = new PendingRequest* (the main thread must delete it via
// PipeServer_CompleteRequest).
bool PipeServer_Start(HWND hNotifyWnd);

// Signals the thread to stop and joins it. Call from cleanup.
void PipeServer_Stop();

// Rejects requests already posted to the hidden window but not dispatched
// before shutdown. Call after PipeServer_Stop and before destroying the window.
void PipeServer_RejectPendingRequests();

// Sends the response back to the client, closes the pipe, frees the request.
void PipeServer_CompleteRequest(PendingRequest* req, const std::string& responseJson);