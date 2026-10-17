// mpv_dynamic.h — Dynamic loader for libmpv-2.dll
// Include this AFTER <windows.h>. Do NOT include client.h directly.

#pragma once
#include <windows.h>
#include <string>

// ─── mpv forward declarations (we never include client.h in the engine) ───
struct mpv_handle;
struct mpv_event;

// ─── Function pointer typedefs ───

typedef mpv_handle* (*PFN_mpv_create)();
typedef int          (*PFN_mpv_initialize)(mpv_handle*);
typedef void         (*PFN_mpv_terminate_destroy)(mpv_handle*);
typedef void         (*PFN_mpv_destroy)(mpv_handle*);

typedef int (*PFN_mpv_set_option_string)(mpv_handle*, const char*, const char*);
typedef int (*PFN_mpv_set_property_string)(mpv_handle*, const char*, const char*);
typedef int (*PFN_mpv_get_property_string)(mpv_handle*, const char*, const char**);

// mpv_command takes a NULL-terminated array of C strings
typedef int (*PFN_mpv_command)(mpv_handle*, const char**);

// Event handling
typedef mpv_event* (*PFN_mpv_wait_event)(mpv_handle*, double);
typedef void       (*PFN_mpv_set_wakeup_callback)(mpv_handle*, void (*)(void*), void*);

// ─── Resolved function pointers (global, one set for the whole process) ───

struct MpvApi {
    HMODULE                         dll = nullptr;

    PFN_mpv_create                  create = nullptr;
    PFN_mpv_initialize              initialize = nullptr;
    PFN_mpv_terminate_destroy       terminate_destroy = nullptr;
    PFN_mpv_destroy                 destroy = nullptr;

    PFN_mpv_set_option_string       set_option_string = nullptr;
    PFN_mpv_set_property_string     set_property_string = nullptr;
    PFN_mpv_get_property_string     get_property_string = nullptr;
    PFN_mpv_command                 command = nullptr;

    PFN_mpv_wait_event              wait_event = nullptr;
    PFN_mpv_set_wakeup_callback     set_wakeup_callback = nullptr;
};

inline MpvApi g_mpv;   // single global instance

// ─── Loader ───

inline bool LoadMpvDll(const wchar_t* dllName = L"libmpv-2.dll") {
    if (g_mpv.dll) return true;   // already loaded

    g_mpv.dll = LoadLibraryW(dllName);
    if (!g_mpv.dll) {
        // Try fallback: look next to the exe
        wchar_t exePath[MAX_PATH];
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        std::wstring dir(exePath);
        dir = dir.substr(0, dir.find_last_of(L"\\/") + 1);
        dir += dllName;
        g_mpv.dll = LoadLibraryW(dir.c_str());
        if (!g_mpv.dll) return false;
    }

#define LOAD(name) \
        g_mpv.name = (PFN_mpv_##name)GetProcAddress(g_mpv.dll, "mpv_" #name); \
        if (!g_mpv.name) { FreeLibrary(g_mpv.dll); g_mpv.dll = nullptr; return false; }

    LOAD(create)
        LOAD(initialize)
        LOAD(terminate_destroy)
        LOAD(destroy)
        LOAD(set_option_string)
        LOAD(set_property_string)
        LOAD(get_property_string)
        LOAD(command)
        LOAD(wait_event)
        LOAD(set_wakeup_callback)

#undef LOAD
        return true;
}

inline void UnloadMpvDll() {
    if (g_mpv.dll) {
        FreeLibrary(g_mpv.dll);
        g_mpv.dll = nullptr;
    }
}