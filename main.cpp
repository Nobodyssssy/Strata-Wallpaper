#define _CRT_SECURE_NO_WARNINGS

#include <windows.h>
#include <wtsapi32.h>
#include <commdlg.h>
#include <shellapi.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <cstdlib>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <iostream>
#include <vector>
#include <string>
#include <map>
#include <set>
#include <atomic>
#include <thread>
#include <mutex>
#include "mpv_dynamic.h"
#include "PipeServer.h"
#include <nlohmann/json.hpp>
#include <io.h>
#include <dwmapi.h>
#include <tlhelp32.h>
#include <stdio.h>

using json = nlohmann::json;

#pragma comment(lib, "wtsapi32.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "bcrypt.lib")

// =====================================================================
// Forward declarations
// =====================================================================
static std::string GetVideoPathForMonitor(int index, bool& hasOverride);
static std::string GetSettingsFilePath();
static std::string GetLogFilePath();
static std::string HandleIpcCommand(const std::string& raw);
static int g_targetFps = 0; // 0 = no limit
static void RefreshPreviewVideo(const std::string& path);

static void ApplyQualityToPlayer(mpv_handle* mpv, const std::string& mode);
static void ApplyQualityChange(const std::string& newMode);
static std::string BuildVideoFilter();

// Forward declarations for functions used by ApplyQualityChange but
// defined later in this translation unit.
static void StartPreview(const std::string& overridePath);
static void StopPreview();
static bool SetupWallpapers(bool fastReembed = false);
static void ReleaseAllWallpapers();

static void StartLockOverlay();
static void StopLockOverlay();
static void DismissLockOverlay();
static bool GetLockVideoPath(std::string& out, int monitorIdx);

static bool LoadPrayerCache();
static void SavePrayerCache();
static void FetchPrayerTimesAsync();
static bool GetPrayerTimesForToday(std::string& sunrise, std::string& maghrib);

// =====================================================================
// Global state
// =====================================================================

struct WallpaperInstance {
    HWND        hwnd;
    mpv_handle* player;
    HMONITOR    monitor;
    int         index;
    bool        hasOverride;
    std::string currentVideoPath;
    ULONGLONG   warmupUntil;
};
std::vector<WallpaperInstance> g_wallpapers;
std::map<HWND, bool> g_monitorPaused;

std::atomic<bool> g_sessionLocked{ false };
std::atomic<bool> g_manualPaused{ false };

HWND g_hWorkerW = nullptr;
HWND g_hHiddenWnd = nullptr;
HWND g_hPreviewWnd = nullptr;
mpv_handle* g_previewPlayer = nullptr;
static std::string g_previewCurrentPath;

std::string g_videoPath;

static int g_monitorIndex = 0;
static int g_idlePauseSeconds = 0;

// ---- Quality -------------------------------------------------------
static std::string g_qualityMode = "balanced";   // fast | balanced | high | vsr

// ---- Slideshow -----------------------------------------------------
struct SlideshowConfig {
    bool enabled = false;
    int intervalMinutes = 10;
    bool random = false;
    std::vector<std::string> playlist;
};
static SlideshowConfig g_slideshow;
static ULONGLONG g_lastSlideshowSwitch = 0;
static int g_nextPlaylistIndex = 0;

// ---- Day / Night ---------------------------------------------------
// Mode: "manual" | "sunrise" | "prayer"
struct DayNightConfig {
    bool enabled = false;
    std::string mode = "sunrise";
    double latitude = 0.0;
    double longitude = 0.0;
    std::string dayVideo;
    std::string nightVideo;
    bool isDay = true;

    // manual
    std::string manualDayStart = "07:00";
    std::string manualNightStart = "19:00";

    // prayer
    int    prayerMethod = 3;
    int    duhaOffsetMinutes = 20;
};
static DayNightConfig g_dayNight;
static DayNightConfig g_lockDayNight;
static ULONGLONG g_lastDayNightCheck = 0;
static ULONGLONG g_lastSunCalcTick = 0;
static double g_cachedSunrise = -1;
static double g_cachedSunset = -1;

// ── Per-monitor assignments (Batch 8) ──
// Must be declared AFTER DayNightConfig since they embed it.
struct MonitorAssignment {
    std::string    desktopPath;
    DayNightConfig dayNight;
    bool           dayNightEnabled = false;
};
static std::map<int, MonitorAssignment> g_monitorAssignments;

struct LockMonitorAssignment {
    std::string    lockPath;
    DayNightConfig dayNight;
    bool           dayNightEnabled = false;
};
static std::map<int, LockMonitorAssignment> g_lockMonitorAssignments;

// ---- Prayer times cache --------------------------------------------
struct PrayerDay {
    std::string date;     // "DD-MM-YYYY"
    std::string sunrise;  // "HH:MM"
    std::string maghrib;  // "HH:MM"
};
struct PrayerCache {
    time_t fetched_at = 0;
    int method = 3;
    double latitude = 0.0;
    double longitude = 0.0;
    std::vector<PrayerDay> days;
};
static PrayerCache g_prayerCache;
static std::mutex g_prayerMutex;
static std::atomic<bool> g_prayerFetchInProgress{ false };
static std::atomic<bool> g_prayerFetchDone{ false };
static std::atomic<bool> g_prayerFetchCancel{ false };
static std::thread g_prayerFetchThread;

static constexpr ULONGLONG kPrayerCacheTtlSeconds = 7ULL * 24ULL * 3600ULL;

// ---- Lock overlay --------------------------------------------------
struct LockOverlay {
    HWND        hwnd;
    mpv_handle* player;
    HMONITOR    monitor;
    int         index;
    bool        isPrimary;
};
static std::vector<LockOverlay> g_lockOverlays;

static bool         g_lockEnabled = false;
static int          g_lockIdleSeconds = 300;     // 5 min default
static std::string  g_lockMode = "screensaver";  // "screensaver" | "password"
static std::string  g_lockPasswordHash;
static bool         g_lockUseDayNightVideos = true;
static std::string  g_lockDayVideo;
static std::string  g_lockNightVideo;

// ── New (Batch 7) ──
static bool         g_lockPauseOnLock = false;
static bool         g_lockDimWhenLocked = true;
static bool         g_lockShowClock = true;
static std::string  g_lockFillMode = "fill";   // fill|fit|stretch|center
static int          g_lockFps = 30;
static std::string  g_lockWallpaperPath;
static std::map<int, std::string> g_lockMonitorWallpaperPaths;

// Shuffle mode:
//   "off"            — sequential playlist
//   "random"         — uniform random, no immediate repeats
//   "weighted"       — favorites more likely (weight 3 vs 1)
//   "favorites_only" — rotate favorites only; falls back to uniform if empty
static std::string  g_shuffleMode = "off";
static std::set<std::string> g_favoritePaths;

static bool         g_lockActive = false;
static ULONGLONG    g_lockStartedAt = 0;
static bool         g_lockPasswordPromptVisible = false;
static std::wstring g_lockTypedPassword;
static std::wstring g_lockPromptMessage;

// Low-level keyboard hook installed while the lock overlay is up.
// Blocks Win, Win+Tab, Alt+Tab, Alt+F4, Ctrl+Esc so the overlay
// isn't trivially dismissed by opening the Start menu or Task View.
// Ctrl+Shift+Esc (Task Manager) and Win+L (OS sign-in) can't be
// blocked without a signed driver — out of scope for a screensaver-
// style overlay.
static HHOOK g_hLockKeyboardHook = nullptr;

// Password prompt auto-hide. Reset on any key or mouse event; when
// idle for this many ms, the prompt hides and the lock overlay goes
// back to just clock-over-video.
static ULONGLONG    g_lockLastPasswordInputTick = 0;
static constexpr ULONGLONG kLockPasswordHideAfterMs = 6000;

// ---- Library -------------------------------------------------------
static std::vector<std::string> g_libraryFolders;
static std::vector<std::string> g_libraryFiles;
static std::string g_forceVideoPath;
static std::map<int, std::string> g_monitorForcePaths;
static std::string g_displayMode = "copy";

// =====================================================================
// Utility helpers
// =====================================================================
static bool IsVideoFile(const std::string& path) {
    size_t dot = path.find_last_of('.');
    if (dot == std::string::npos) return false;
    std::string ext = path.substr(dot);
    for (auto& c : ext) c = (char)tolower(c);
    return ext == ".mp4" || ext == ".mkv" || ext == ".webm"
        || ext == ".avi" || ext == ".mov" || ext == ".wmv";
}

static bool FileExists(const std::string& p) {
    return !p.empty() && GetFileAttributesA(p.c_str()) != INVALID_FILE_ATTRIBUTES;
}

static bool ParseTimeHHMM(const std::string& s, int& h, int& m) {
    if (s.size() < 5) return false;
    if (s[0] < '0' || s[0] > '9') return false;
    if (s[1] < '0' || s[1] > '9') return false;
    if (s[2] != ':') return false;
    if (s[3] < '0' || s[3] > '9') return false;
    if (s[4] < '0' || s[4] > '9') return false;
    h = (s[0] - '0') * 10 + (s[1] - '0');
    m = (s[3] - '0') * 10 + (s[4] - '0');
    return h >= 0 && h < 24 && m >= 0 && m < 60;
}

static std::string Narrow(const wchar_t* w) {
    if (!w) return "";
    char buf[256] = {};
    WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, sizeof(buf) - 1, nullptr, nullptr);
    return buf;
}

static std::wstring Widen(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

static std::string Sha256Hex(const std::string& input) {
    BCRYPT_ALG_HANDLE hAlg = nullptr;
    if (BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0)
        return "";
    DWORD hashLen = 0, cbResult = 0;
    BCryptGetProperty(hAlg, BCRYPT_HASH_LENGTH,
        reinterpret_cast<PUCHAR>(&hashLen), sizeof(hashLen), &cbResult, 0);

    std::vector<BYTE> hash(hashLen);
    BCRYPT_HASH_HANDLE hHash = nullptr;
    BCryptCreateHash(hAlg, &hHash, nullptr, 0, nullptr, 0, 0);
    BCryptHashData(hHash, reinterpret_cast<PUCHAR>(const_cast<char*>(input.data())),
        (ULONG)input.size(), 0);
    BCryptFinishHash(hHash, hash.data(), hashLen, 0);
    BCryptDestroyHash(hHash);
    BCryptCloseAlgorithmProvider(hAlg, 0);

    std::string out;
    out.reserve(hashLen * 2);
    char b[3];
    for (BYTE by : hash) {
        sprintf_s(b, "%02x", by);
        out += b;
    }
    return out;
}

// =====================================================================
// Library helpers
// =====================================================================
static void ScanFolderRecursive(const std::string& folder,
    std::vector<std::string>& out) {
    std::string pattern = folder + "\\*";
    WIN32_FIND_DATAA fd{};
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;

    do {
        if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0)
            continue;
        std::string full = folder + "\\" + fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            ScanFolderRecursive(full, out);
        }
        else if (IsVideoFile(fd.cFileName)) {
            out.push_back(full);
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
}

static void LoadLibraryFolders() {
    g_libraryFolders.clear();
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return;

    char buf[8192] = {};
    GetPrivateProfileStringA("Library", "Folders", "", buf, 8192, ini.c_str());

    std::string s = buf;
    size_t start = 0;
    while (start < s.size()) {
        size_t end = s.find(';', start);
        if (end == std::string::npos) end = s.size();
        std::string item = s.substr(start, end - start);
        size_t a = item.find_first_not_of(" \t");
        size_t b = item.find_last_not_of(" \t");
        if (a != std::string::npos && b != std::string::npos)
            g_libraryFolders.push_back(item.substr(a, b - a + 1));
        start = end + 1;
    }
}

static void SaveLibraryToDisk() {
    std::string ini = GetSettingsFilePath();
    size_t slash = ini.find_last_of("\\/");
    if (slash == std::string::npos) return;
    std::string path = ini.substr(0, slash + 1) + "library.json";

    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "wb") != 0 || !f) return;

    fputs("{\n  \"version\": 1,\n  \"count\": ", f);
    fprintf(f, "%d,\n  \"files\": [\n", (int)g_libraryFiles.size());
    for (size_t i = 0; i < g_libraryFiles.size(); i++) {
        std::string esc;
        for (char c : g_libraryFiles[i]) {
            if (c == '\\') esc += "\\\\";
            else if (c == '"') esc += "\\\"";
            else esc += c;
        }
        std::string name = g_libraryFiles[i];
        size_t s2 = name.find_last_of("\\/");
        if (s2 != std::string::npos) name = name.substr(s2 + 1);
        fprintf(f, "    { \"path\": \"%s\", \"name\": \"%s\" }%s\n",
            esc.c_str(), name.c_str(),
            (i + 1 < g_libraryFiles.size() ? "," : ""));
    }
    fputs("  ]\n}\n", f);
    fclose(f);
}

static PrayerCache GetPrayerCacheSnapshot() {
    std::lock_guard<std::mutex> lock(g_prayerMutex);
    return g_prayerCache;
}

static int RescanLibrary() {
    g_libraryFiles.clear();
    for (auto& folder : g_libraryFolders) {
        if (GetFileAttributesA(folder.c_str()) == INVALID_FILE_ATTRIBUTES)
            continue;
        ScanFolderRecursive(folder, g_libraryFiles);
    }
    std::cout << "[LIBRARY] Scanned " << g_libraryFolders.size()
        << " folder(s), found " << g_libraryFiles.size() << " video(s).\n";
    SaveLibraryToDisk();
    return (int)g_libraryFiles.size();
}

// =====================================================================
// Tray / single-instance
// =====================================================================
static NOTIFYICONDATAW g_nid = {};
static HICON g_hTrayIcon = nullptr;
static HANDLE g_hSingleInstanceMutex = nullptr;
static UINT   g_wmShowExisting = 0;

// Startup balloon. Off by default — the notification is redundant
// once you know the tray icon exists. Override with
// [Engine] ShowStartupBalloon=1 in settings.ini if you ever want it back.
static bool   g_showStartupBalloon = false;

constexpr wchar_t kMutexName[] = L"Local\\StrataWallpaper_Engine_SingleInstance";
constexpr UINT WM_TRAYICON = WM_APP + 1;
constexpr UINT WM_APP_PRAYER_FETCH_DONE = WM_APP + 10;
constexpr UINT ID_TRAY_PAUSE_RESUME = 40001;
constexpr UINT ID_TRAY_OPEN_SETTINGS = 40002;
constexpr UINT ID_TRAY_OPEN_LOG = 40003;
constexpr UINT ID_TRAY_RELOAD = 40004;
constexpr UINT ID_TRAY_AUTOSTART = 40005;
constexpr UINT ID_TRAY_EXIT = 40006;
constexpr UINT ID_TRAY_OPEN_PANEL = 40007;

constexpr wchar_t kRunKeyPath[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kRunValueName[] = L"StrataWallpaper";

constexpr ULONGLONG kPauseCheckMs = 500;
constexpr ULONGLONG kExplorerCheckMs = 1000;
constexpr ULONGLONG kSlideshowCheckMs = 5000;
constexpr ULONGLONG kDayNightCheckMs = 60000;
constexpr ULONGLONG kSunCalcIntervalMs = 3600000;
constexpr ULONGLONG kLockCheckMs = 1000;

constexpr wchar_t kHiddenWndClass[] = L"StrataWallpaperHiddenWnd";
constexpr wchar_t kLockWndClass[] = L"StrataLockOverlayClass";

// =====================================================================
// Video filter (used both at creation and on set_fps / set_quality)
// =====================================================================
static std::string BuildVideoFilter() {
    std::string vf;
    if (g_qualityMode == "vsr") {
        vf += "d3d11vpp=scaling-mode=nvidia:scale=1.33";
    }
    if (g_targetFps > 0) {
        if (!vf.empty()) vf += ",";
        vf += "fps=fps=" + std::to_string(g_targetFps);
    }
    return vf;
}

// =====================================================================
// mpv helpers
// =====================================================================
static mpv_handle* CreateMpvPlayer(HWND hwnd, int targetFps = 0) {
    if (!g_mpv.dll || !g_mpv.create) return nullptr;

    mpv_handle* mpv = g_mpv.create();
    if (!mpv) return nullptr;

    char widBuf[32];
    sprintf_s(widBuf, "0x%llx", (unsigned long long)(uintptr_t)hwnd);
    g_mpv.set_option_string(mpv, "wid", widBuf);

    // vo=gpu-next, as agreed for the quality expansion.
    g_mpv.set_option_string(mpv, "vo", "gpu-next");
    g_mpv.set_option_string(mpv, "profile", "fast");
    g_mpv.set_option_string(mpv, "gpu-api", "d3d11");
    g_mpv.set_option_string(mpv, "gpu-context", "d3d11");

    // Keepaspect, no audio, no OSD, seamless loop.
    g_mpv.set_option_string(mpv, "keepaspect", "no");
    g_mpv.set_option_string(mpv, "video-unscaled", "no");
    g_mpv.set_option_string(mpv, "audio", "no");
    g_mpv.set_option_string(mpv, "ao", "null");
    g_mpv.set_option_string(mpv, "osc", "no");
    g_mpv.set_option_string(mpv, "osd-bar", "no");
    g_mpv.set_option_string(mpv, "input-default-bindings", "no");
    g_mpv.set_option_string(mpv, "input-vo-keyboard", "no");
    g_mpv.set_option_string(mpv, "loop-file", "inf");
    g_mpv.set_option_string(mpv, "vd-lavc-skiploopfilter", "all");
    g_mpv.set_option_string(mpv, "terminal", "yes");
    g_mpv.set_option_string(mpv, "msg-level", "all=warn");

    // Belt-and-suspenders: tell mpv not to draw anything on its own.
    // Explicit drawtext filters are unaffected.
    g_mpv.set_option_string(mpv, "osd-playing-msg", "");
    g_mpv.set_option_string(mpv, "osd-msg1", "");
    g_mpv.set_option_string(mpv, "osd-msg2", "");
    g_mpv.set_option_string(mpv, "osd-msg3", "");

    // Lock-screen clock via mpv OSD (sits above the video).
    // §FIX — osd-level back to 3 so ASS override tags render ({\fs120\bord3...}).
    // §FIX — osd-duration=1ms so auto-triggered OSD (loop-seek status
    // readout "00:00:07 / 00:00:08 (95%)") flashes once and vanishes.
    // Previously 0, which per the mpv manual means "stay visible until
    // hidden explicitly" — the readout was sticking permanently.
    // The clock's explicit show-text duration (1200ms) is unaffected.
    g_mpv.set_option_string(mpv, "osd-level", "1");
    g_mpv.set_option_string(mpv, "osd-duration", "1");
    g_mpv.set_option_string(mpv, "osd-bar", "no");
    g_mpv.set_option_string(mpv, "osd-align-x", "center");
    g_mpv.set_option_string(mpv, "osd-align-y", "center");
    g_mpv.set_option_string(mpv, "osd-font", "Segoe UI");
    g_mpv.set_option_string(mpv, "osd-font-size", "60");

    // ── Quality: hwdec and scaler options MUST be before initialize ──
    if (g_qualityMode == "vsr") {
        g_mpv.set_option_string(mpv, "hwdec", "d3d11va");
    }
    else {
        g_mpv.set_option_string(mpv, "hwdec", "no");
        if (g_qualityMode == "fast") {
            g_mpv.set_option_string(mpv, "scale", "bilinear");
            g_mpv.set_option_string(mpv, "cscale", "bilinear");
            g_mpv.set_option_string(mpv, "sigmoid-upscaling", "no");
            g_mpv.set_option_string(mpv, "linear-downscaling", "no");
        }
        else if (g_qualityMode == "balanced") {
            g_mpv.set_option_string(mpv, "scale", "ewa_lanczossharp");
            g_mpv.set_option_string(mpv, "cscale", "ewa_lanczossharp");
            g_mpv.set_option_string(mpv, "sigmoid-upscaling", "yes");
            g_mpv.set_option_string(mpv, "linear-downscaling", "no");
        }
        else if (g_qualityMode == "high") {
            g_mpv.set_option_string(mpv, "scale", "ewa_lanczos4sharpest");
            g_mpv.set_option_string(mpv, "cscale", "ewa_lanczos4sharpest");
            g_mpv.set_option_string(mpv, "sigmoid-upscaling", "yes");
            g_mpv.set_option_string(mpv, "linear-downscaling", "yes");
        }
    }

    // ── Combined vf: VSR + FPS cap (order matters, VSR first) ──
    std::string vf = BuildVideoFilter();
    if (!vf.empty()) {
        g_mpv.set_option_string(mpv, "vf", vf.c_str());
    }

    (void)targetFps; // g_targetFps is authoritative now

    if (g_mpv.initialize(mpv) < 0) {
        g_mpv.terminate_destroy(mpv);
        return nullptr;
    }

    std::cout << "[QUALITY] player created vo=gpu-next mode=" << g_qualityMode
        << " vf=\"" << vf << "\"\n";
    return mpv;
}

static void UpdateVlcDisplay(HWND hwnd, mpv_handle* player) {
    (void)hwnd; (void)player;
}

// =====================================================================
// Quality — runtime application and change orchestration
// =====================================================================
static void ApplyQualityToPlayer(mpv_handle* mpv, const std::string& mode) {
    if (!mpv) return;
    if (mode == "fast") {
        g_mpv.set_property_string(mpv, "scale", "bilinear");
        g_mpv.set_property_string(mpv, "cscale", "bilinear");
        g_mpv.set_property_string(mpv, "sigmoid-upscaling", "no");
        g_mpv.set_property_string(mpv, "linear-downscaling", "no");
    }
    else if (mode == "balanced") {
        g_mpv.set_property_string(mpv, "scale", "ewa_lanczossharp");
        g_mpv.set_property_string(mpv, "cscale", "ewa_lanczossharp");
        g_mpv.set_property_string(mpv, "sigmoid-upscaling", "yes");
        g_mpv.set_property_string(mpv, "linear-downscaling", "no");
    }
    else if (mode == "high") {
        g_mpv.set_property_string(mpv, "scale", "ewa_lanczos4sharpest");
        g_mpv.set_property_string(mpv, "cscale", "ewa_lanczos4sharpest");
        g_mpv.set_property_string(mpv, "sigmoid-upscaling", "yes");
        g_mpv.set_property_string(mpv, "linear-downscaling", "yes");
    }
}

static void ApplyQualityChange(const std::string& newMode) {
    if (newMode != "fast" && newMode != "balanced"
        && newMode != "high" && newMode != "vsr") {
        std::cout << "[QUALITY] unknown mode: " << newMode << "\n";
        return;
    }

    std::string oldMode = g_qualityMode;
    g_qualityMode = newMode;

    WritePrivateProfileStringA("Performance", "Quality",
        newMode.c_str(), GetSettingsFilePath().c_str());

    bool needsRebuild = (oldMode == "vsr" || newMode == "vsr");

    if (!needsRebuild) {
        std::cout << "[QUALITY] runtime switch " << oldMode << " -> " << newMode << "\n";
        for (auto& wp : g_wallpapers) ApplyQualityToPlayer(wp.player, newMode);
        if (g_previewPlayer) ApplyQualityToPlayer(g_previewPlayer, newMode);
        for (auto& lo : g_lockOverlays) ApplyQualityToPlayer(lo.player, newMode);
        return;
    }

    std::cout << "[QUALITY] rebuild required " << oldMode << " -> " << newMode << "\n";

    std::string previewPath = g_previewCurrentPath;
    bool hadPreview = (g_hPreviewWnd != nullptr);
    if (hadPreview) StopPreview();

    bool hadLock = g_lockActive;
    if (hadLock) StopLockOverlay();

    ReleaseAllWallpapers();
    SetupWallpapers();

    if (hadLock) StartLockOverlay();
    if (hadPreview && !previewPath.empty()) StartPreview(previewPath);
}

// =====================================================================
// Idle detection
// =====================================================================
static int GetIdleSeconds() {
    LASTINPUTINFO lii{};
    lii.cbSize = sizeof(lii);
    if (!GetLastInputInfo(&lii)) return 0;
    DWORD now = GetTickCount();
    DWORD idleMs = now - lii.dwTime;
    return (int)(idleMs / 1000);
}

// =====================================================================
// Pause condition detection
// =====================================================================
static HMONITOR GetForegroundMonitorWithActiveWindow() {
    HWND fg = GetForegroundWindow();
    if (!fg || !IsWindow(fg)) return nullptr;

    HWND root = GetAncestor(fg, GA_ROOT);
    if (root && IsWindow(root)) fg = root;

    // Our own lock-overlay is a "fullscreen window" from our perspective,
    // but we don't want to treat it as an active app.
    if (fg == g_hHiddenWnd) return nullptr;
    for (auto& lo : g_lockOverlays)
        if (fg == lo.hwnd) return nullptr;

    DWORD pid = 0;
    GetWindowThreadProcessId(fg, &pid);
    if (pid == GetCurrentProcessId()) return nullptr;

    wchar_t cls[64] = {};
    GetClassNameW(fg, cls, 64);
    if (_wcsicmp(cls, L"Progman") == 0) return nullptr;
    if (_wcsicmp(cls, L"WorkerW") == 0) return nullptr;
    if (_wcsicmp(cls, L"Shell_TrayWnd") == 0) return nullptr;
    if (_wcsicmp(cls, L"Shell_SecondaryTrayWnd") == 0) return nullptr;

    BOOL cloaked = FALSE;
    DwmGetWindowAttribute(fg, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
    if (cloaked) return nullptr;

    RECT wr{};
    if (!GetWindowRect(fg, &wr)) return nullptr;

    HMONITOR hm = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
    if (!hm) return nullptr;

    MONITORINFO mi = { sizeof(mi) };
    if (!GetMonitorInfoW(hm, &mi)) return nullptr;

    int monW = mi.rcMonitor.right - mi.rcMonitor.left;
    int monH = mi.rcMonitor.bottom - mi.rcMonitor.top;
    int wrkW = mi.rcWork.right - mi.rcWork.left;
    int wrkH = mi.rcWork.bottom - mi.rcWork.top;
    int winW = wr.right - wr.left;
    int winH = wr.bottom - wr.top;

    bool coversMonitor = (winW >= monW * 95 / 100) && (winH >= monH * 95 / 100);
    bool coversWork = (winW >= wrkW * 95 / 100) && (winH >= wrkH * 95 / 100);

    return (coversMonitor || coversWork) ? hm : nullptr;
}

static bool IsOnBatteryWithSaver() {
    SYSTEM_POWER_STATUS sps{};
    if (!GetSystemPowerStatus(&sps)) return false;
    return sps.ACLineStatus == 0 && (sps.SystemStatusFlag & 0x01) != 0;
}
static bool IsRemoteSession() {
    return GetSystemMetrics(SM_REMOTESESSION) != 0;
}

// =====================================================================
// Pause state machine
// =====================================================================
static HMONITOR g_lastActiveMonitor = nullptr;
static int      g_activeMonitorMissCount = 0;

static void PauseOnePlayer(WallpaperInstance& wp) {
    if (!wp.player) return;
    g_mpv.set_property_string(wp.player, "pause", "yes");
}
static void ResumeOnePlayer(WallpaperInstance& wp) {
    if (!wp.player) return;
    g_mpv.set_property_string(wp.player, "pause", "no");
    UpdateVlcDisplay(wp.hwnd, wp.player);
}

static void UpdatePauseState() {
    HMONITOR detected = GetForegroundMonitorWithActiveWindow();
    if (detected != nullptr) {
        g_lastActiveMonitor = detected;
        g_activeMonitorMissCount = 0;
    }
    else {
        g_activeMonitorMissCount++;
        if (g_activeMonitorMissCount >= 4) g_lastActiveMonitor = nullptr;
    }
    HMONITOR activeMonitor = g_lastActiveMonitor;

    bool batterySaver = IsOnBatteryWithSaver();
    bool rdp = IsRemoteSession();
    bool locked = g_sessionLocked.load();
    bool manual = g_manualPaused.load();
    bool lockOverlay = g_lockActive;

    int idleSec = GetIdleSeconds();
    bool idle = (g_idlePauseSeconds > 0 && idleSec >= g_idlePauseSeconds);

    bool globalPause = batterySaver || rdp || locked || idle || manual || lockOverlay;

    for (auto& wp : g_wallpapers) {
        bool onActiveMonitor;
        if (wp.monitor == nullptr) onActiveMonitor = (activeMonitor != nullptr);
        else onActiveMonitor = (activeMonitor && wp.monitor == activeMonitor);

        bool shouldPauseThis = globalPause || onActiveMonitor;
        bool currently = g_monitorPaused.count(wp.hwnd) ? g_monitorPaused[wp.hwnd] : false;
        if (shouldPauseThis == currently) continue;

        if (shouldPauseThis) {
            std::cout << "[PAUSE ] monitor=" << wp.monitor
                << " manual=" << manual
                << " lockOverlay=" << lockOverlay
                << " global=" << globalPause
                << " activeMonitor=" << onActiveMonitor
                << " idleSec=" << idleSec << "\n";
            PauseOnePlayer(wp);
        }
        else {
            std::cout << "[RESUME] monitor=" << wp.monitor
                << " idleSec=" << idleSec << "\n";
            ResumeOnePlayer(wp);
        }
        g_monitorPaused[wp.hwnd] = shouldPauseThis;
    }
}

static const char* GetPauseReason() {
    if (g_manualPaused.load()) return "manual";
    if (g_lockActive) return "lock_overlay";
    if (g_sessionLocked.load()) return "session_locked";
    if (g_idlePauseSeconds > 0 && GetIdleSeconds() >= g_idlePauseSeconds)
        return "idle";
    if (IsOnBatteryWithSaver()) return "battery_saver";
    if (IsRemoteSession()) return "remote_session";
    for (const auto& entry : g_monitorPaused) {
        if (entry.second) return "active_monitor";
    }
    return "none";
}

// =====================================================================
// Settings paths
// =====================================================================
static std::string GetSettingsFilePath() {
    const char* localAppData = std::getenv("LOCALAPPDATA");
    if (!localAppData) return "";
    std::string dir = std::string(localAppData) + "\\Blueprint";
    CreateDirectoryA(dir.c_str(), nullptr);
    return dir + "\\settings.ini";
}

static std::string GetLogFilePath() {
    const char* localAppData = std::getenv("LOCALAPPDATA");
    if (!localAppData) return "";
    std::string dir = std::string(localAppData) + "\\Blueprint";
    CreateDirectoryA(dir.c_str(), nullptr);
    return dir + "\\engine.log";
}

static std::string GetPrayerCachePath() {
    const char* localAppData = std::getenv("LOCALAPPDATA");
    if (!localAppData) return "";
    std::string dir = std::string(localAppData) + "\\Blueprint";
    CreateDirectoryA(dir.c_str(), nullptr);
    return dir + "\\prayer_times.json";
}

// =====================================================================
// Settings load / save
// =====================================================================
static std::string LoadVideoPath() {
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return "";
    char buf[MAX_PATH] = {};
    GetPrivateProfileStringA("Wallpaper", "VideoPath", "", buf, MAX_PATH, ini.c_str());
    return std::string(buf);
}
static void SaveVideoPath(const std::string& path) {
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return;
    WritePrivateProfileStringA("Wallpaper", "VideoPath", path.c_str(), ini.c_str());
}

static int LoadIdlePauseSeconds() {
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return 0;
    char buf[32] = {};
    GetPrivateProfileStringA("Performance", "IdlePauseSeconds", "", buf, 32, ini.c_str());
    if (buf[0] == '\0') {
        WritePrivateProfileStringA("Performance", "IdlePauseSeconds", "0", ini.c_str());
        return 0;
    }
    return std::atoi(buf);
}

static std::vector<std::string> SplitList(const std::string& s) {
    std::vector<std::string> out;
    size_t start = 0;
    while (start < s.size()) {
        size_t end = s.find(';', start);
        if (end == std::string::npos) end = s.size();
        std::string item = s.substr(start, end - start);
        size_t a = item.find_first_not_of(" \t");
        size_t b = item.find_last_not_of(" \t");
        if (a != std::string::npos && b != std::string::npos)
            out.push_back(item.substr(a, b - a + 1));
        start = end + 1;
    }
    return out;
}

static SlideshowConfig LoadSlideshowConfig() {
    SlideshowConfig cfg;
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return cfg;
    char buf[32] = {};
    GetPrivateProfileStringA("Slideshow", "Enabled", "0", buf, 32, ini.c_str());
    cfg.enabled = (std::atoi(buf) != 0);
    GetPrivateProfileStringA("Slideshow", "IntervalMinutes", "10", buf, 32, ini.c_str());
    cfg.intervalMinutes = std::atoi(buf);
    if (cfg.intervalMinutes <= 0) cfg.intervalMinutes = 10;
    GetPrivateProfileStringA("Slideshow", "Random", "0", buf, 32, ini.c_str());
    cfg.random = (std::atoi(buf) != 0);
    char listBuf[8192] = {};
    GetPrivateProfileStringA("Slideshow", "Playlist", "", listBuf, 8192, ini.c_str());
    cfg.playlist = SplitList(listBuf);
    return cfg;
}

static DayNightConfig LoadDayNightConfigFromSection(const char* section) {
    DayNightConfig cfg;
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return cfg;

    char buf[MAX_PATH] = {};
    GetPrivateProfileStringA(section, "Enabled", "0", buf, 32, ini.c_str());
    cfg.enabled = (std::atoi(buf) != 0);

    GetPrivateProfileStringA(section, "Mode", "sunrise", buf, 32, ini.c_str());
    cfg.mode = buf;

    GetPrivateProfileStringA(section, "DayVideo", "", buf, MAX_PATH, ini.c_str());
    cfg.dayVideo = buf;

    GetPrivateProfileStringA(section, "NightVideo", "", buf, MAX_PATH, ini.c_str());
    cfg.nightVideo = buf;

    GetPrivateProfileStringA(section, "ManualDayStart", "07:00", buf, 32, ini.c_str());
    cfg.manualDayStart = buf;

    GetPrivateProfileStringA(section, "ManualNightStart", "19:00", buf, 32, ini.c_str());
    cfg.manualNightStart = buf;

    GetPrivateProfileStringA(section, "DuhaOffsetMinutes", "20", buf, 32, ini.c_str());
    cfg.duhaOffsetMinutes = std::atoi(buf);

    // Latitude / longitude / prayer method only stored in [DayNight].
    // [LockDayNight] inherits these at runtime from the desktop config.
    if (strcmp(section, "DayNight") == 0) {
        GetPrivateProfileStringA(section, "Latitude", "0", buf, 32, ini.c_str());
        cfg.latitude = std::atof(buf);
        GetPrivateProfileStringA(section, "Longitude", "0", buf, 32, ini.c_str());
        cfg.longitude = std::atof(buf);
        GetPrivateProfileStringA(section, "PrayerMethod", "3", buf, 32, ini.c_str());
        cfg.prayerMethod = std::atoi(buf);
    }

    return cfg;
}

static DayNightConfig LoadDayNightConfig() {
    return LoadDayNightConfigFromSection("DayNight");
}

static DayNightConfig LoadLockDayNightConfig() {
    DayNightConfig cfg = LoadDayNightConfigFromSection("LockDayNight");
    // Location is shared with the desktop config.
    cfg.latitude = g_dayNight.latitude;
    cfg.longitude = g_dayNight.longitude;
    cfg.prayerMethod = g_dayNight.prayerMethod;
    return cfg;
}

static void SaveLockDayNightConfig() {
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return;
    auto w = [&](const char* key, const std::string& val) {
        WritePrivateProfileStringA("LockDayNight", key, val.c_str(), ini.c_str());
        };
    auto wi = [&](const char* key, int val) {
        WritePrivateProfileStringA("LockDayNight", key,
            std::to_string(val).c_str(), ini.c_str());
        };

    w("Enabled", g_lockDayNight.enabled ? "1" : "0");
    w("Mode", g_lockDayNight.mode);
    w("DayVideo", g_lockDayNight.dayVideo);
    w("NightVideo", g_lockDayNight.nightVideo);
    w("ManualDayStart", g_lockDayNight.manualDayStart);
    w("ManualNightStart", g_lockDayNight.manualNightStart);
    wi("DuhaOffsetMinutes", g_lockDayNight.duhaOffsetMinutes);
}
static std::string LoadQualityMode() {
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return "balanced";
    char buf[32] = {};
    GetPrivateProfileStringA("Performance", "Quality", "balanced", buf, 32, ini.c_str());
    std::string m = buf;
    if (m != "fast" && m != "balanced" && m != "high" && m != "vsr")
        m = "balanced";
    return m;
}

static void LoadLockConfig() {
    std::string ini = GetSettingsFilePath();
    if (ini.empty()) return;

    char buf[MAX_PATH] = {};

    GetPrivateProfileStringA("Lock", "Enabled", "0", buf, 32, ini.c_str());
    g_lockEnabled = (std::atoi(buf) != 0);

    GetPrivateProfileStringA("Lock", "IdleSeconds", "300", buf, 32, ini.c_str());
    g_lockIdleSeconds = std::atoi(buf);
    if (g_lockIdleSeconds < 10) g_lockIdleSeconds = 10;

    GetPrivateProfileStringA("Lock", "Mode", "screensaver", buf, 32, ini.c_str());
    g_lockMode = buf;
    if (g_lockMode != "screensaver" && g_lockMode != "password")
        g_lockMode = "screensaver";

    GetPrivateProfileStringA("Lock", "PasswordHash", "", buf, 256, ini.c_str());
    g_lockPasswordHash = buf;

    // Sanity: password mode with no hash is unreachable — the user
    // would be stuck behind a prompt they can never satisfy. Coerce
    // to screensaver and write it back so the INI reflects reality.
    if (g_lockMode == "password" && g_lockPasswordHash.empty()) {
        std::cout << "[LOCK ] Mode=password with empty hash; "
            "coercing to screensaver\n";
        g_lockMode = "screensaver";
        WritePrivateProfileStringA("Lock", "Mode",
            "screensaver", ini.c_str());
    }

    GetPrivateProfileStringA("Lock", "UseDayNightVideos", "1", buf, 32, ini.c_str());
    g_lockUseDayNightVideos = (std::atoi(buf) != 0);

    GetPrivateProfileStringA("Lock", "DayVideo", "", buf, MAX_PATH, ini.c_str());
    g_lockDayVideo = buf;

    GetPrivateProfileStringA("Lock", "NightVideo", "", buf, MAX_PATH, ini.c_str());
    g_lockNightVideo = buf;

    // ── New (Batch 7) ──
    GetPrivateProfileStringA("Lock", "PauseOnLock", "0", buf, 32, ini.c_str());
    g_lockPauseOnLock = (std::atoi(buf) != 0);

    GetPrivateProfileStringA("Lock", "DimWhenLocked", "1", buf, 32, ini.c_str());
    g_lockDimWhenLocked = (std::atoi(buf) != 0);

    GetPrivateProfileStringA("Lock", "ShowClock", "1", buf, 32, ini.c_str());
    g_lockShowClock = (std::atoi(buf) != 0);

    GetPrivateProfileStringA("Lock", "FillMode", "fill", buf, 32, ini.c_str());
    g_lockFillMode = buf;

    GetPrivateProfileStringA("Lock", "Fps", "30", buf, 32, ini.c_str());
    g_lockFps = std::atoi(buf);

    GetPrivateProfileStringA("Lock", "WallpaperOverride", "", buf, MAX_PATH, ini.c_str());
    g_lockWallpaperPath = buf;

    g_lockMonitorWallpaperPaths.clear();
    for (int i = 0; i < 8; i++) {
        std::string section = "LockMonitor" + std::to_string(i);
        char mbuf[MAX_PATH] = {};
        GetPrivateProfileStringA(section.c_str(), "VideoPath", "",
            mbuf, MAX_PATH, ini.c_str());
        if (mbuf[0] != '\0' && FileExists(mbuf)) {
            g_lockMonitorWallpaperPaths[i] = mbuf;
        }
    }

    // Shuffle. Accepts both the legacy "0"/"1" values and the new
    // "off"/"random"/"weighted"/"favorites_only" strings so existing
    // profiles don't lose their setting.
    {
        char modeBuf[32] = {};
        GetPrivateProfileStringA("Performance", "ShuffleMode", "off",
            modeBuf, 32, ini.c_str());
        std::string mode = modeBuf;
        if (mode == "0" || mode.empty()) mode = "off";
        else if (mode == "1") mode = "random";
        if (mode != "off" && mode != "random"
            && mode != "weighted" && mode != "favorites_only") {
            mode = "off";
        }
        g_shuffleMode = mode;
        g_slideshow.random = (mode != "off");
    }

    // Favorites list, persisted so an engine restart preserves
    // weighted-shuffle behavior even before the panel connects.
    {
        char favBuf[16384] = {};
        GetPrivateProfileStringA("Performance", "Favorites", "",
            favBuf, 16384, ini.c_str());
        g_favoritePaths.clear();
        for (auto& f : SplitList(favBuf)) {
            if (!f.empty()) g_favoritePaths.insert(f);
        }
    }
}

// =====================================================================
// Auto-start
// =====================================================================
static std::wstring GetCurrentExePath() {
    wchar_t buf[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    return std::wstring(buf);
}

static bool IsAutoStartEnabled() {
    HKEY hKey = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKeyPath, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return false;
    wchar_t buf[MAX_PATH * 2] = {};
    DWORD size = sizeof(buf);
    DWORD type = 0;
    LONG r = RegQueryValueExW(hKey, kRunValueName, nullptr, &type,
        reinterpret_cast<LPBYTE>(buf), &size);
    RegCloseKey(hKey);
    return (r == ERROR_SUCCESS && type == REG_SZ);
}

static bool SetAutoStart(bool enable) {
    HKEY hKey = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKeyPath, 0, KEY_SET_VALUE, &hKey)
        != ERROR_SUCCESS) {
        std::cout << "[AUTOSTART] Could not open Run key. Error=" << GetLastError() << "\n";
        return false;
    }
    bool ok = false;
    if (enable) {
        std::wstring cmd = L"\"" + GetCurrentExePath() + L"\"";
        LONG r = RegSetValueExW(hKey, kRunValueName, 0, REG_SZ,
            reinterpret_cast<const BYTE*>(cmd.c_str()),
            (DWORD)((cmd.size() + 1) * sizeof(wchar_t)));
        ok = (r == ERROR_SUCCESS);
    }
    else {
        LONG r = RegDeleteValueW(hKey, kRunValueName);
        ok = (r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND);
    }
    RegCloseKey(hKey);
    std::cout << "[AUTOSTART] " << (enable ? "Enabled" : "Disabled")
        << (ok ? "" : " (FAILED)") << "\n";
    return ok;
}

// =====================================================================
// Solar calculation
// =====================================================================
static void CalculateSunTimes(double lat, double lon,
    double& sunriseHour, double& sunsetHour) {
    const double PI = 3.14159265358979323846;

    SYSTEMTIME st;
    GetLocalTime(&st);
    int days[] = { 0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    if ((st.wYear % 4 == 0 && st.wYear % 100 != 0) || st.wYear % 400 == 0)
        days[2] = 29;
    int doy = 0;
    for (int m = 1; m < st.wMonth; m++) doy += days[m];
    doy += st.wDay;

    double decl = 23.44 * sin((360.0 / 365.0) * (doy - 81) * PI / 180.0);
    double latRad = lat * PI / 180.0;
    double declRad = decl * PI / 180.0;
    double zenithRad = 90.833 * PI / 180.0;
    double cosH = (cos(zenithRad) - sin(latRad) * sin(declRad))
        / (cos(latRad) * cos(declRad));

    if (cosH > 1.0 || cosH < -1.0) { sunriseHour = sunsetHour = -1.0; return; }

    double H = acos(cosH) * 180.0 / PI;
    double Hh = H / 15.0;

    TIME_ZONE_INFORMATION tz;
    DWORD tzResult = GetTimeZoneInformation(&tz);
    double tzOffsetHours;
    if (tzResult == TIME_ZONE_ID_DAYLIGHT)
        tzOffsetHours = -(tz.Bias + tz.DaylightBias) / 60.0;
    else
        tzOffsetHours = -tz.Bias / 60.0;

    double solarNoonLocal = 12.0 - lon / 15.0 + tzOffsetHours;
    sunriseHour = solarNoonLocal - Hh;
    sunsetHour = solarNoonLocal + Hh;

    while (sunriseHour < 0.0) sunriseHour += 24.0;
    while (sunriseHour >= 24.0) sunriseHour -= 24.0;
    while (sunsetHour < 0.0) sunsetHour += 24.0;
    while (sunsetHour >= 24.0) sunsetHour -= 24.0;
}

static bool IsCurrentlyDay_Sunrise() {
    if (g_cachedSunrise < 0 || g_cachedSunset < 0) return true;
    SYSTEMTIME st;
    GetLocalTime(&st);
    double nowHour = st.wHour + st.wMinute / 60.0 + st.wSecond / 3600.0;
    return nowHour >= g_cachedSunrise && nowHour < g_cachedSunset;
}

// =====================================================================
// Prayer times — cache + HTTP
// =====================================================================
static std::string TodayDateString() {
    SYSTEMTIME st;
    GetLocalTime(&st);
    char buf[16];
    sprintf_s(buf, "%02d-%02d-%04d", st.wDay, st.wMonth, st.wYear);
    return buf;
}

static std::string DateStringOffsetDays(int offset) {
    time_t now = time(nullptr);
    now += (time_t)offset * 86400;
    struct tm gmt;
    gmtime_s(&gmt, &now);
    // We only need a day/month/year tuple; using gmtime is fine because
    // we just need a date label to match against, not the wall clock.
    char buf[16];
    sprintf_s(buf, "%02d-%02d-%04d", gmt.tm_mday, gmt.tm_mon + 1, gmt.tm_year + 1900);
    return buf;
}

static std::string HttpGet(const std::wstring& host, const std::wstring& path) {
    std::string result;
    HINTERNET hSession = WinHttpOpen(L"StrataWallpaper/1.0",
        WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME,
        WINHTTP_NO_PROXY_BYPASS, 0);
    if (!hSession) return result;

    HINTERNET hConnect = WinHttpConnect(hSession, host.c_str(),
        INTERNET_DEFAULT_HTTPS_PORT, 0);
    if (!hConnect) { WinHttpCloseHandle(hSession); return result; }

    HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"GET", path.c_str(),
        nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
        WINHTTP_FLAG_SECURE);
    if (!hRequest) {
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return result;
    }

    WinHttpSetTimeouts(hRequest, 5000, 5000, 5000, 5000);

    if (!WinHttpSendRequest(hRequest, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
        WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(hRequest, nullptr)) {
        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return result;
    }

    DWORD dwSize = 0;
    do {
        if (!WinHttpQueryDataAvailable(hRequest, &dwSize)) break;
        if (dwSize == 0) break;
        std::vector<char> buf(dwSize + 1);
        DWORD dwRead = 0;
        if (!WinHttpReadData(hRequest, buf.data(), dwSize, &dwRead)) break;
        buf[dwRead] = '\0';
        result.append(buf.data(), dwRead);
    } while (dwSize > 0);

    WinHttpCloseHandle(hRequest);
    WinHttpCloseHandle(hConnect);
    WinHttpCloseHandle(hSession);
    return result;
}

static bool FetchOneDay(const std::string& dateLabel,
    double lat, double lng, int method,
    std::string& outSunrise, std::string& outMaghrib) {
    wchar_t path[512];
    swprintf_s(path,
        L"/v1/timings/%S?latitude=%.6f&longitude=%.6f&method=%d",
        dateLabel.c_str(), lat, lng, method);

    std::string resp = HttpGet(L"api.aladhan.com", path);
    if (resp.empty()) return false;

    try {
        json j = json::parse(resp);
        if (!j.contains("data") || !j["data"].contains("timings")) return false;
        auto& t = j["data"]["timings"];

        auto clean = [](std::string s) -> std::string {
            // Strip " (TZ)" suffix if present; keep "HH:MM".
            if (s.size() >= 5) return s.substr(0, 5);
            return s;
            };

        std::string sunrise = clean(t.value("Sunrise", std::string("")));
        std::string maghrib = clean(t.value("Maghrib", std::string("")));

        if (sunrise.size() != 5 || maghrib.size() != 5) return false;
        outSunrise = sunrise;
        outMaghrib = maghrib;
        return true;
    }
    catch (...) {
        return false;
    }
}

static bool LoadPrayerCache() {
    std::string path = GetPrayerCachePath();
    if (path.empty()) return false;
    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "rb") != 0 || !f) return false;

    std::string data;
    char buf[4096];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) data.append(buf, n);
    fclose(f);

    try {
        json j = json::parse(data);
        PrayerCache c;
        c.fetched_at = j.value("fetched_at", (long long)0);
        c.method = j.value("method", 3);
        c.latitude = j.value("latitude", 0.0);
        c.longitude = j.value("longitude", 0.0);
        if (j.contains("days")) {
            for (auto& d : j["days"]) {
                PrayerDay pd;
                pd.date = d.value("date", std::string(""));
                pd.sunrise = d.value("sunrise", std::string(""));
                pd.maghrib = d.value("maghrib", std::string(""));
                c.days.push_back(pd);
            }
        }
        {
            std::lock_guard<std::mutex> lock(g_prayerMutex);
            g_prayerCache = c;
        }
        std::cout << "[PRAYER] cache loaded: " << c.days.size()
            << " day(s), fetched_at=" << c.fetched_at << "\n";
        return !c.days.empty();
    }
    catch (...) {
        std::cout << "[PRAYER] cache parse failed\n";
        return false;
    }
}

static void SavePrayerCache() {
    std::string path = GetPrayerCachePath();
    if (path.empty()) return;

    PrayerCache cache;
    {
        std::lock_guard<std::mutex> lock(g_prayerMutex);
        cache = g_prayerCache;
    }

    json j;
    j["fetched_at"] = (long long)cache.fetched_at;
    j["method"] = cache.method;
    j["latitude"] = cache.latitude;
    j["longitude"] = cache.longitude;
    j["days"] = json::array();
    for (auto& d : cache.days) {
        json e;
        e["date"] = d.date;
        e["sunrise"] = d.sunrise;
        e["maghrib"] = d.maghrib;
        j["days"].push_back(e);
    }

    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "wb") != 0 || !f) return;
    std::string s = j.dump(2);
    fwrite(s.data(), 1, s.size(), f);
    fclose(f);
}

// Background thread body. Posts WM_APP_PRAYER_FETCH_DONE when finished.
static void PrayerFetchThreadProc(DayNightConfig config) {
    PrayerCache fresh;
    fresh.fetched_at = time(nullptr);
    fresh.method = config.prayerMethod;
    fresh.latitude = config.latitude;
    fresh.longitude = config.longitude;

    int okCount = 0;
    for (int i = 0; i < 7; i++) {
        if (g_prayerFetchCancel.load()) break;
        std::string label = DateStringOffsetDays(i);
        std::string sunrise, maghrib;
        if (g_prayerFetchCancel.load()) break;
        if (FetchOneDay(label, fresh.latitude, fresh.longitude,
            fresh.method, sunrise, maghrib)) {
            PrayerDay pd;
            pd.date = label;
            pd.sunrise = sunrise;
            pd.maghrib = maghrib;
            fresh.days.push_back(pd);
            okCount++;
        }
        else {
            std::cout << "[PRAYER] fetch failed for " << label << "\n";
        }
    }

    if (okCount > 0 && !g_prayerFetchCancel.load()) {
        {
            std::lock_guard<std::mutex> lock(g_prayerMutex);
            g_prayerCache = fresh;
        }
        SavePrayerCache();
        std::cout << "[PRAYER] fetch OK: " << okCount << "/7 day(s)\n";
        g_prayerFetchDone.store(true);
    }
    else {
        std::cout << "[PRAYER] fetch returned nothing; keeping old cache\n";
    }
    g_prayerFetchInProgress.store(false);

    if (!g_prayerFetchCancel.load() && g_hHiddenWnd) {
        PostMessageW(g_hHiddenWnd, WM_APP_PRAYER_FETCH_DONE, 0, 0);
    }
}

static void FetchPrayerTimesAsync() {
    if (g_prayerFetchInProgress.exchange(true)) return;
    if (g_prayerFetchThread.joinable())
        g_prayerFetchThread.join();
    g_prayerFetchCancel.store(false);
    DayNightConfig config = g_dayNight;
    g_prayerFetchThread = std::thread(PrayerFetchThreadProc, std::move(config));
}

static void StopPrayerFetch() {
    g_prayerFetchCancel.store(true);
    if (g_prayerFetchThread.joinable())
        g_prayerFetchThread.join();
}

static bool GetPrayerTimesForToday(std::string& sunrise, std::string& maghrib) {
    std::string today = TodayDateString();
    PrayerCache cache = GetPrayerCacheSnapshot();
    for (auto& d : cache.days) {
        if (d.date == today) {
            sunrise = d.sunrise;
            maghrib = d.maghrib;
            return true;
        }
    }
    return false;
}

static bool IsCurrentlyDay_ManualFor(const DayNightConfig& cfg) {
    int dh, dm, nh, nm;
    if (!ParseTimeHHMM(cfg.manualDayStart, dh, dm)) return true;
    if (!ParseTimeHHMM(cfg.manualNightStart, nh, nm)) return true;

    SYSTEMTIME st;
    GetLocalTime(&st);
    int nowTotal = st.wHour * 60 + st.wMinute;
    int dayStart = dh * 60 + dm;
    int nightStart = nh * 60 + nm;

    if (dayStart <= nightStart)
        return nowTotal >= dayStart && nowTotal < nightStart;
    else
        return nowTotal >= dayStart || nowTotal < nightStart;
}

static bool IsCurrentlyDay_PrayerFor(const DayNightConfig& cfg) {
    std::string sunrise, maghrib;
    if (!GetPrayerTimesForToday(sunrise, maghrib)) return true;

    int sh, sm, mh, mm;
    if (!ParseTimeHHMM(sunrise, sh, sm)) return true;
    if (!ParseTimeHHMM(maghrib, mh, mm)) return true;

    int duhaTotal = sh * 60 + sm + cfg.duhaOffsetMinutes;
    int maghribTotal = mh * 60 + mm;

    SYSTEMTIME st;
    GetLocalTime(&st);
    int nowTotal = st.wHour * 60 + st.wMinute;

    return nowTotal >= duhaTotal && nowTotal < maghribTotal;
}

static bool IsCurrentlyDayFor(const DayNightConfig& cfg) {
    if (cfg.mode == "manual")  return IsCurrentlyDay_ManualFor(cfg);
    if (cfg.mode == "prayer")  return IsCurrentlyDay_PrayerFor(cfg);
    return IsCurrentlyDay_Sunrise();   // shared: uses g_cachedSunrise/g_cachedSunset
}

static bool IsCurrentlyDay() {
    return IsCurrentlyDayFor(g_dayNight);
}

// =====================================================================
// File picker
// =====================================================================
static std::string PromptForVideoFile() {
    OPENFILENAMEA ofn = {};
    char fileName[MAX_PATH] = {};
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = GetConsoleWindow();
    ofn.lpstrFilter = "Video Files\0*.mp4;*.mkv;*.webm;*.avi;*.mov;*.wmv\0All Files\0*.*\0";
    ofn.lpstrFile = fileName;
    ofn.nMaxFile = MAX_PATH;
    ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    ofn.lpstrTitle = "Choose a video for your live wallpaper";
    if (GetOpenFileNameA(&ofn)) return std::string(fileName);
    return "";
}

// =====================================================================
// Video path resolution
// =====================================================================
static std::string GetVideoPathForMonitor(int index, bool& hasOverride) {
    hasOverride = false;

    auto mit = g_monitorForcePaths.find(index);
    if (mit != g_monitorForcePaths.end() && !mit->second.empty()) {
        if (FileExists(mit->second)) {
            hasOverride = true;
            return mit->second;
        }
        std::cout << "[WARN  ] Monitor " << index
            << " override missing on disk: " << mit->second << "\n";
    }

    if (g_displayMode != "individual" && !g_forceVideoPath.empty()) {
        if (FileExists(g_forceVideoPath)) {
            hasOverride = true;
            return g_forceVideoPath;
        }
        std::cout << "[WARN  ] Global override missing on disk: "
            << g_forceVideoPath << "\n";
    }

    std::string ini = GetSettingsFilePath();
    if (!ini.empty()) {
        std::string section = "Monitor" + std::to_string(index);
        char buf[MAX_PATH] = {};
        GetPrivateProfileStringA(section.c_str(), "VideoPath", "",
            buf, MAX_PATH, ini.c_str());
        if (buf[0] != '\0') {
            if (FileExists(buf)) { hasOverride = true; return std::string(buf); }
            std::cout << "[WARN  ] Monitor " << index
                << " INI path missing on disk: " << buf << "\n";
        }
    }

    if (g_dayNight.enabled) {
        std::string p = g_dayNight.isDay
            ? g_dayNight.dayVideo : g_dayNight.nightVideo;
        if (FileExists(p)) return p;
    }

    if (g_slideshow.enabled && !g_slideshow.playlist.empty()) {
        int idx = 0;
        if (g_slideshow.random) idx = rand() % (int)g_slideshow.playlist.size();
        return g_slideshow.playlist[idx];
    }

    return LoadVideoPath();
}

// =====================================================================
// Video switching
// =====================================================================
static void SwitchVideoOnMonitor(WallpaperInstance& wp, const std::string& path) {
    if (!wp.player || path.empty()) return;
    if (path == wp.currentVideoPath) return;

    const char* cmd[] = { "loadfile", path.c_str(), "replace", nullptr };
    g_mpv.command(wp.player, cmd);

    wp.currentVideoPath = path;
    wp.warmupUntil = GetTickCount64() + 5000;

    bool paused = g_monitorPaused.count(wp.hwnd) ? g_monitorPaused[wp.hwnd] : false;
    if (paused) {
        g_mpv.set_property_string(wp.player, "pause", "yes");
    }
}

// =====================================================================
// Slideshow & Day/Night
// =====================================================================
static bool IsFavoritePath(const std::string& path) {
    for (auto& f : g_favoritePaths) {
        if (_stricmp(f.c_str(), path.c_str()) == 0) return true;
    }
    return false;
}

// Favorites get weight 3, non-favorites weight 1. Tries up to 6 times
// to avoid returning skipPath; gives up and returns any item rather
// than looping forever on a single-item pool.
static std::string PickWeightedNoRepeat(const std::vector<std::string>& pool,
    const std::string& skipPath) {
    if (pool.empty()) return "";

    int totalWeight = 0;
    std::vector<int> weights;
    weights.reserve(pool.size());
    for (const auto& p : pool) {
        int w = IsFavoritePath(p) ? 3 : 1;
        weights.push_back(w);
        totalWeight += w;
    }
    if (totalWeight <= 0) return pool[rand() % pool.size()];

    for (int attempt = 0; attempt < 6; attempt++) {
        int roll = rand() % totalWeight;
        int acc = 0;
        for (size_t i = 0; i < pool.size(); i++) {
            acc += weights[i];
            if (roll < acc) {
                if (skipPath.empty() ||
                    _stricmp(pool[i].c_str(), skipPath.c_str()) != 0) {
                    return pool[i];
                }
                break;
            }
        }
    }
    return pool[rand() % pool.size()];
}

static std::string PickUniformNoRepeat(const std::vector<std::string>& pool,
    const std::string& skipPath) {
    if (pool.empty()) return "";
    if (pool.size() == 1) return pool[0];
    for (int attempt = 0; attempt < 8; attempt++) {
        const std::string& cand = pool[rand() % pool.size()];
        if (skipPath.empty() ||
            _stricmp(cand.c_str(), skipPath.c_str()) != 0) {
            return cand;
        }
    }
    return pool[rand() % pool.size()];
}

static void UpdateSlideshow() {
    if (!g_slideshow.enabled) return;

    const std::vector<std::string>& pool =
        !g_libraryFiles.empty() ? g_libraryFiles : g_slideshow.playlist;
    if (pool.empty()) return;

    ULONGLONG now = GetTickCount64();
    ULONGLONG intervalMs = (ULONGLONG)g_slideshow.intervalMinutes * 60ULL * 1000ULL;

    if (g_lastSlideshowSwitch == 0) { g_lastSlideshowSwitch = now; return; }
    if (now - g_lastSlideshowSwitch < intervalMs) return;
    g_lastSlideshowSwitch = now;

    // favorites_only pool. If empty, degrade to uniform rather than
    // freezing the slideshow. The panel is expected to warn the user
    // and offer to disable the filter (Section 9 H2).
    std::vector<std::string> favoritesOnly;
    if (g_shuffleMode == "favorites_only") {
        for (const auto& p : pool) {
            if (IsFavoritePath(p)) favoritesOnly.push_back(p);
        }
        if (favoritesOnly.empty()) {
            std::cout << "[SLIDESHOW] favorites_only requested but no favorites; "
                "falling back to uniform rotation\n";
        }
    }

    std::cout << "[SLIDESHOW] rotating (mode=" << g_shuffleMode << ")...\n";
    for (auto& wp : g_wallpapers) {
        if (wp.hasOverride) continue;

        std::string next;
        if (g_shuffleMode == "weighted") {
            next = PickWeightedNoRepeat(pool, wp.currentVideoPath);
        }
        else if (g_shuffleMode == "favorites_only") {
            next = favoritesOnly.empty()
                ? PickUniformNoRepeat(pool, wp.currentVideoPath)
                : PickUniformNoRepeat(favoritesOnly, wp.currentVideoPath);
        }
        else if (g_shuffleMode == "random") {
            next = PickUniformNoRepeat(pool, wp.currentVideoPath);
        }
        else {
            next = pool[g_nextPlaylistIndex % pool.size()];
        }

        if (next.empty() || !FileExists(next)) continue;
        SwitchVideoOnMonitor(wp, next);
        std::cout << "  -> monitor=" << wp.monitor << " to " << next << "\n";
    }
    if (g_shuffleMode == "off") g_nextPlaylistIndex++;
}
static void RefreshLockOverlayVideo() {
    if (!g_lockActive) return;
    for (auto& lo : g_lockOverlays) {
        std::string path;
        if (GetLockVideoPath(path, lo.index)) {
            std::cout << "[LOCK ] Overlay loading video: " << path << "\n";
            const char* cmd[] = { "loadfile", path.c_str(), nullptr };
            g_mpv.command(lo.player, cmd);
        }
        else {
            std::cout << "[LOCK ] Overlay failed to resolve any video.\n";
        }
    }
}

static void UpdateDayNight() {
    if (!g_dayNight.enabled) return;

    ULONGLONG now = GetTickCount64();

    if (g_dayNight.mode == "sunrise" || g_dayNight.mode == "prayer") {
        if (g_cachedSunrise < 0 || (now - g_lastSunCalcTick) >= kSunCalcIntervalMs) {
            CalculateSunTimes(g_dayNight.latitude, g_dayNight.longitude,
                g_cachedSunrise, g_cachedSunset);
            g_lastSunCalcTick = now;
        }
    }

    bool newIsDay = IsCurrentlyDay();
    if (newIsDay == g_dayNight.isDay) return;
    g_dayNight.isDay = newIsDay;

    std::cout << "[DAYNIGHT] mode=" << g_dayNight.mode
        << " transition to " << (newIsDay ? "DAY" : "NIGHT") << "\n";

    // 1. Update Desktop Wallpapers
    for (auto& wp : g_wallpapers) {
        wp.hasOverride = false;
        std::string path = newIsDay ? g_dayNight.dayVideo : g_dayNight.nightVideo;
        if (!FileExists(path)) continue;
        SwitchVideoOnMonitor(wp, path);
    }

    // 2. Update Lock Screen Overlay if it is currently visible.
    //    Lock D/N video priority is resolved inside GetLockVideoPath.
    RefreshLockOverlayVideo();
}
// =====================================================================
// Lock overlay implementation
// =====================================================================
//
// Clock rendering: we intentionally do NOT use mpv's OSD. Two reasons
// learned the hard way:
//   1. mpv's OSD text renderer doesn't reliably process ASS override
//      tags — "{\fs120}..." was appearing literally on screen.
//   2. mpv emits a seek/pause OSD every time the engine pauses or
//      resumes the wallpaper, which happens whenever the mouse moves
//      over the active monitor. That readout ("00:00:01 / 00:00:12")
//      then stuck around despite osd-duration.
// Drawtext is a video filter — text is burned into the frame, immune
// to OSD quirks, and reproducible across mpv builds.
// Tracks the last minute value we rendered, so the WM_TIMER-driven
// call can no-op between minute changes.
static int g_lockClockLastMinute = -1;

// Renders the lock clock via mpv's osd-overlay command. This is not
// the same as show-text: osd-overlay hands the string directly to
// libass, which renders the ASS override tags correctly. show-text
// goes through mpv's own OSD text renderer, which we discovered
// passes ASS tags through as literal text.
//
// We use osd-overlay instead of drawtext because drawtext requires
// FFmpeg's libavfilter to be built with --enable-libfreetype, and
// many prebuilt libmpv-2.dll bundles (including the one we're using)
// omit it. osd-overlay depends only on libass, which mpv hard-depends on.
static void UpdateLockClockFilters(mpv_handle* player, bool firstTime) {
    if (!player) return;

    if (!g_lockShowClock) {
        // Clear any previous overlay.
        const char* rm[] = {
            "osd-overlay", "1", "none", "", nullptr
        };
        g_mpv.command(player, rm);
        return;
    }

    SYSTEMTIME st;
    GetLocalTime(&st);

    // Only rewrite when the minute changes. osd-overlay with the same
    // id just updates the existing overlay, so a no-op here is fine.
    if (!firstTime && st.wMinute == g_lockClockLastMinute) return;
    g_lockClockLastMinute = st.wMinute;

    static const char* kDays[] = {
        "Sunday","Monday","Tuesday","Wednesday",
        "Thursday","Friday","Saturday"
    };
    static const char* kMonths[] = {
        "","January","February","March","April","May","June",
        "July","August","September","October","November","December"
    };

    // Single ASS event containing both time and date.
    // \an5 = center alignment, \pos centres at (960, 540) which is
    // the middle of the ASS coordinate grid we declare below.
    // \N = hard line break within the event.
    char ass[512];
    sprintf_s(ass,
        "{\\an5\\pos(960,350)}"
        "{\\fs120\\bord4\\1c&HFFFFFF&\\3c&H000000&}"
        "%02d:%02d"
        "{\\fs40\\bord2}"
        "\\N\\N%s, %s %d",
        st.wHour, st.wMinute,
        kDays[st.wDayOfWeek], kMonths[st.wMonth], st.wDay);

    // osd-overlay <id> <format> <data> [<res_x> <res_y>]
    // Format "ass-events" means <data> is a raw ASS event body.
    // 1920x1080 is a safe virtual canvas; ASS scales to the actual
    // video surface.
    const char* cmd[] = {
        "osd-overlay", "1", "ass-events", ass, "1920", "1080", nullptr
    };
    int rc = g_mpv.command(player, cmd);
    std::cout << "[LOCK ] osd-overlay rc=" << rc
        << " ass=\"" << ass << "\"\n";
}

// Password prompt, rendered as a second osd-overlay (id 2). The clock
// uses id 1 so they don't stomp on each other. Same reasoning as the
// clock: GDI drawing is invisible under mpv's D3D11 swap chain, so we
// go through libass via osd-overlay.
//
// Colors in ASS are &HBBGGRR& (BGR order, opposite of RGB). Red is
// &H0000FF&, white &HFFFFFF&, light grey &HDDDDDD&.
// True only when the user has actually configured a password. Having
// mode="password" with an empty hash is an invalid state — it usually
// means the hash was cleared but the mode wasn't. Treat it as
// screensaver so the user isn't stuck behind a prompt they cannot
// satisfy.
static bool LockPasswordRequired() {
    return g_lockMode == "password" && !g_lockPasswordHash.empty();
}

static void UpdateLockPasswordOverlay(mpv_handle* player, bool isPrimaryMonitor) {
    if (!player) return;

    // The password prompt renders only on the primary monitor. Other
    // monitors show just the clock. Input from any monitor still
    // feeds the shared buffer (WM_CHAR is global state), so typing
    // works regardless of which screen has focus — the visible
    // feedback simply lives on the primary.
    if (!isPrimaryMonitor) {
        const char* rm[] = { "osd-overlay", "2", "none", "", nullptr };
        g_mpv.command(player, rm);
        return;
    }

    if (!LockPasswordRequired() || !g_lockPasswordPromptVisible) {
        // Clear the prompt overlay.
        const char* rm[] = { "osd-overlay", "2", "none", "", nullptr };
        g_mpv.command(player, rm);
        return;
    }

    // Build the masked dots. Cap at 40 so the string stays bounded.
    size_t visibleChars = g_lockTypedPassword.size();
    if (visibleChars > 40) visibleChars = 40;
    std::wstring maskChars(visibleChars, L'\x2022');

    std::string maskedUtf8;
    if (g_lockTypedPassword.empty()) {
        maskedUtf8 = "Type, then press Enter";
    }
    else {
        int n = WideCharToMultiByte(CP_UTF8, 0, maskChars.c_str(),
            (int)maskChars.size(), nullptr, 0, nullptr, nullptr);
        maskedUtf8.resize(n);
        WideCharToMultiByte(CP_UTF8, 0, maskChars.c_str(),
            (int)maskChars.size(), &maskedUtf8[0], n, nullptr, nullptr);
    }

    std::string errorLine = Narrow(g_lockPromptMessage.c_str());

    // \an5 = center alignment. \pos centers at virtual canvas (960,620),
    // below the clock which sits at 540.
    // \fs sets font size, \bord outline width, \1c primary fill color.
    // \N = hard line break.
    char ass[768];
    if (errorLine.empty()) {
        sprintf_s(ass,
            "{\\an5\\pos(960,780)}"
            "{\\fs30\\bord2\\1c&HDDDDDD&}Enter password to dismiss"
            "\\N\\N"
            "{\\fs60\\bord4\\1c&HFFFFFF&}%s"
            "\\N"
            "{\\fs20\\bord1\\1c&H888888&}"
            "─────────────────────────────",
            maskedUtf8.c_str());
    }
    else {
        sprintf_s(ass,
            "{\\an5\\pos(960,780)}"
            "{\\fs30\\bord2\\1c&HDDDDDD&}Enter password to dismiss"
            "\\N\\N"
            "{\\fs60\\bord4\\1c&HFFFFFF&}%s"
            "\\N"
            "{\\fs20\\bord1\\1c&H888888&}"
            "─────────────────────────────"
            "\\N\\N"
            "{\\fs26\\bord2\\1c&H0000FF&}%s",
            maskedUtf8.c_str(),
            errorLine.c_str());
    }

    const char* cmd[] = {
        "osd-overlay", "2", "ass-events", ass, "1920", "1080", nullptr
    };
    int rc = g_mpv.command(player, cmd);
    std::cout << "[LOCK ] password overlay rc=" << rc << "\n";
}

// Push the current password state to every lock overlay. Called
// whenever the visible prompt, typed text, or error message changes.
static void RefreshLockPasswordOverlays() {
    for (auto& lo : g_lockOverlays) {
        if (lo.player) UpdateLockPasswordOverlay(lo.player, lo.isPrimary);
    }
}

static bool GetLockVideoPath(std::string& out, int monitorIdx) {
    // BULLETPROOF FIX: Force reload from INI right now, ignoring IPC memory entirely.
    DayNightConfig localDn = LoadLockDayNightConfig();

    bool isDay = true;
    if (localDn.enabled) isDay = IsCurrentlyDayFor(localDn);
    else if (g_dayNight.enabled) isDay = g_dayNight.isDay;

    std::cout << "[LOCK TRACE] Reading from INI directly. Enabled=" << localDn.enabled
        << " Day='" << localDn.dayVideo << "' Night='" << localDn.nightVideo << "'\n";

    // 1. Check Lock Screen Day/Night settings first
    if (localDn.enabled) {
        std::string p = isDay ? localDn.dayVideo : localDn.nightVideo;
        if (FileExists(p)) {
            std::cout << "[LOCK TRACE] -> Found Lock D/N video.\n";
            out = p; return true;
        }
        else {
            std::cout << "[LOCK TRACE] -> Lock D/N file missing or empty.\n";
        }
    }

    // 2. Check Desktop Day/Night settings (if allowed)
    if (g_dayNight.enabled && g_lockUseDayNightVideos) {
        std::string p = isDay ? g_dayNight.dayVideo : g_dayNight.nightVideo;
        if (FileExists(p)) {
            std::cout << "[LOCK TRACE] -> Found Desktop D/N video.\n";
            out = p; return true;
        }
    }

    // 3. Check Per-Monitor static lock wallpaper
    if (monitorIdx >= 0) {
        auto it = g_lockMonitorWallpaperPaths.find(monitorIdx);
        if (it != g_lockMonitorWallpaperPaths.end() && !it->second.empty() && FileExists(it->second)) {
            std::cout << "[LOCK TRACE] -> Found per-monitor static wallpaper.\n";
            out = it->second; return true;
        }
    }

    // 4. Check Global static lock wallpaper
    if (!g_lockWallpaperPath.empty() && FileExists(g_lockWallpaperPath)) {
        std::cout << "[LOCK TRACE] -> Found global static wallpaper.\n";
        out = g_lockWallpaperPath; return true;
    }

    std::cout << "[LOCK TRACE] -> No lock wallpaper found at all.\n";
    return false;
}

static void DrawLockClock(HDC hdc, RECT rc) {
    SYSTEMTIME st;
    GetLocalTime(&st);

    wchar_t timeBuf[16];
    swprintf_s(timeBuf, L"%02d:%02d", st.wHour, st.wMinute);

    wchar_t dateBuf[64];
    static const wchar_t* kDays[] = {
        L"Sunday", L"Monday", L"Tuesday", L"Wednesday",
        L"Thursday", L"Friday", L"Saturday"
    };
    static const wchar_t* kMonths[] = {
        L"", L"January", L"February", L"March", L"April", L"May", L"June",
        L"July", L"August", L"September", L"October", L"November", L"December"
    };
    swprintf_s(dateBuf, L"%s, %s %d, %d",
        kDays[st.wDayOfWeek],
        kMonths[st.wMonth], st.wDay, st.wYear);

    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, RGB(255, 255, 255));

    int w = rc.right - rc.left;
    int h = rc.bottom - rc.top;

    // Big time font, scaled to monitor.
    int timeH = max(48, h / 6);
    HFONT hBig = CreateFontW(-timeH, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    HFONT hOld = (HFONT)SelectObject(hdc, hBig);

    // Shadow for readability on any video.
    SetTextColor(hdc, RGB(0, 0, 0));
    RECT rTimeShadow = rc;
    rTimeShadow.left += 3; rTimeShadow.top += 3;
    DrawTextW(hdc, timeBuf, -1, &rTimeShadow,
        DT_CENTER | DT_VCENTER | DT_SINGLELINE);

    SetTextColor(hdc, RGB(255, 255, 255));
    DrawTextW(hdc, timeBuf, -1, &rc,
        DT_CENTER | DT_VCENTER | DT_SINGLELINE);

    // Date below the time
    int dateH = max(18, h / 28);
    HFONT hDate = CreateFontW(-dateH, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    SelectObject(hdc, hDate);

    RECT rDate = rc;
    rDate.top = rc.top + h / 2 + timeH / 2 + 12;
    rDate.bottom = rDate.top + dateH * 2;
    SetTextColor(hdc, RGB(0, 0, 0));
    RECT rDateShadow = rDate;
    rDateShadow.left += 2; rDateShadow.top += 2;
    DrawTextW(hdc, dateBuf, -1, &rDateShadow,
        DT_CENTER | DT_TOP | DT_SINGLELINE);
    SetTextColor(hdc, RGB(235, 235, 235));
    DrawTextW(hdc, dateBuf, -1, &rDate,
        DT_CENTER | DT_TOP | DT_SINGLELINE);

    SelectObject(hdc, hOld);
    DeleteObject(hBig);
    DeleteObject(hDate);
}

static void DrawLockPasswordPrompt(HDC hdc, RECT rc) {
    int w = rc.right - rc.left;
    int h = rc.bottom - rc.top;

    int boxW = min(460, w - 80);
    int boxH = 180;
    int boxX = (w - boxW) / 2;
    int boxY = (h - boxH) / 2 + 100;

    RECT box = { boxX, boxY, boxX + boxW, boxY + boxH };

    // Semi-transparent dark backdrop
    HBRUSH hB = CreateSolidBrush(RGB(20, 20, 20));
    FillRect(hdc, &box, hB);
    DeleteObject(hB);

    // Border
    HPEN hP = CreatePen(PS_SOLID, 1, RGB(120, 120, 120));
    HGDIOBJ oldP = SelectObject(hdc, hP);
    HGDIOBJ oldB = SelectObject(hdc, GetStockObject(NULL_BRUSH));
    Rectangle(hdc, box.left, box.top, box.right, box.bottom);
    SelectObject(hdc, oldB);
    SelectObject(hdc, oldP);
    DeleteObject(hP);

    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, RGB(220, 220, 220));

    HFONT hLabel = CreateFontW(-18, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    HFONT hOld = (HFONT)SelectObject(hdc, hLabel);

    RECT rLabel = box;
    rLabel.left += 20; rLabel.right -= 20; rLabel.top += 18; rLabel.bottom = rLabel.top + 24;
    DrawTextW(hdc, L"Enter password to dismiss", -1, &rLabel,
        DT_LEFT | DT_TOP | DT_SINGLELINE);

    // Masked password
    std::wstring masked(g_lockTypedPassword.size(), L'\x2022');
    if (masked.empty()) masked = L"\x2014";  // em dash as placeholder

    HFONT hPw = CreateFontW(-24, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    SelectObject(hdc, hPw);

    RECT rPw = box;
    rPw.left += 20; rPw.right -= 20; rPw.top = rLabel.bottom + 12;
    rPw.bottom = rPw.top + 36;
    // Underline area
    HPEN hLine = CreatePen(PS_SOLID, 1, RGB(90, 90, 90));
    HGDIOBJ oldP2 = SelectObject(hdc, hLine);
    MoveToEx(hdc, rPw.left, rPw.bottom - 4, nullptr);
    LineTo(hdc, rPw.right, rPw.bottom - 4);
    SelectObject(hdc, oldP2);
    DeleteObject(hLine);

    SetTextColor(hdc, RGB(255, 255, 255));
    DrawTextW(hdc, masked.c_str(), -1, &rPw,
        DT_LEFT | DT_VCENTER | DT_SINGLELINE);

    // Message (error or hint)
    if (!g_lockPromptMessage.empty()) {
        HFONT hMsg = CreateFontW(-14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
        SelectObject(hdc, hMsg);
        SetTextColor(hdc, RGB(255, 120, 120));
        RECT rMsg = box;
        rMsg.left += 20; rMsg.right -= 20; rMsg.top = rPw.bottom + 8;
        rMsg.bottom = rMsg.top + 20;
        DrawTextW(hdc, g_lockPromptMessage.c_str(), -1, &rMsg,
            DT_LEFT | DT_TOP | DT_SINGLELINE);
        DeleteObject(hMsg);
    }

    SelectObject(hdc, hOld);
    DeleteObject(hLabel);
    DeleteObject(hPw);
}

static LRESULT CALLBACK LockOverlayWndProc(HWND hwnd, UINT msg,
    WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_PAINT: {
        // GDI fallback layer. mpv's D3D11 swap chain sits on top of
        // this in the normal case, so this is only visible when no
        // video is loaded (black-screen state). Draw the clock and
        // password prompt here too so the fallback is complete.
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hwnd, &ps);
        RECT rc;
        GetClientRect(hwnd, &rc);

        FillRect(hdc, &rc, (HBRUSH)GetStockObject(BLACK_BRUSH));
        DrawLockClock(hdc, rc);

        if (g_lockMode == "password" && g_lockPasswordPromptVisible) {
            DrawLockPasswordPrompt(hdc, rc);
        }

        EndPaint(hwnd, &ps);
        return 0;
    }

    case WM_TIMER: {
        // Refresh the clock via osd-overlay. Internally no-ops unless
        // the displayed minute has changed.
        auto* player = reinterpret_cast<mpv_handle*>(
            GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (player) {
            UpdateLockClockFilters(player, /*firstTime=*/false);
        }

        // Auto-hide the password prompt after inactivity. Any key or
        // mouse event resets g_lockLastPasswordInputTick.
        if (g_lockMode == "password" && g_lockPasswordPromptVisible) {
            ULONGLONG idle = GetTickCount64() - g_lockLastPasswordInputTick;
            if (idle > kLockPasswordHideAfterMs) {
                g_lockPasswordPromptVisible = false;
                g_lockTypedPassword.clear();
                g_lockPromptMessage.clear();
                RefreshLockPasswordOverlays();
            }
        }

        // Refresh the GDI fallback layer.
        InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    }

    case WM_ERASEBKGND:
        return 1;

    case WM_MOUSEMOVE: {
        if (!LockPasswordRequired()) {
            // Screensaver-style behavior: any input dismisses. Ignore
            // the first ~750ms so the cursor's arrival doesn't
            // immediately dismiss.
            if (GetTickCount64() - g_lockStartedAt > 750) {
                DismissLockOverlay();
            }
        }
        // Password mode: do nothing on simple mouse move. The prompt
        // stays hidden until the user clicks or presses a key.
        return 0;
    }
    case WM_LBUTTONDOWN:
    case WM_RBUTTONDOWN:
    case WM_MBUTTONDOWN: {
        if (g_lockMode == "screensaver") {
            DismissLockOverlay();
            return 0;
        }
        g_lockLastPasswordInputTick = GetTickCount64();
        if (!g_lockPasswordPromptVisible) {
            g_lockPasswordPromptVisible = true;
            g_lockTypedPassword.clear();
            g_lockPromptMessage.clear();
            RefreshLockPasswordOverlays();
        }
        return 0;
    }

    case WM_KEYDOWN:
    case WM_SYSKEYDOWN: {
        if (g_lockMode == "screensaver") {
            DismissLockOverlay();
            return 0;
        }
        g_lockLastPasswordInputTick = GetTickCount64();
        if (!g_lockPasswordPromptVisible) {
            g_lockPasswordPromptVisible = true;
            g_lockTypedPassword.clear();
            g_lockPromptMessage.clear();
            RefreshLockPasswordOverlays();
        }
        if (wParam == VK_RETURN) {
            std::string typed = Narrow(g_lockTypedPassword.c_str());
            std::string hash = Sha256Hex(typed);
            if (!g_lockPasswordHash.empty() && hash == g_lockPasswordHash) {
                DismissLockOverlay();
            }
            else {
                g_lockTypedPassword.clear();
                g_lockPromptMessage = L"Wrong password.";
                RefreshLockPasswordOverlays();
            }
        }
        else if (wParam == VK_BACK) {
            if (!g_lockTypedPassword.empty()) {
                g_lockTypedPassword.pop_back();
                RefreshLockPasswordOverlays();
            }
        }
        else if (wParam == VK_ESCAPE) {
            g_lockPasswordPromptVisible = false;
            g_lockTypedPassword.clear();
            g_lockPromptMessage.clear();
            RefreshLockPasswordOverlays();
        }
        return 0;
    }
    case WM_CHAR: {
        if (g_lockMode == "password" && g_lockPasswordPromptVisible) {
            g_lockLastPasswordInputTick = GetTickCount64();
            wchar_t c = (wchar_t)wParam;
            if (c >= 32 && c != 127) {
                g_lockTypedPassword.push_back(c);
                RefreshLockPasswordOverlays();
            }
        }
        return 0;
    }

    case WM_DESTROY:
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static LRESULT CALLBACK LockKeyboardHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)) {
        KBDLLHOOKSTRUCT* p = reinterpret_cast<KBDLLHOOKSTRUCT*>(lParam);
        if (p) {
            // Block Windows key (any modifier combination)
            if (p->vkCode == VK_LWIN || p->vkCode == VK_RWIN) return 1;

            // Block Win+Tab and Alt+Tab
            if (p->vkCode == VK_TAB && ((p->flags & LLKHF_ALTDOWN) ||
                (GetAsyncKeyState(VK_LWIN) & 0x8000) ||
                (GetAsyncKeyState(VK_RWIN) & 0x8000))) {
                return 1;
            }

            // Block Alt+F4
            if (p->vkCode == VK_F4 && (p->flags & LLKHF_ALTDOWN)) return 1;

            // Block Ctrl+Esc
            if (p->vkCode == VK_ESCAPE &&
                (GetAsyncKeyState(VK_CONTROL) & 0x8000)) return 1;

            // Block Win+D (show desktop). 'D' == VK_D == 0x44;
            // VK_A..VK_Z aren't defined unless WINVER >= 0x0500, so
            // use the character literal to stay SDK-independent.
            if (p->vkCode == 'D' &&
                ((GetAsyncKeyState(VK_LWIN) & 0x8000) ||
                    (GetAsyncKeyState(VK_RWIN) & 0x8000))) return 1;
        }
    }
    return CallNextHookEx(g_hLockKeyboardHook, nCode, wParam, lParam);
}

struct LockEnumData { int nextIndex; };

static BOOL CALLBACK LockMonitorEnumProc(HMONITOR hMonitor, HDC, LPRECT, LPARAM lp) {
    auto* data = reinterpret_cast<LockEnumData*>(lp);
    int thisIndex = data ? data->nextIndex++ : -1;

    MONITORINFO mi = { sizeof(mi) };
    if (!GetMonitorInfoW(hMonitor, &mi)) return TRUE;

    bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;

    int x = mi.rcMonitor.left;
    int y = mi.rcMonitor.top;
    int w = mi.rcMonitor.right - mi.rcMonitor.left;
    int h = mi.rcMonitor.bottom - mi.rcMonitor.top;

    HWND hwnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
        kLockWndClass, L"Strata Lock Overlay",
        WS_POPUP,
        x, y, w, h,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!hwnd) return TRUE;

    mpv_handle* player = CreateMpvPlayer(hwnd, g_targetFps);
    if (!player) {
        DestroyWindow(hwnd);
        return TRUE;
    }
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(player));

    // Apply dim setting immediately
    if (g_lockDimWhenLocked)
        g_mpv.set_property_string(player, "brightness", "-30");

    std::string path;
    if (GetLockVideoPath(path, thisIndex)) {
        std::cout << "[LOCK ] Overlay loading video: " << path << "\n";
        const char* cmd[] = { "loadfile", path.c_str(), nullptr };
        g_mpv.command(player, cmd);
    }
    else {
        std::cout << "[LOCK ] Overlay failed to resolve any video.\n";
    }

    // Attach the clock via osd-overlay.
    UpdateLockClockFilters(player, /*firstTime=*/true);

    // Sync the password overlay to the current state (usually hidden).
    // Only the primary monitor ever shows it.
    UpdateLockPasswordOverlay(player, isPrimary);

    ShowWindow(hwnd, SW_SHOW);
    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
    SetForegroundWindow(hwnd);
    SetTimer(hwnd, 1, 1000, nullptr);

    LockOverlay lo{};
    lo.hwnd = hwnd;
    lo.player = player;
    lo.monitor = hMonitor;
    lo.index = thisIndex;
    lo.isPrimary = isPrimary;
    g_lockOverlays.push_back(lo);
    return TRUE;
}

static void StartLockOverlay() {
    if (g_lockActive) return;
    g_lockActive = true;
    g_lockStartedAt = GetTickCount64();
    g_lockPasswordPromptVisible = false;
    g_lockTypedPassword.clear();
    g_lockPromptMessage.clear();

    // FORCE RELOAD from INI right before locking
    g_lockDayNight = LoadLockDayNightConfig();
    std::cout << "[LOCK ] --- STARTING LOCK OVERLAY ---\n";
    std::cout << "[LOCK ] Loaded from INI -> enabled=" << g_lockDayNight.enabled
        << " DayVideo='" << g_lockDayNight.dayVideo << "'"
        << " NightVideo='" << g_lockDayNight.nightVideo << "'\n";
    g_lockTypedPassword.clear();
    g_lockPromptMessage.clear();

    // Install keyboard hook to block Win, Alt+Tab, etc. while locked.
    if (!g_hLockKeyboardHook) {
        g_hLockKeyboardHook = SetWindowsHookExW(
            WH_KEYBOARD_LL, LockKeyboardHookProc,
            GetModuleHandleW(nullptr), 0);
        if (!g_hLockKeyboardHook) {
            std::cout << "[LOCK ] keyboard hook install failed, "
                "err=" << GetLastError() << "\n";
        }
    }

    g_lockOverlays.clear();
    LockEnumData enumData{ 0 };
    EnumDisplayMonitors(nullptr, nullptr, LockMonitorEnumProc,
        reinterpret_cast<LPARAM>(&enumData));

    std::cout << "[LOCK ] overlay shown on " << g_lockOverlays.size()
        << " monitor(s), mode=" << g_lockMode << "\n";

    // Force wallpaper pause state immediately.
    UpdatePauseState();
}

static void StopLockOverlay() {
    if (g_hLockKeyboardHook) {
        UnhookWindowsHookEx(g_hLockKeyboardHook);
        g_hLockKeyboardHook = nullptr;
    }

    for (auto& lo : g_lockOverlays) {
        if (lo.player) {
            g_mpv.terminate_destroy(lo.player);
            lo.player = nullptr;
        }
        if (lo.hwnd && IsWindow(lo.hwnd)) {
            KillTimer(lo.hwnd, 1);
            DestroyWindow(lo.hwnd);
        }
    }
    g_lockOverlays.clear();
    g_lockActive = false;
    g_lockPasswordPromptVisible = false;
    g_lockTypedPassword.clear();
    g_lockPromptMessage.clear();
    std::cout << "[LOCK ] overlay dismissed\n";
    UpdatePauseState();
}

static void DismissLockOverlay() {
    if (!g_lockActive) return;
    StopLockOverlay();
}

// =====================================================================
// Icon / tray
// =====================================================================
static HICON LoadAppIcon(HINSTANCE hInst) {
    std::wstring exePath = GetCurrentExePath();
    size_t slash = exePath.find_last_of(L"\\/");
    std::wstring iconPath = (slash == std::wstring::npos ? L"" : exePath.substr(0, slash + 1))
        + L"strata.ico";

    HICON h = (HICON)LoadImageW(nullptr, iconPath.c_str(), IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON),
        GetSystemMetrics(SM_CYSMICON),
        LR_LOADFROMFILE);
    if (h) return h;
    h = LoadIconW(nullptr, IDI_INFORMATION);
    return h ? h : LoadIconW(nullptr, IDI_APPLICATION);
}

static void UpdateTrayTooltip() {
    if (!g_nid.hWnd) return;
    std::wstring state = L"Running";
    if (g_manualPaused.load())       state = L"Paused (manual)";
    else if (g_sessionLocked.load()) state = L"Session locked";
    std::wstring tip = L"Strata Wallpaper - " + state;
    wcsncpy_s(g_nid.szTip, tip.c_str(), _TRUNCATE);
    g_nid.uFlags = NIF_TIP;
    Shell_NotifyIconW(NIM_MODIFY, &g_nid);
}

static void ShowStartupBalloon() {
    if (!g_showStartupBalloon) return;
    if (!g_nid.hWnd) return;
    g_nid.uFlags = NIF_INFO;
    g_nid.dwInfoFlags = NIIF_INFO;
    wcsncpy_s(g_nid.szInfoTitle, L"Strata Wallpaper", _TRUNCATE);
    wcsncpy_s(g_nid.szInfo,
        L"Live wallpaper running. Double-click the tray icon to pause/resume.",
        _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &g_nid);
}

static void ShowAlreadyRunningBalloon() {
    if (!g_nid.hWnd) return;
    g_nid.uFlags = NIF_INFO;
    g_nid.dwInfoFlags = NIIF_INFO;
    wcsncpy_s(g_nid.szInfoTitle, L"Strata Wallpaper", _TRUNCATE);
    wcsncpy_s(g_nid.szInfo,
        L"Engine is already running. Look for the tray icon.",
        _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &g_nid);
}

static void ReloadSettingsFromDisk() {
    g_forceVideoPath.clear();
    g_idlePauseSeconds = LoadIdlePauseSeconds();
    g_slideshow = LoadSlideshowConfig();
    g_dayNight = LoadDayNightConfig();
    g_lockDayNight = LoadLockDayNightConfig();

    {
        char modeBuf[32] = {};
        GetPrivateProfileStringA("Wallpaper", "DisplayMode", "copy",
            modeBuf, 32, GetSettingsFilePath().c_str());
        g_displayMode = modeBuf;
        std::cout << "Display mode    : " << g_displayMode << "\n";
    }

    // New sections
    std::string q = LoadQualityMode();
    if (q != g_qualityMode) ApplyQualityChange(q);
    LoadLockConfig();
    LoadPrayerCache();

    LoadLibraryFolders();
    RescanLibrary();

    // Reload persisted wallpaper assignments.
    {
        std::string w = LoadVideoPath();
        if (!w.empty() && FileExists(w)) {
            g_forceVideoPath = w;
        }
        for (int i = 0; i < 8; i++) {
            std::string section = "Monitor" + std::to_string(i);
            char buf[MAX_PATH] = {};
            GetPrivateProfileStringA(section.c_str(), "VideoPath", "",
                buf, MAX_PATH, GetSettingsFilePath().c_str());
            if (buf[0] != '\0' && FileExists(buf)) {
                g_monitorForcePaths[i] = buf;
            }
        }
    }

    if (g_dayNight.enabled) {
        CalculateSunTimes(g_dayNight.latitude, g_dayNight.longitude,
            g_cachedSunrise, g_cachedSunset);
        g_lastSunCalcTick = GetTickCount64();
    }

    std::cout << "[RELOAD] Settings reloaded.\n";
    std::cout << "  Idle-pause : " << g_idlePauseSeconds << "s\n";
    std::cout << "  Slideshow  : " << (g_slideshow.enabled ? "on" : "off") << "\n";
    std::cout << "  Day/Night  : " << (g_dayNight.enabled ? "on" : "off")
        << " (mode=" << g_dayNight.mode << ")\n";
    std::cout << "  Quality    : " << g_qualityMode << "\n";
    std::cout << "  Lock       : " << (g_lockEnabled ? "on" : "off")
        << " (" << g_lockIdleSeconds << "s, " << g_lockMode << ")\n";
}

static void LaunchControlPanel() {
    // Prefer signaling an existing panel process to show itself instead
    // of spawning a second process. The WinUI panel must call AppWindow.Show().
    HANDLE panelEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE,
        L"Local\\StrataWallpaper_Panel_Show");
    if (panelEvent != NULL) {
        SetEvent(panelEvent);
        CloseHandle(panelEvent);
        std::cout << "[TRAY  ] signaled control panel\n";
        return;
    }

    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    std::wstring dir(exePath);
    size_t slash = dir.find_last_of(L"\\/");
    if (slash != std::wstring::npos) dir = dir.substr(0, slash + 1);

    const wchar_t* relCandidates[] = {
        L"Blueprint.ControlPanel.exe",
        L"..\\..\\Blueprint.ControlPanel\\bin\\x64\\Release\\net8.0-windows10.0.19041.0\\win-x64\\Blueprint.ControlPanel.exe",
        L"..\\..\\Blueprint.ControlPanel\\bin\\x64\\Debug\\net8.0-windows10.0.19041.0\\win-x64\\Blueprint.ControlPanel.exe",
        L"..\\..\\..\\Blueprint.ControlPanel\\bin\\x64\\Release\\net8.0-windows10.0.19041.0\\win-x64\\Blueprint.ControlPanel.exe",
    };

    for (auto rel : relCandidates) {
        std::wstring combined = dir + rel;
        wchar_t full[MAX_PATH * 2] = {};
        if (!GetFullPathNameW(combined.c_str(), MAX_PATH * 2, full, nullptr))
            continue;

        DWORD attrs = GetFileAttributesW(full);
        if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_DIRECTORY))
            continue;

        STARTUPINFOW si{};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi{};
        std::wstring cmd = L"\"" + std::wstring(full) + L"\"";

        if (CreateProcessW(nullptr, cmd.data(), nullptr, nullptr,
            FALSE, 0, nullptr, dir.c_str(), &si, &pi)) {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            std::cout << "[TRAY  ] launched control panel\n";
            return;
        }
    }
    std::cout << "[TRAY  ] could not find control panel exe\n";
}

static void ShowTrayMenu(HWND hwnd) {
    HMENU menu = CreatePopupMenu();
    const wchar_t* pauseLabel = g_manualPaused.load() ? L"Resume" : L"Pause";
    AppendMenuW(menu, MF_STRING, ID_TRAY_OPEN_PANEL, L"Open Strata Wallpaper...");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, ID_TRAY_PAUSE_RESUME, pauseLabel);
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, ID_TRAY_OPEN_SETTINGS, L"Open Settings INI...");
    AppendMenuW(menu, MF_STRING, ID_TRAY_OPEN_LOG, L"Open Log...");
    AppendMenuW(menu, MF_STRING, ID_TRAY_RELOAD, L"Reload Settings");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    UINT autoFlags = MF_STRING | (IsAutoStartEnabled() ? MF_CHECKED : MF_UNCHECKED);
    AppendMenuW(menu, autoFlags, ID_TRAY_AUTOSTART, L"Run at Startup");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, ID_TRAY_EXIT, L"Exit");

    POINT pt;
    GetCursorPos(&pt);
    SetForegroundWindow(hwnd);
    TrackPopupMenu(menu, TPM_RIGHTBUTTON, pt.x, pt.y, 0, hwnd, nullptr);
    DestroyMenu(menu);
}

// =====================================================================
// Hidden window proc
// =====================================================================
static LRESULT CALLBACK HiddenWndProc(HWND hwnd, UINT msg,
    WPARAM wParam, LPARAM lParam) {
    if (msg == WM_APP_IPC_REQUEST) {
        PendingRequest* req = reinterpret_cast<PendingRequest*>(lParam);
        std::string response = HandleIpcCommand(req->requestJson);
        PipeServer_CompleteRequest(req, response);
        return 0;
    }

    if (msg == WM_APP_PRAYER_FETCH_DONE) {
        if (g_prayerFetchDone.exchange(false)) {
            std::cout << "[PRAYER] fetch done; cache refreshed\n";
        }
        return 0;
    }

    if (g_wmShowExisting != 0 && msg == g_wmShowExisting) {
        std::cout << "[SINGLE] Second instance attempted to launch.\n";
        ShowAlreadyRunningBalloon();
        ShowTrayMenu(hwnd);
        return 0;
    }

    if (msg == WM_WTSSESSION_CHANGE) {
        if (wParam == WTS_SESSION_LOCK) {
            std::cout << "[EVENT ] session LOCKED\n";
            g_sessionLocked.store(true);
            UpdatePauseState();
            UpdateTrayTooltip();
        }
        else if (wParam == WTS_SESSION_UNLOCK) {
            std::cout << "[EVENT ] session UNLOCKED\n";
            g_sessionLocked.store(false);
            UpdatePauseState();
            UpdateTrayTooltip();
        }
        return 0;
    }

    if (msg == WM_TRAYICON) {
        UINT mouseMsg = (UINT)LOWORD(lParam);
        if (mouseMsg == WM_RBUTTONUP || mouseMsg == WM_CONTEXTMENU) {
            ShowTrayMenu(hwnd);
        }
        else if (mouseMsg == WM_LBUTTONDBLCLK) {
            LaunchControlPanel();
        }
        return 0;
    }

    if (msg == WM_COMMAND) {
        switch (LOWORD(wParam)) {
        case ID_TRAY_OPEN_PANEL:  LaunchControlPanel(); break;
        case ID_TRAY_PAUSE_RESUME:
            g_manualPaused.store(!g_manualPaused.load());
            UpdatePauseState();
            UpdateTrayTooltip();
            break;
        case ID_TRAY_OPEN_SETTINGS:
            ShellExecuteA(nullptr, "open", GetSettingsFilePath().c_str(),
                nullptr, nullptr, SW_SHOWNORMAL);
            break;
        case ID_TRAY_OPEN_LOG:
            ShellExecuteA(nullptr, "open", GetLogFilePath().c_str(),
                nullptr, nullptr, SW_SHOWNORMAL);
            break;
        case ID_TRAY_RELOAD: ReloadSettingsFromDisk(); break;
        case ID_TRAY_AUTOSTART: SetAutoStart(!IsAutoStartEnabled()); break;
        case ID_TRAY_EXIT: PostQuitMessage(0); break;
        }
        return 0;
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static bool CreateHiddenWindowAndTray(HINSTANCE hInst) {
    WNDCLASSW hwc = {};
    hwc.lpfnWndProc = HiddenWndProc;
    hwc.hInstance = hInst;
    hwc.lpszClassName = kHiddenWndClass;
    RegisterClassW(&hwc);

    g_hHiddenWnd = CreateWindowExW(0, kHiddenWndClass, L"", WS_POPUP,
        0, 0, 0, 0, nullptr, nullptr, hInst, nullptr);
    if (!g_hHiddenWnd) return false;

    WTSRegisterSessionNotification(g_hHiddenWnd, NOTIFY_FOR_THIS_SESSION);

    g_hTrayIcon = LoadAppIcon(hInst);
    memset(&g_nid, 0, sizeof(g_nid));
    g_nid.cbSize = sizeof(g_nid);
    g_nid.hWnd = g_hHiddenWnd;
    g_nid.uID = 1;
    g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_nid.uCallbackMessage = WM_TRAYICON;
    g_nid.hIcon = g_hTrayIcon;
    wcsncpy_s(g_nid.szTip, L"Strata Wallpaper", _TRUNCATE);
    if (!Shell_NotifyIconW(NIM_ADD, &g_nid)) return false;
    ShowStartupBalloon();
    return true;
}

static void DestroyTrayAndHiddenWindow() {
    if (g_hHiddenWnd) {
        g_nid.uFlags = 0;
        Shell_NotifyIconW(NIM_DELETE, &g_nid);
        WTSUnRegisterSessionNotification(g_hHiddenWnd);
        DestroyWindow(g_hHiddenWnd);
        g_hHiddenWnd = nullptr;
    }
    if (g_hTrayIcon) { DestroyIcon(g_hTrayIcon); g_hTrayIcon = nullptr; }
}

// =====================================================================
// Wallpaper window proc
// =====================================================================
static LRESULT CALLBACK WallpaperWndProc(HWND hwnd, UINT msg,
    WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_SIZE: {
        auto* player = reinterpret_cast<mpv_handle*>(
            GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        UpdateVlcDisplay(hwnd, player);
        return 0;
    }
    case WM_NCHITTEST: {
        if (hwnd == g_hPreviewWnd) {
            LRESULT hit = DefWindowProcW(hwnd, msg, wParam, lParam);
            if (hit == HTCLIENT) return HTCAPTION;
            return hit;
        }
        break;
    }
    case WM_ERASEBKGND: return 1;
    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hwnd, &ps);
        RECT rc; GetClientRect(hwnd, &rc);
        FillRect(hdc, &rc, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
        HPEN pen = CreatePen(PS_SOLID, 1, RGB(80, 80, 80));
        HGDIOBJ old = SelectObject(hdc, pen);
        HGDIOBJ oldBrush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
        Rectangle(hdc, rc.left, rc.top, rc.right, rc.bottom);
        SelectObject(hdc, oldBrush);
        SelectObject(hdc, old);
        DeleteObject(pen);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_DESTROY: return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

// =====================================================================
// WorkerW discovery
// =====================================================================
static bool WorkerWCoversVirtualDesktop(HWND hw) {
    if (!hw || !IsWindow(hw)) return false;
    RECT r{};
    if (!GetWindowRect(hw, &r)) return false;
    int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
    int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    return r.left <= vx + 8 && r.top <= vy + 8 &&
        (r.right - r.left) >= vw - 16 &&
        (r.bottom - r.top) >= vh - 16;
}

static HWND FindBackgroundWorkerW(HWND hIconsWorkerW) {
    HWND searchAfter = hIconsWorkerW;
    for (int i = 0; i < 8; ++i) {
        HWND candidate = FindWindowExW(nullptr, searchAfter, L"WorkerW", nullptr);
        if (!candidate) break;
        HWND defView = FindWindowExW(candidate, nullptr, L"SHELLDLL_DefView", nullptr);
        if (!defView) {
            RECT rc{};
            if (GetClientRect(candidate, &rc) &&
                rc.right > rc.left && rc.bottom > rc.top) {
                return candidate;
            }
        }
        searchAfter = candidate;
    }
    return nullptr;
}

// =====================================================================
// Per-monitor wallpaper creation
// =====================================================================
struct MonitorEnumData {
    HWND parent;
    HWND hDefView;
};

static BOOL CALLBACK MonitorEnumProc(HMONITOR hMonitor, HDC, LPRECT, LPARAM dwData) {
    auto* data = reinterpret_cast<MonitorEnumData*>(dwData);

    int thisIndex = g_monitorIndex++;
    bool hasOverride = false;
    std::string thisVideoPath = GetVideoPathForMonitor(thisIndex, hasOverride);
    std::cout << "  -> Monitor " << thisIndex
        << (hasOverride ? " [override]" : " [rotating]")
        << " video: " << thisVideoPath << "\n";

    MONITORINFO mi = { sizeof(mi) };
    if (!GetMonitorInfoW(hMonitor, &mi)) return TRUE;

    POINT tl = { mi.rcMonitor.left,  mi.rcMonitor.top };
    POINT br = { mi.rcMonitor.right, mi.rcMonitor.bottom };
    ScreenToClient(data->parent, &tl);
    ScreenToClient(data->parent, &br);

    int x = tl.x, y = tl.y;
    int w = br.x - tl.x, h = br.y - tl.y;
    if (w <= 0 || h <= 0) return TRUE;

    HWND hwnd = CreateWindowExW(
        0, L"BlueprintWallpaperClass", L"Blueprint Wallpaper",
        WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
        x, y, w, h, data->parent, nullptr,
        GetModuleHandleW(nullptr), nullptr);
    if (!hwnd) return TRUE;

    RECT actual{};
    GetWindowRect(hwnd, &actual);
    std::cout << "  created window: "
        << actual.left << "," << actual.top
        << " size " << (actual.right - actual.left) << "x" << (actual.bottom - actual.top)
        << " visible=" << IsWindowVisible(hwnd)
        << " parent=" << data->parent << "\n";

    mpv_handle* player = CreateMpvPlayer(hwnd, g_targetFps);
    if (!player) { DestroyWindow(hwnd); return TRUE; }
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(player));

    if (!thisVideoPath.empty()) {
        const char* cmd[] = { "loadfile", thisVideoPath.c_str(), nullptr };
        g_mpv.command(player, cmd);
    }

    SetWindowPos(hwnd, data->hDefView, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
    RedrawWindow(hwnd, nullptr, nullptr,
        RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_UPDATENOW);

    WallpaperInstance inst{};
    inst.hwnd = hwnd;
    inst.player = player;
    inst.monitor = hMonitor;
    inst.index = thisIndex;
    inst.hasOverride = hasOverride;
    inst.currentVideoPath = thisVideoPath;
    inst.warmupUntil = GetTickCount64() + 5000;

    g_wallpapers.push_back(inst);
    g_monitorPaused[hwnd] = false;
    return TRUE;
}

// =====================================================================
// Setup / teardown
// =====================================================================
static bool SetupWallpapers(bool fastReembed) {
    HWND hProgman = FindWindowW(L"Progman", nullptr);
    if (!hProgman) return false;

    DWORD_PTR result = 0;
    HWND hDefView = nullptr;
    HWND hIconsWorkerW = nullptr;
    HWND hWorkerW = nullptr;

    const int maxAttempts = fastReembed ? 1 : 12;

    for (int attempt = 0; attempt < maxAttempts; attempt++) {
        SendMessageTimeoutW(hProgman, 0x052C, 0, 0, SMTO_NORMAL, 1000, &result);

        hDefView = FindWindowExW(hProgman, nullptr, L"SHELLDLL_DefView", nullptr);
        if (!hDefView)
            hDefView = FindWindowExW(nullptr, nullptr, L"SHELLDLL_DefView", nullptr);

        hIconsWorkerW = hDefView ? GetParent(hDefView) : hProgman;
        hWorkerW = FindBackgroundWorkerW(hIconsWorkerW);

        if (hWorkerW && WorkerWCoversVirtualDesktop(hWorkerW)) {
            std::cout << "[WORKERW] ready on attempt " << (attempt + 1) << "\n";
            break;
        }
        if (!fastReembed) {
            std::cout << "[WORKERW] attempt " << (attempt + 1)
                << " hwnd=" << hWorkerW
                << " covers=" << (hWorkerW ? WorkerWCoversVirtualDesktop(hWorkerW) : 0)
                << " retrying...\n";
            Sleep(200);
        }
    }

    if (!hWorkerW) hWorkerW = hProgman;
    g_hWorkerW = hWorkerW;
    std::cout << "Background WorkerW HWND = " << hWorkerW << "\n";

    RECT wrc{};
    GetWindowRect(hWorkerW, &wrc);
    std::cout << "  WorkerW screen rect: "
        << wrc.left << "," << wrc.top
        << "  size " << (wrc.right - wrc.left) << "x" << (wrc.bottom - wrc.top)
        << "\n";

    if (g_displayMode == "stretch") {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        POINT tl = { vx, vy };
        POINT br = { vx + vw, vy + vh };
        ScreenToClient(hWorkerW, &tl);
        ScreenToClient(hWorkerW, &br);

        int x = tl.x, y = tl.y, w = br.x - tl.x, h = br.y - tl.y;
        std::cout << "[STRETCH] One window at " << x << "," << y
            << " size " << w << "x" << h << "\n";

        HWND hwnd = CreateWindowExW(
            0, L"BlueprintWallpaperClass", L"Blueprint Wallpaper",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            x, y, w, h, hWorkerW, nullptr,
            GetModuleHandleW(nullptr), nullptr);
        if (!hwnd) return false;

        mpv_handle* player = CreateMpvPlayer(hwnd, g_targetFps);
        if (!player) { DestroyWindow(hwnd); return false; }
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(player));

        std::string path = !g_forceVideoPath.empty() ? g_forceVideoPath : LoadVideoPath();
        if (path.empty()) {
            g_mpv.terminate_destroy(player);
            DestroyWindow(hwnd);
            return false;
        }

        const char* cmd[] = { "loadfile", path.c_str(), nullptr };
        g_mpv.command(player, cmd);

        SetWindowPos(hwnd, hDefView, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
        RedrawWindow(hwnd, nullptr, nullptr,
            RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_UPDATENOW);

        WallpaperInstance inst{};
        inst.hwnd = hwnd;
        inst.player = player;
        inst.monitor = nullptr;
        inst.index = -1;
        inst.hasOverride = false;
        inst.currentVideoPath = path;
        inst.warmupUntil = GetTickCount64() + 5000;
        g_wallpapers.push_back(inst);
        g_monitorPaused[hwnd] = false;
        return true;
    }

    g_monitorIndex = 0;
    MonitorEnumData enumData = { hWorkerW, hDefView };
    EnumDisplayMonitors(nullptr, nullptr, MonitorEnumProc,
        reinterpret_cast<LPARAM>(&enumData));
    return !g_wallpapers.empty();
}

static bool IsControlPanelRunning() {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;

    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    bool found = false;

    if (Process32FirstW(snap, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, L"Blueprint.ControlPanel.exe") == 0) {
                found = true;
                break;
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return found;
}

// =====================================================================
// Preview window
// =====================================================================
static void StartPreview(const std::string& overridePath) {
    if (g_hPreviewWnd) {
        if (!overridePath.empty()) RefreshPreviewVideo(overridePath);
        SetForegroundWindow(g_hPreviewWnd);
        return;
    }

    const int w = 640, h = 360;
    int screenW = GetSystemMetrics(SM_CXSCREEN);
    int screenH = GetSystemMetrics(SM_CYSCREEN);
    int x = screenW - w - 220;
    int y = (screenH - h) / 2;

    g_hPreviewWnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
        L"BlueprintWallpaperClass", L"BlueprintPreview",
        WS_POPUP,
        x, y, w, h,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!g_hPreviewWnd) {
        std::cout << "[PREVIEW] CreateWindowExW failed\n";
        return;
    }

    g_previewPlayer = CreateMpvPlayer(g_hPreviewWnd, g_targetFps);
    if (!g_previewPlayer) {
        DestroyWindow(g_hPreviewWnd);
        g_hPreviewWnd = nullptr;
        return;
    }
    SetWindowLongPtrW(g_hPreviewWnd, GWLP_USERDATA,
        reinterpret_cast<LONG_PTR>(g_previewPlayer));

    std::string path = overridePath;
    if (path.empty()) {
        if (!g_wallpapers.empty() && !g_wallpapers[0].currentVideoPath.empty())
            path = g_wallpapers[0].currentVideoPath;
        else
            path = LoadVideoPath();
    }

    if (!path.empty()) {
        const char* cmd[] = { "loadfile", path.c_str(), nullptr };
        g_mpv.command(g_previewPlayer, cmd);
        g_previewCurrentPath = path;
    }

    ShowWindow(g_hPreviewWnd, SW_SHOWNOACTIVATE);
    UpdateWindow(g_hPreviewWnd);
    std::cout << "[PREVIEW] started: " << path << "\n";
}

static void StopPreview() {
    if (g_previewPlayer) {
        g_mpv.terminate_destroy(g_previewPlayer);
        g_previewPlayer = nullptr;
    }
    if (g_hPreviewWnd && IsWindow(g_hPreviewWnd)) {
        DestroyWindow(g_hPreviewWnd);
    }
    g_hPreviewWnd = nullptr;
    g_previewCurrentPath.clear();
    std::cout << "[PREVIEW] stopped\n";
}

static void RefreshPreviewVideo(const std::string& path) {
    if (!g_previewPlayer || path.empty()) return;
    const char* cmd[] = { "loadfile", path.c_str(), "replace", nullptr };
    g_mpv.command(g_previewPlayer, cmd);
    g_previewCurrentPath = path;
    std::cout << "[PREVIEW] switched to " << path << "\n";
}

static void ReleaseAllWallpapers() {
    for (auto& wp : g_wallpapers) {
        if (wp.player) {
            g_mpv.terminate_destroy(wp.player);
            wp.player = nullptr;
        }
        if (wp.hwnd && IsWindow(wp.hwnd)) DestroyWindow(wp.hwnd);
    }
    g_wallpapers.clear();
    g_monitorPaused.clear();
}

// =====================================================================
// Console / log
// =====================================================================
static bool CommandLineHasFlag(const wchar_t* flag) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;
    bool found = false;
    for (int i = 0; i < argc; i++) {
        if (_wcsicmp(argv[i], flag) == 0) { found = true; break; }
    }
    LocalFree(argv);
    return found;
}

static void RedirectOutputAndHideConsole() {
    std::string logPath = GetLogFilePath();
    if (!logPath.empty()) {
        FILE* f = _fsopen(logPath.c_str(), "w", _SH_DENYNO);
        if (f) {
            _dup2(_fileno(f), _fileno(stdout));
            _dup2(_fileno(f), _fileno(stderr));
            setvbuf(stdout, nullptr, _IONBF, 0);
        }
    }
    HWND hConsole = GetConsoleWindow();
    if (hConsole) ShowWindow(hConsole, SW_HIDE);
}

// =====================================================================
// Single-instance
// =====================================================================
enum class SingleInstanceResult { FirstInstance, SecondInstance };

static SingleInstanceResult AcquireSingleInstance() {
    g_hSingleInstanceMutex = CreateMutexW(nullptr, TRUE, kMutexName);
    if (!g_hSingleInstanceMutex) return SingleInstanceResult::FirstInstance;

    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        HWND existing = FindWindowW(kHiddenWndClass, nullptr);
        if (existing && g_wmShowExisting != 0) {
            PostMessageW(existing, g_wmShowExisting, 0, 0);
        }
        CloseHandle(g_hSingleInstanceMutex);
        g_hSingleInstanceMutex = nullptr;
        return SingleInstanceResult::SecondInstance;
    }
    return SingleInstanceResult::FirstInstance;
}

static void ReleaseSingleInstance() {
    if (g_hSingleInstanceMutex) {
        ReleaseMutex(g_hSingleInstanceMutex);
        CloseHandle(g_hSingleInstanceMutex);
        g_hSingleInstanceMutex = nullptr;
    }
}

// =====================================================================
// IPC helpers
// =====================================================================
static std::string Ipc_Ok(const std::string& id, const json& body = json::object()) {
    json r;
    r["version"] = 1;
    r["id"] = id;
    r["status"] = "ok";
    r["body"] = body;
    return r.dump();
}

static std::string Ipc_Error(const std::string& id,
    const std::string& code,
    const std::string& msg) {
    json r;
    r["version"] = 1;
    r["id"] = id;
    r["status"] = "error";
    r["body"]["code"] = code;
    r["body"]["message"] = msg;
    return r.dump();
}

struct MonEntry { int index; RECT rect; bool primary; };
static std::vector<MonEntry> g_monitorList;

static BOOL CALLBACK EnumMonitorListProc(HMONITOR hm, HDC, LPRECT, LPARAM) {
    MONITORINFO mi = { sizeof(mi) };
    if (!GetMonitorInfoW(hm, &mi)) return TRUE;
    MonEntry e;
    e.index = (int)g_monitorList.size();
    e.rect = mi.rcMonitor;
    e.primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
    g_monitorList.push_back(e);
    return TRUE;
}

// =====================================================================
// IPC command handler
// =====================================================================
static std::string HandleIpcCommand(const std::string& raw) {
    std::string id;
    try {
        json req = json::parse(raw);
        id = req.value("id", std::string(""));
        std::string type = req.value("type", std::string(""));

        // Quiet IPC: get_status, get_settings, and ping are used for
        // polling and would otherwise flood the log.
        if (type != "get_status" && type != "get_settings"
            && type != "ping") {
            std::cout << "[IPC ] Command: " << type << " (id=" << id << ")\n";
        }

        if (type == "ping") {
            return Ipc_Ok(id, { { "pong", true } });
        }

        if (type == "get_status") {
            const char* pauseReason = GetPauseReason();
            json body;
            body["state"] = std::string(pauseReason) == "none" ? "running" : "paused";
            body["paused"] = std::string(pauseReason) != "none";
            body["monitors"] = (int)g_wallpapers.size();
            body["pause_reason"] = pauseReason;
            body["lock_overlay_active"] = g_lockActive;
            if (!g_wallpapers.empty())
                body["wallpaper"] = g_wallpapers[0].currentVideoPath;
            return Ipc_Ok(id, body);
        }

        if (type == "pause") {
            g_manualPaused.store(true);
            UpdatePauseState();
            UpdateTrayTooltip();
            return Ipc_Ok(id, { { "paused", true } });
        }
        if (type == "resume") {
            g_manualPaused.store(false);
            UpdatePauseState();
            UpdateTrayTooltip();
            return Ipc_Ok(id, { { "paused", false } });
        }

        if (type == "apply_wallpaper") {
            std::string path = req["body"].value("path", std::string(""));
            std::string monitor = req["body"].value("monitor", std::string("all"));

            if (path.empty() ||
                GetFileAttributesA(path.c_str()) == INVALID_FILE_ATTRIBUTES) {
                return Ipc_Error(id, "file_not_found",
                    "Video file does not exist: " + path);
            }

            // ── Batch 7: honor per-wallpaper overrides ──
            if (req["body"].contains("overrides")) {
                auto& ov = req["body"]["overrides"];

                if (ov.contains("fps")) {
                    int f = ov["fps"].get<int>();
                    if (f >= 0) {
                        g_targetFps = f;
                        std::string filter = BuildVideoFilter();
                        for (auto& wp : g_wallpapers) {
                            if (wp.player)
                                g_mpv.set_property_string(wp.player, "vf", filter.c_str());
                        }
                    }
                }
                if (ov.contains("quality")) {
                    std::string q = ov["quality"].get<std::string>();
                    if (q == "fast" || q == "balanced" || q == "high" || q == "vsr") {
                        if (q != g_qualityMode) ApplyQualityChange(q);
                    }
                }
                if (ov.contains("display_mode")) {
                    std::string m = ov["display_mode"].get<std::string>();
                    if (m == "copy" || m == "stretch" || m == "individual")
                        g_displayMode = m;
                }
                if (ov.contains("apply_all_monitors")) {
                    if (ov["apply_all_monitors"].get<bool>()) monitor = "all";
                }
                if (ov.contains("daynight")) {
                    auto& dn = ov["daynight"];
                    if (dn.contains("enabled"))
                        g_dayNight.enabled = dn["enabled"].get<bool>();
                    if (dn.contains("mode")) {
                        std::string m = dn["mode"].get<std::string>();
                        if (m == "manual" || m == "sunrise" || m == "prayer")
                            g_dayNight.mode = m;
                    }
                    if (dn.contains("day_video"))
                        g_dayNight.dayVideo = dn["day_video"].get<std::string>();
                    if (dn.contains("night_video"))
                        g_dayNight.nightVideo = dn["night_video"].get<std::string>();
                }
            }

            std::string iniPath = GetSettingsFilePath();

            if (monitor == "all" || g_displayMode == "stretch") {
                g_forceVideoPath = path;
                g_monitorForcePaths.clear();
                WritePrivateProfileStringA("Wallpaper", "VideoPath",
                    path.c_str(), iniPath.c_str());
                for (int i = 0; i < 8; i++) {
                    std::string section = "Monitor" + std::to_string(i);
                    WritePrivateProfileStringA(section.c_str(), "VideoPath",
                        nullptr, iniPath.c_str());
                }
            }
            else {
                // Do NOT clear g_forceVideoPath here — a single-monitor
                // apply must not knock a previous "Both" assignment off
                // the other monitor.
                int idx = -1;
                try { idx = std::stoi(monitor); }
                catch (...) {
                    return Ipc_Error(id, "bad_request",
                        "Invalid monitor value: " + monitor);
                }
                g_monitorForcePaths[idx] = path;
                std::string section = "Monitor" + std::to_string(idx);
                WritePrivateProfileStringA(section.c_str(), "VideoPath",
                    path.c_str(), iniPath.c_str());
            }

            g_videoPath = path;
            SaveVideoPath(path);

            if (!g_wallpapers.empty()) {
                g_monitorIndex = 0;
                for (auto& wp : g_wallpapers) {
                    bool hasOverride = false;
                    std::string newPath = GetVideoPathForMonitor(wp.index, hasOverride);
                    if (newPath.empty()) continue;
                    SwitchVideoOnMonitor(wp, newPath);
                    wp.hasOverride = hasOverride;
                }
                if (g_previewPlayer && !g_wallpapers.empty()) {
                    RefreshPreviewVideo(g_wallpapers[0].currentVideoPath);
                }
                std::cout << "[APPLY ] swapped video in place\n";
                return Ipc_Ok(id, { { "state", "playing" },{ "wallpaper", path } });
            }

            if (SetupWallpapers()) {
                if (g_previewPlayer) RefreshPreviewVideo(path);
                return Ipc_Ok(id, { { "state", "playing" },{ "wallpaper", path } });
            }
            return Ipc_Error(id, "internal_error", "Failed to set up wallpaper.");
        }

        if (type == "stop") {
            ReleaseAllWallpapers();
            return Ipc_Ok(id, { { "state", "stopped" } });
        }

        if (type == "get_version") {
            return Ipc_Ok(id, {
                { "engine", "0.1.0" },
                { "backend", "libmpv" }
                });
        }

        if (type == "list_monitors") {
            g_monitorList.clear();
            EnumDisplayMonitors(nullptr, nullptr, EnumMonitorListProc, 0);
            json arr = json::array();
            for (auto& m : g_monitorList) {
                json j;
                j["index"] = m.index;
                j["x"] = m.rect.left;
                j["y"] = m.rect.top;
                j["width"] = m.rect.right - m.rect.left;
                j["height"] = m.rect.bottom - m.rect.top;
                j["primary"] = m.primary;
                arr.push_back(j);
            }
            return Ipc_Ok(id, { { "monitors", arr } });
        }

        if (type == "get_library") {
            json arr = json::array();
            for (auto& p : g_libraryFiles) {
                json item;
                item["path"] = p;
                size_t slash = p.find_last_of("\\/");
                item["name"] = (slash == std::string::npos)
                    ? p : p.substr(slash + 1);
                arr.push_back(item);
            }
            return Ipc_Ok(id, {
                { "count", (int)g_libraryFiles.size() },
                { "files", arr }
                });
        }

        if (type == "rescan_library") {
            int count = RescanLibrary();
            return Ipc_Ok(id, { { "count", count } });
        }

        if (type == "add_library_folder") {
            std::string folder = req["body"].value("path", std::string(""));
            if (folder.empty() ||
                GetFileAttributesA(folder.c_str()) == INVALID_FILE_ATTRIBUTES) {
                return Ipc_Error(id, "folder_not_found",
                    "Folder does not exist: " + folder);
            }
            for (auto& f : g_libraryFolders)
                if (_stricmp(f.c_str(), folder.c_str()) == 0)
                    return Ipc_Ok(id, { { "added", false },
                                        { "reason", "already present" } });

            g_libraryFolders.push_back(folder);
            std::string joined;
            for (size_t i = 0; i < g_libraryFolders.size(); i++) {
                if (i) joined += ";";
                joined += g_libraryFolders[i];
            }
            WritePrivateProfileStringA("Library", "Folders",
                joined.c_str(), GetSettingsFilePath().c_str());
            int count = RescanLibrary();
            return Ipc_Ok(id, { { "added", true },{ "count", count } });
        }

        if (type == "remove_library_folder") {
            std::string folder = req["body"].value("path", std::string(""));
            bool removed = false;
            for (auto it = g_libraryFolders.begin();
                it != g_libraryFolders.end(); ++it) {
                if (_stricmp(it->c_str(), folder.c_str()) == 0) {
                    g_libraryFolders.erase(it);
                    removed = true;
                    break;
                }
            }
            if (removed) {
                std::string joined;
                for (size_t i = 0; i < g_libraryFolders.size(); i++) {
                    if (i) joined += ";";
                    joined += g_libraryFolders[i];
                }
                WritePrivateProfileStringA("Library", "Folders",
                    joined.c_str(), GetSettingsFilePath().c_str());
                RescanLibrary();
            }
            return Ipc_Ok(id, { { "removed", removed } });
        }

        // ── relocate_library_file ──
        // Renames an entry in the library list and rewrites any live
        // assignment (global, per-monitor, or lock) that pointed at the
        // old path.
        if (type == "relocate_library_file") {
            std::string oldPath = req["body"].value("old_path", std::string(""));
            std::string newPath = req["body"].value("new_path", std::string(""));

            if (oldPath.empty() || newPath.empty()) {
                return Ipc_Error(id, "bad_request",
                    "old_path and new_path are both required");
            }
            if (GetFileAttributesA(newPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
                return Ipc_Error(id, "file_not_found",
                    "New file does not exist: " + newPath);
            }

            bool renamed = false;
            for (auto& p : g_libraryFiles) {
                if (_stricmp(p.c_str(), oldPath.c_str()) == 0) {
                    p = newPath;
                    renamed = true;
                }
            }
            if (renamed) SaveLibraryToDisk();

            // Live in-memory assignments.
            if (!g_forceVideoPath.empty() &&
                _stricmp(g_forceVideoPath.c_str(), oldPath.c_str()) == 0) {
                g_forceVideoPath = newPath;
            }
            for (auto& kv : g_monitorForcePaths) {
                if (_stricmp(kv.second.c_str(), oldPath.c_str()) == 0)
                    kv.second = newPath;
            }
            if (!g_lockWallpaperPath.empty() &&
                _stricmp(g_lockWallpaperPath.c_str(), oldPath.c_str()) == 0) {
                g_lockWallpaperPath = newPath;
                WritePrivateProfileStringA("Lock", "WallpaperOverride",
                    newPath.c_str(), GetSettingsFilePath().c_str());
            }
            for (auto& kv : g_lockMonitorWallpaperPaths) {
                if (_stricmp(kv.second.c_str(), oldPath.c_str()) == 0) {
                    kv.second = newPath;
                    std::string section = "LockMonitor" + std::to_string(kv.first);
                    WritePrivateProfileStringA(section.c_str(), "VideoPath",
                        newPath.c_str(), GetSettingsFilePath().c_str());
                }
            }

            // Persisted assignments in settings.ini.
            std::string ini = GetSettingsFilePath();
            {
                char buf[MAX_PATH] = {};
                GetPrivateProfileStringA("Wallpaper", "VideoPath", "",
                    buf, MAX_PATH, ini.c_str());
                if (buf[0] != '\0' && _stricmp(buf, oldPath.c_str()) == 0) {
                    WritePrivateProfileStringA("Wallpaper", "VideoPath",
                        newPath.c_str(), ini.c_str());
                }
            }
            for (int i = 0; i < 8; i++) {
                std::string section = "Monitor" + std::to_string(i);
                char mbuf[MAX_PATH] = {};
                GetPrivateProfileStringA(section.c_str(), "VideoPath", "",
                    mbuf, MAX_PATH, ini.c_str());
                if (mbuf[0] != '\0' && _stricmp(mbuf, oldPath.c_str()) == 0) {
                    WritePrivateProfileStringA(section.c_str(), "VideoPath",
                        newPath.c_str(), ini.c_str());
                }
            }

            std::cout << "[RELOC] " << oldPath << " -> " << newPath
                << " (renamed=" << renamed << ")\n";

            return Ipc_Ok(id, {
                { "renamed", renamed },
                { "old_path", oldPath },
                { "new_path", newPath }
                });
        }

        if (type == "get_settings") {
            json body;
            body["idle_pause_seconds"] = g_idlePauseSeconds;
            body["preview_active"] = (g_hPreviewWnd != nullptr);
            body["target_fps"] = g_targetFps;
            body["pause_on_fullscreen"] = true;
            body["pause_on_battery"] = true;
            body["pause_on_rdp"] = true;
            body["slideshow_enabled"] = g_slideshow.enabled;
            body["slideshow_interval"] = g_slideshow.intervalMinutes;
            body["daynight_enabled"] = g_dayNight.enabled;
            body["auto_start"] = IsAutoStartEnabled();
            body["monitors"] = (int)g_wallpapers.size();

            // NEW
            body["quality"] = g_qualityMode;

            body["daynight_mode"] = g_dayNight.mode;
            body["daynight_manual_day_start"] = g_dayNight.manualDayStart;
            body["daynight_manual_night_start"] = g_dayNight.manualNightStart;
            body["daynight_latitude"] = g_dayNight.latitude;
            body["daynight_longitude"] = g_dayNight.longitude;
            body["daynight_prayer_method"] = g_dayNight.prayerMethod;
            body["daynight_duha_offset"] = g_dayNight.duhaOffsetMinutes;
            body["daynight_day_video"] = g_dayNight.dayVideo;
            body["daynight_night_video"] = g_dayNight.nightVideo;

            // Today's computed times (for Settings UI display)
            {
                std::string sr, mg;
                if (GetPrayerTimesForToday(sr, mg)) {
                    body["today_date"] = TodayDateString();
                    body["today_sunrise"] = sr;
                    body["today_maghrib"] = mg;
                }
            }
            body["lock_daynight_enabled"] = g_lockDayNight.enabled;
            body["lock_daynight_mode"] = g_lockDayNight.mode;

            body["lock_enabled"] = g_lockEnabled;
            body["lock_idle_seconds"] = g_lockIdleSeconds;
            body["lock_mode"] = g_lockMode;
            body["lock_has_password"] = !g_lockPasswordHash.empty();
            body["lock_use_daynight_videos"] = g_lockUseDayNightVideos;
            body["lock_day_video"] = g_lockDayVideo;
            body["lock_night_video"] = g_lockNightVideo;

            // ── New (Batch 7) ──
            body["monitor_count"] = (int)g_wallpapers.size();

            // Explicit assignment state for the panel's badges and the
            // Home hero.
            if (g_forceVideoPath.empty()) {
                body["global_assignment_path"] = json(nullptr);
            }
            else {
                body["global_assignment_path"] = g_forceVideoPath;
            }

            json monitorPaths = json::object();
            for (auto& kv : g_monitorForcePaths) {
                monitorPaths[std::to_string(kv.first)] = kv.second;
            }
            body["monitor_assignment_paths"] = monitorPaths;

            if (g_lockWallpaperPath.empty()) {
                body["lock_assignment_path"] = json(nullptr);
            }
            else {
                body["lock_assignment_path"] = g_lockWallpaperPath;
            }

            json lockMonitorPaths = json::object();
            for (auto& kv : g_lockMonitorWallpaperPaths) {
                lockMonitorPaths[std::to_string(kv.first)] = kv.second;
            }
            body["lock_monitor_assignment_paths"] = lockMonitorPaths;

            std::string currentWallpaper;
            for (auto& wp : g_wallpapers) {
                if (!wp.currentVideoPath.empty()) {
                    currentWallpaper = wp.currentVideoPath;
                    break;
                }
            }
            if (currentWallpaper.empty()) {
                if (!g_forceVideoPath.empty())
                    currentWallpaper = g_forceVideoPath;
                else
                    currentWallpaper = LoadVideoPath();
            }
            body["current_wallpaper"] = currentWallpaper;

            // report the ACTUAL fps from g_targetFps; if 0, send the sane default
            body["target_fps"] = (g_targetFps > 0) ? g_targetFps : 30;

            body["paused"] = g_manualPaused.load();
            body["turned_off"] = g_wallpapers.empty();
            body["display_mode"] = g_displayMode;

            body["lock_pause_on_lock"] = g_lockPauseOnLock;
            body["lock_dim_when_locked"] = g_lockDimWhenLocked;
            body["lock_show_clock"] = g_lockShowClock;
            body["lock_fill_mode"] = g_lockFillMode;
            body["lock_fps"] = g_lockFps;
            body["lock_current_wallpaper"] = g_lockWallpaperPath;

            body["shuffle_mode"] = g_shuffleMode;
            body["shuffle_favorite_count"] = (int)g_favoritePaths.size();
            return Ipc_Ok(id, body);
        }

        if (type == "preview_start") {
            StartPreview("");
            return Ipc_Ok(id, { { "preview", true } });
        }
        if (type == "preview_stop") {
            StopPreview();
            return Ipc_Ok(id, { { "preview", false } });
        }
        if (type == "preview_set") {
            std::string path = req["body"].value("path", std::string(""));
            if (path.empty() || !FileExists(path)) {
                return Ipc_Error(id, "file_not_found", "Video not found: " + path);
            }
            if (g_hPreviewWnd && g_previewPlayer) RefreshPreviewVideo(path);
            else StartPreview(path);
            return Ipc_Ok(id, { { "preview", true },{ "path", path } });
        }

        if (type == "set_fps") {
            int fps = req["body"].value("fps", 0);
            if (fps < 0) fps = 0;
            g_targetFps = fps;
            WritePrivateProfileStringA("Performance", "TargetFPS",
                std::to_string(fps).c_str(), GetSettingsFilePath().c_str());

            std::string filter = BuildVideoFilter();
            for (auto& wp : g_wallpapers) {
                if (wp.player) g_mpv.set_property_string(wp.player, "vf", filter.c_str());
            }
            if (g_previewPlayer)
                g_mpv.set_property_string(g_previewPlayer, "vf", filter.c_str());
            for (auto& lo : g_lockOverlays) {
                if (lo.player) g_mpv.set_property_string(lo.player, "vf", filter.c_str());
            }

            std::cout << "[FPS  ] applied live (no rebuild) vf=\"" << filter << "\"\n";
            return Ipc_Ok(id, { { "fps", fps } });
        }

        if (type == "set_idle_pause") {
            int sec = req["body"].value("seconds", 0);
            if (sec < 0) sec = 0;
            g_idlePauseSeconds = sec;
            WritePrivateProfileStringA("Performance", "IdlePauseSeconds",
                std::to_string(sec).c_str(), GetSettingsFilePath().c_str());
            return Ipc_Ok(id, { { "idle_pause_seconds", sec } });
        }

        if (type == "set_slideshow") {
            bool enabled = req["body"].value("enabled", false);
            int  interval = req["body"].value("interval_minutes", 10);
            if (interval < 1) interval = 1;
            g_slideshow.enabled = enabled;
            g_slideshow.intervalMinutes = interval;
            auto ini = GetSettingsFilePath();
            WritePrivateProfileStringA("Slideshow", "Enabled",
                enabled ? "1" : "0", ini.c_str());
            WritePrivateProfileStringA("Slideshow", "IntervalMinutes",
                std::to_string(interval).c_str(), ini.c_str());
            return Ipc_Ok(id, json::object());
        }

        if (type == "set_autostart") {
            bool enabled = req["body"].value("enabled", false);
            SetAutoStart(enabled);
            return Ipc_Ok(id, { { "enabled", enabled } });
        }

        if (type == "restore_wallpaper") {
            ReleaseAllWallpapers();
            g_forceVideoPath.clear();
            g_monitorForcePaths.clear();

            std::string ini = GetSettingsFilePath();
            bool any = false;
            for (int i = 0; i < 8; i++) {
                std::string section = "Monitor" + std::to_string(i);
                char buf[MAX_PATH] = {};
                GetPrivateProfileStringA(section.c_str(), "VideoPath", "",
                    buf, MAX_PATH, ini.c_str());
                if (buf[0] != '\0' && FileExists(buf)) {
                    g_monitorForcePaths[i] = buf;
                    any = true;
                }
            }
            if (!any) {
                std::string path = LoadVideoPath();
                if (path.empty() || !FileExists(path)) {
                    return Ipc_Error(id, "no_saved_wallpaper",
                        "No previously saved wallpaper to restore.");
                }
                g_forceVideoPath = path;
            }
            if (SetupWallpapers())
                return Ipc_Ok(id, { { "state", "playing" } });
            return Ipc_Error(id, "internal_error", "Failed to restore wallpaper.");
        }

        if (type == "clear_wallpaper") {
            ReleaseAllWallpapers();
            g_forceVideoPath.clear();
            g_monitorForcePaths.clear();
            return Ipc_Ok(id, { { "state", "cleared" } });
        }

        if (type == "set_display_mode") {
            std::string mode = req["body"].value("mode", "copy");
            if (mode != "copy" && mode != "stretch" && mode != "individual") {
                return Ipc_Error(id, "bad_request", "Invalid mode: " + mode);
            }
            g_displayMode = mode;
            WritePrivateProfileStringA("Wallpaper", "DisplayMode",
                mode.c_str(), GetSettingsFilePath().c_str());
            if (!g_wallpapers.empty() || !g_forceVideoPath.empty()
                || !g_monitorForcePaths.empty()) {
                ReleaseAllWallpapers();
                SetupWallpapers();
            }
            return Ipc_Ok(id, { { "mode", mode } });
        }

        if (type == "get_display_mode") {
            return Ipc_Ok(id, { { "mode", g_displayMode } });
        }

        // ============================================================
        // FEATURE 1 — Quality
        // ============================================================
        if (type == "set_quality") {
            std::string mode = req["body"].value("mode", std::string("balanced"));
            if (mode != "fast" && mode != "balanced"
                && mode != "high" && mode != "vsr") {
                return Ipc_Error(id, "bad_request", "Invalid quality mode: " + mode);
            }
            ApplyQualityChange(mode);
            return Ipc_Ok(id, { { "mode", mode } });
        }

        if (type == "get_quality_diagnostics") {
            json body;
            body["mode"] = g_qualityMode;
            body["vo"] = "gpu-next";
            body["hwdec"] = (g_qualityMode == "vsr") ? "d3d11va" : "no";
            body["vf"] = BuildVideoFilter();
            if (g_qualityMode == "fast") {
                body["scale"] = "bilinear";
                body["cscale"] = "bilinear";
            }
            else if (g_qualityMode == "balanced") {
                body["scale"] = "ewa_lanczossharp";
                body["cscale"] = "ewa_lanczossharp";
            }
            else if (g_qualityMode == "high") {
                body["scale"] = "ewa_lanczos4sharpest";
                body["cscale"] = "ewa_lanczos4sharpest";
            }
            else {
                body["scale"] = "vsr";
                body["cscale"] = "vsr";
            }
            return Ipc_Ok(id, body);
        }

        // ============================================================
        // FEATURE 2 — Day / Night
        // ============================================================
        if (type == "set_daynight_mode") {
            std::string mode = req["body"].value("mode", std::string("sunrise"));
            if (mode != "manual" && mode != "sunrise" && mode != "prayer") {
                return Ipc_Error(id, "bad_request", "Invalid mode: " + mode);
            }
            bool enabled = req["body"].value("enabled", g_dayNight.enabled);

            g_dayNight.mode = mode;
            g_dayNight.enabled = enabled;
            auto ini = GetSettingsFilePath();
            WritePrivateProfileStringA("DayNight", "Mode", mode.c_str(), ini.c_str());
            WritePrivateProfileStringA("DayNight", "Enabled",
                enabled ? "1" : "0", ini.c_str());

            // If switching to prayer and cache is stale/empty, kick a fetch.
            if (mode == "prayer") {
                PrayerCache cache = GetPrayerCacheSnapshot();
                time_t now = time(nullptr);
                bool stale = (cache.days.empty()) ||
                    ((time_t)cache.fetched_at + (time_t)kPrayerCacheTtlSeconds < now);
                bool paramChanged = (cache.method != g_dayNight.prayerMethod)
                    || (fabs(cache.latitude - g_dayNight.latitude) > 0.01)
                    || (fabs(cache.longitude - g_dayNight.longitude) > 0.01);
                if (stale || paramChanged) FetchPrayerTimesAsync();
            }

            // Recompute day/night immediately
            if (g_dayNight.enabled) {
                g_dayNight.isDay = IsCurrentlyDay();
                for (auto& wp : g_wallpapers) {
                    if (wp.hasOverride) continue;
                    std::string p = g_dayNight.isDay
                        ? g_dayNight.dayVideo : g_dayNight.nightVideo;
                    if (FileExists(p)) SwitchVideoOnMonitor(wp, p);
                }
            }
            RefreshLockOverlayVideo();
            return Ipc_Ok(id, { { "mode", mode },{ "enabled", enabled } });
        }

        if (type == "set_daynight_manual") {
            std::string ds = req["body"].value("day_start", std::string("07:00"));
            std::string ns = req["body"].value("night_start", std::string("19:00"));
            int h, m;
            if (!ParseTimeHHMM(ds, h, m))
                return Ipc_Error(id, "bad_request", "Invalid day_start: " + ds);
            if (!ParseTimeHHMM(ns, h, m))
                return Ipc_Error(id, "bad_request", "Invalid night_start: " + ns);

            g_dayNight.manualDayStart = ds;
            g_dayNight.manualNightStart = ns;
            auto ini = GetSettingsFilePath();
            WritePrivateProfileStringA("DayNight", "ManualDayStart",
                ds.c_str(), ini.c_str());
            WritePrivateProfileStringA("DayNight", "ManualNightStart",
                ns.c_str(), ini.c_str());
            return Ipc_Ok(id, json::object());
        }

        if (type == "set_daynight_prayer") {
            double lat = req["body"].value("latitude", g_dayNight.latitude);
            double lng = req["body"].value("longitude", g_dayNight.longitude);
            int method = req["body"].value("method", g_dayNight.prayerMethod);
            int duha = req["body"].value("duha_offset_minutes",
                g_dayNight.duhaOffsetMinutes);

            g_dayNight.latitude = lat;
            g_dayNight.longitude = lng;
            g_dayNight.prayerMethod = method;
            g_dayNight.duhaOffsetMinutes = duha;

            auto ini = GetSettingsFilePath();
            char buf[64];
            sprintf_s(buf, "%.6f", lat);
            WritePrivateProfileStringA("DayNight", "Latitude", buf, ini.c_str());
            sprintf_s(buf, "%.6f", lng);
            WritePrivateProfileStringA("DayNight", "Longitude", buf, ini.c_str());
            WritePrivateProfileStringA("DayNight", "PrayerMethod",
                std::to_string(method).c_str(), ini.c_str());
            WritePrivateProfileStringA("DayNight", "DuhaOffsetMinutes",
                std::to_string(duha).c_str(), ini.c_str());

            // Re-fetch (params changed).
            if (g_dayNight.mode == "prayer") FetchPrayerTimesAsync();

            return Ipc_Ok(id, {
                { "latitude", lat },
                { "longitude", lng },
                { "method", method },
                { "duha_offset_minutes", duha }
                });
        }

        if (type == "set_daynight_videos") {
            std::string dv = req["body"].value("day_video", g_dayNight.dayVideo);
            std::string nv = req["body"].value("night_video", g_dayNight.nightVideo);
            g_dayNight.dayVideo = dv;
            g_dayNight.nightVideo = nv;
            auto ini = GetSettingsFilePath();
            WritePrivateProfileStringA("DayNight", "DayVideo", dv.c_str(), ini.c_str());
            WritePrivateProfileStringA("DayNight", "NightVideo", nv.c_str(), ini.c_str());
            RefreshLockOverlayVideo();
            return Ipc_Ok(id, { { "day_video", dv },{ "night_video", nv } });
        }

        if (type == "get_daynight_config") {
            json body;
            body["enabled"] = g_dayNight.enabled;
            body["mode"] = g_dayNight.mode;
            body["day_video"] = g_dayNight.dayVideo;
            body["night_video"] = g_dayNight.nightVideo;
            body["manual_day_start"] = g_dayNight.manualDayStart;
            body["manual_night_start"] = g_dayNight.manualNightStart;
            body["latitude"] = g_dayNight.latitude;
            body["longitude"] = g_dayNight.longitude;
            body["prayer_method"] = g_dayNight.prayerMethod;
            body["duha_offset_minutes"] = g_dayNight.duhaOffsetMinutes;

            // Optional diagnostics
            std::string sunrise, maghrib;
            if (GetPrayerTimesForToday(sunrise, maghrib)) {
                body["today_sunrise"] = sunrise;
                body["today_maghrib"] = maghrib;
            }
            PrayerCache cache = GetPrayerCacheSnapshot();
            body["cache_days"] = (int)cache.days.size();
            body["cache_fetched_at"] = (long long)cache.fetched_at;
            body["is_day_now"] = g_dayNight.isDay;
            return Ipc_Ok(id, body);
        }

        if (type == "refresh_prayer_times") {
            if (g_dayNight.mode != "prayer")
                return Ipc_Error(id, "bad_request", "Not in prayer mode");
            FetchPrayerTimesAsync();
            return Ipc_Ok(id, { { "queued", true } });
        }

        // ============================================================
        // FEATURE 3 — Lock overlay
        // ============================================================
        if (type == "set_lock_config") {
            if (g_lockActive && LockPasswordRequired()) {
                bool weakensProtection = false;
                if (req["body"].contains("mode")
                    && req["body"]["mode"].is_string()
                    && req["body"]["mode"].get<std::string>() == "screensaver") {
                    weakensProtection = true;
                }
                if (req["body"].contains("password")) {
                    weakensProtection = true;
                }
                if (req["body"].value("clear_password", false))
                    weakensProtection = true;

                if (weakensProtection) {
                    if (!req["body"].contains("password")
                        || !req["body"]["password"].is_string()
                        || Sha256Hex(req["body"]["password"].get<std::string>())
                            != g_lockPasswordHash) {
                        return Ipc_Error(id, "password_required",
                            "The active lock requires the current password");
                    }
                }
            }

            if (req["body"].contains("enabled"))
                g_lockEnabled = req["body"]["enabled"].get<bool>();
            if (req["body"].contains("idle_seconds")) {
                int sec = req["body"]["idle_seconds"].get<int>();
                if (sec < 10) sec = 10;
                g_lockIdleSeconds = sec;
            }
            if (req["body"].contains("mode")) {
                std::string m = req["body"]["mode"].get<std::string>();
                if (m == "screensaver" || m == "password") g_lockMode = m;
            }
            if (req["body"].contains("password")) {
                std::string pw = req["body"]["password"].get<std::string>();
                if (pw.empty()) {
                    g_lockPasswordHash.clear();
                }
                else {
                    g_lockPasswordHash = Sha256Hex(pw);
                }
            }
            if (req["body"].contains("clear_password")) {
                if (req["body"]["clear_password"].get<bool>())
                    g_lockPasswordHash.clear();
            }
            if (req["body"].contains("use_daynight_videos"))
                g_lockUseDayNightVideos = req["body"]["use_daynight_videos"].get<bool>();
            if (req["body"].contains("day_video"))
                g_lockDayVideo = req["body"]["day_video"].get<std::string>();
            if (req["body"].contains("night_video"))
                g_lockNightVideo = req["body"]["night_video"].get<std::string>();

            // ── New (Batch 7) ──
            if (req["body"].contains("pause_on_lock"))
                g_lockPauseOnLock = req["body"]["pause_on_lock"].get<bool>();
            if (req["body"].contains("dim_when_locked"))
                g_lockDimWhenLocked = req["body"]["dim_when_locked"].get<bool>();
            if (req["body"].contains("show_clock"))
                g_lockShowClock = req["body"]["show_clock"].get<bool>();
            if (req["body"].contains("fill_mode")) {
                std::string fm = req["body"]["fill_mode"].get<std::string>();
                if (fm == "fill" || fm == "fit" || fm == "stretch" || fm == "center")
                    g_lockFillMode = fm;
            }
            if (req["body"].contains("fps")) {
                int f = req["body"]["fps"].get<int>();
                if (f >= 0) g_lockFps = f;
            }

            // Runtime coercion: password mode with no hash is unusable —
            // the prompt will show but no password will ever dismiss it.
            // Force screensaver and tell the panel what actually stuck.
            bool coerced = false;
            if (g_lockMode == "password" && g_lockPasswordHash.empty()) {
                std::cout << "[LOCK ] set_lock_config requested password mode "
                    "with empty hash; coercing to screensaver\n";
                g_lockMode = "screensaver";
                coerced = true;
            }

            auto ini = GetSettingsFilePath();
            WritePrivateProfileStringA("Lock", "Enabled",
                g_lockEnabled ? "1" : "0", ini.c_str());
            WritePrivateProfileStringA("Lock", "IdleSeconds",
                std::to_string(g_lockIdleSeconds).c_str(), ini.c_str());
            WritePrivateProfileStringA("Lock", "Mode",
                g_lockMode.c_str(), ini.c_str());
            WritePrivateProfileStringA("Lock", "PasswordHash",
                g_lockPasswordHash.c_str(), ini.c_str());
            WritePrivateProfileStringA("Lock", "UseDayNightVideos",
                g_lockUseDayNightVideos ? "1" : "0", ini.c_str());
            WritePrivateProfileStringA("Lock", "DayVideo",
                g_lockDayVideo.c_str(), ini.c_str());
            WritePrivateProfileStringA("Lock", "NightVideo",
                g_lockNightVideo.c_str(), ini.c_str());

            // ── New (Batch 7) ──
            WritePrivateProfileStringA("Lock", "PauseOnLock",
                g_lockPauseOnLock ? "1" : "0", ini.c_str());
            WritePrivateProfileStringA("Lock", "DimWhenLocked",
                g_lockDimWhenLocked ? "1" : "0", ini.c_str());
            WritePrivateProfileStringA("Lock", "ShowClock",
                g_lockShowClock ? "1" : "0", ini.c_str());
            WritePrivateProfileStringA("Lock", "FillMode",
                g_lockFillMode.c_str(), ini.c_str());
            WritePrivateProfileStringA("Lock", "Fps",
                std::to_string(g_lockFps).c_str(), ini.c_str());

            // Apply dim/brightness live to any active overlay
            for (auto& lo : g_lockOverlays) {
                if (lo.player) {
                    g_mpv.set_property_string(lo.player, "brightness",
                        g_lockDimWhenLocked ? "-30" : "0");
                }
            }

            // Report the effective mode back to the panel. If we
            // coerced, the panel should update its toggle to match.
            json reply;
            reply["mode"] = g_lockMode;
            reply["has_password"] = !g_lockPasswordHash.empty();
            reply["coerced"] = coerced;
            return Ipc_Ok(id, reply);
        }

        if (type == "get_lock_config") {
            json body;
            body["enabled"] = g_lockEnabled;
            body["idle_seconds"] = g_lockIdleSeconds;
            body["mode"] = g_lockMode;
            body["has_password"] = !g_lockPasswordHash.empty();
            body["use_daynight_videos"] = g_lockUseDayNightVideos;
            body["day_video"] = g_lockDayVideo;
            body["night_video"] = g_lockNightVideo;
            body["active"] = g_lockActive;
            return Ipc_Ok(id, body);
        }

        if (type == "set_lock_daynight") {
            if (req["body"].contains("enabled"))
                g_lockDayNight.enabled = req["body"]["enabled"].get<bool>();
            if (req["body"].contains("mode")) {
                std::string m = req["body"]["mode"].get<std::string>();
                if (m == "manual" || m == "sunrise" || m == "prayer")
                    g_lockDayNight.mode = m;
            }
            if (req["body"].contains("manual_day_start"))
                g_lockDayNight.manualDayStart = req["body"]["manual_day_start"].get<std::string>();
            if (req["body"].contains("manual_night_start"))
                g_lockDayNight.manualNightStart = req["body"]["manual_night_start"].get<std::string>();
            if (req["body"].contains("day_video"))
                g_lockDayNight.dayVideo = req["body"]["day_video"].get<std::string>();
            if (req["body"].contains("night_video"))
                g_lockDayNight.nightVideo = req["body"]["night_video"].get<std::string>();
            if (req["body"].contains("duha_offset_minutes"))
                g_lockDayNight.duhaOffsetMinutes =
                req["body"]["duha_offset_minutes"].get<int>();

            SaveLockDayNightConfig();
            std::cout << "[LOCK ] daynight updated: enabled=" << g_lockDayNight.enabled
                << " mode=" << g_lockDayNight.mode
                << " dayVideo='" << g_lockDayNight.dayVideo << "'"
                << " nightVideo='" << g_lockDayNight.nightVideo << "'\n";
            RefreshLockOverlayVideo();
            return Ipc_Ok(id, json::object());
        }

        if (type == "get_lock_daynight") {
            json body;
            body["enabled"] = g_lockDayNight.enabled;
            body["mode"] = g_lockDayNight.mode;
            body["manual_day_start"] = g_lockDayNight.manualDayStart;
            body["manual_night_start"] = g_lockDayNight.manualNightStart;
            body["day_video"] = g_lockDayNight.dayVideo;
            body["night_video"] = g_lockDayNight.nightVideo;
            body["duha_offset_minutes"] = g_lockDayNight.duhaOffsetMinutes;
            body["latitude"] = g_dayNight.latitude;
            body["longitude"] = g_dayNight.longitude;
            body["prayer_method"] = g_dayNight.prayerMethod;
            body["is_day_now"] = IsCurrentlyDayFor(g_lockDayNight);

            std::string sunrise, maghrib;
            if (GetPrayerTimesForToday(sunrise, maghrib)) {
                body["today_date"] = TodayDateString();
                body["today_sunrise"] = sunrise;
                body["today_maghrib"] = maghrib;
            }
            return Ipc_Ok(id, body);
        }

        if (type == "lock_now") {
            if (!g_lockActive) StartLockOverlay();
            return Ipc_Ok(id, { { "active", true } });
        }

        if (type == "lock_dismiss") {
            if (g_lockMode == "password") {
                if (g_lockPasswordHash.empty()) {
                    return Ipc_Error(id, "password_not_configured",
                        "Password mode has no configured password");
                }
                if (!req["body"].contains("password")
                    || !req["body"]["password"].is_string()) {
                    return Ipc_Error(id, "password_required",
                        "A password is required to dismiss the lock");
                }
                std::string pw = req["body"]["password"].get<std::string>();
                if (Sha256Hex(pw) != g_lockPasswordHash) {
                    return Ipc_Error(id, "bad_password", "Wrong password");
                }
            }
            DismissLockOverlay();
            return Ipc_Ok(id, { { "active", false } });
        }

        if (type == "shutdown_engine") {
            PostQuitMessage(0);
            return Ipc_Ok(id, { { "shutting_down", true } });
        }

        // ════════════════════════════════════════════════════════
        // Batch 7 — new commands
        // ════════════════════════════════════════════════════════

        if (type == "get_wallpaper_metadata") {
            std::string path = req["body"].value("path", std::string(""));
            if (path.empty() || !FileExists(path)) {
                return Ipc_Error(id, "file_not_found", "Not found: " + path);
            }
            json body;
            body["path"] = path;

            std::string ext;
            size_t dot = path.find_last_of('.');
            if (dot != std::string::npos) {
                ext = path.substr(dot);
                for (auto& c : ext) c = (char)tolower(c);
            }
            if (ext == ".model3.json" || ext == ".moc3" || ext == ".motion3.json")
                body["media_type"] = "live2d";
            else
                body["media_type"] = "video";

            WIN32_FILE_ATTRIBUTE_DATA fad{};
            if (GetFileAttributesExA(path.c_str(), GetFileExInfoStandard, &fad)) {
                ULARGE_INTEGER sz;
                sz.LowPart = fad.nFileSizeLow;
                sz.HighPart = fad.nFileSizeHigh;
                body["file_size"] = (unsigned long long)sz.QuadPart;
            }
            return Ipc_Ok(id, body);
        }

        if (type == "validate_library") {
            json missing = json::array();
            int presentCount = 0;
            for (auto& p : g_libraryFiles) {
                if (FileExists(p)) presentCount++;
                else missing.push_back(p);
            }
            return Ipc_Ok(id, {
                { "missing", missing },
                { "present_count", presentCount },
                { "missing_count", (int)missing.size() }
                });
        }

        if (type == "set_shuffle") {
            // New shape:
            //   { "mode": "off"|"random"|"weighted"|"favorites_only",
            //     "favorites": ["path1", "path2", ...]   // optional
            //   }
            // Legacy shape still accepted:
            //   { "enabled": true|false }  →  "random" / "off"
            std::string mode = req["body"].value("mode", std::string(""));
            if (mode.empty()) {
                bool enabled = req["body"].value("enabled", false);
                mode = enabled ? "random" : "off";
            }
            if (mode != "off" && mode != "random"
                && mode != "weighted" && mode != "favorites_only") {
                return Ipc_Error(id, "bad_request",
                    "Invalid shuffle mode: " + mode);
            }
            g_shuffleMode = mode;
            g_slideshow.random = (mode != "off");

            // Replace the favorites set only when the caller sent one.
            // Panel pushes the current list on every mode change and
            // every star toggle, so this stays in sync.
            if (req["body"].contains("favorites")
                && req["body"]["favorites"].is_array()) {
                g_favoritePaths.clear();
                for (auto& f : req["body"]["favorites"]) {
                    std::string p = f.get<std::string>();
                    if (!p.empty()) g_favoritePaths.insert(p);
                }
            }

            auto ini = GetSettingsFilePath();
            WritePrivateProfileStringA("Performance", "ShuffleMode",
                g_shuffleMode.c_str(), ini.c_str());
            WritePrivateProfileStringA("Slideshow", "Random",
                (mode != "off") ? "1" : "0", ini.c_str());

            std::string joinedFavs;
            bool first = true;
            for (auto& f : g_favoritePaths) {
                if (!first) joinedFavs += ";";
                joinedFavs += f;
                first = false;
            }
            WritePrivateProfileStringA("Performance", "Favorites",
                joinedFavs.c_str(), ini.c_str());

            std::cout << "[SHUFFLE] mode=" << g_shuffleMode
                << " favorites=" << g_favoritePaths.size() << "\n";

            return Ipc_Ok(id, {
                { "mode", g_shuffleMode },
                { "enabled", mode != "off" },
                { "favorite_count", (int)g_favoritePaths.size() }
                });
        }

        if (type == "set_performance_defaults") {
            auto ini = GetSettingsFilePath();

            if (req["body"].contains("fps")) {
                int f = req["body"]["fps"].get<int>();
                if (f < 0) f = 0;
                g_targetFps = f;
                WritePrivateProfileStringA("Performance", "TargetFPS",
                    std::to_string(f).c_str(), ini.c_str());
                std::string filter = BuildVideoFilter();
                for (auto& wp : g_wallpapers) {
                    if (wp.player)
                        g_mpv.set_property_string(wp.player, "vf", filter.c_str());
                }
                if (g_previewPlayer)
                    g_mpv.set_property_string(g_previewPlayer, "vf", filter.c_str());
            }

            if (req["body"].contains("quality")) {
                std::string q = req["body"]["quality"].get<std::string>();
                if (q == "low")          q = "fast";
                else if (q == "medium")  q = "balanced";
                else if (q == "ultra")   q = "high";

                if (q == "fast" || q == "balanced" || q == "high" || q == "vsr") {
                    if (q != g_qualityMode) ApplyQualityChange(q);
                }
            }

            if (req["body"].contains("display_mode")) {
                std::string m = req["body"]["display_mode"].get<std::string>();
                if (m == "copy" || m == "stretch" || m == "individual") {
                    g_displayMode = m;
                    WritePrivateProfileStringA("Wallpaper", "DisplayMode",
                        m.c_str(), ini.c_str());
                }
            }
            return Ipc_Ok(id, json::object());
        }

        if (type == "set_lock_wallpaper") {
            std::string path = req["body"].value("path", std::string(""));
            std::string monitor = req["body"].value("monitor", std::string("all"));

            if (path.empty() || !FileExists(path)) {
                return Ipc_Error(id, "file_not_found", "Not found: " + path);
            }

            std::string ini = GetSettingsFilePath();

            if (monitor == "all" || monitor.empty()) {
                // Global lock wallpaper. Clears per-monitor overrides,
                // matching the "Both" semantics for desktop wallpapers.
                g_lockWallpaperPath = path;
                g_lockMonitorWallpaperPaths.clear();
                WritePrivateProfileStringA("Lock", "WallpaperOverride",
                    path.c_str(), ini.c_str());
                for (int i = 0; i < 8; i++) {
                    std::string section = "LockMonitor" + std::to_string(i);
                    WritePrivateProfileStringA(section.c_str(), "VideoPath",
                        nullptr, ini.c_str());
                }
            }
            else {
                int idx = -1;
                try { idx = std::stoi(monitor); }
                catch (...) {
                    return Ipc_Error(id, "bad_request",
                        "Invalid monitor value: " + monitor);
                }
                g_lockMonitorWallpaperPaths[idx] = path;
                std::string section = "LockMonitor" + std::to_string(idx);
                WritePrivateProfileStringA(section.c_str(), "VideoPath",
                    path.c_str(), ini.c_str());
                // Global lock override is preserved as the fallback for
                // monitors without their own entry.
            }

            // If overlay is up, resolve and swap each monitor's video
            // in place. GetLockVideoPath applies per-monitor priority.
            if (g_lockActive) {
                for (auto& lo : g_lockOverlays) {
                    std::string resolved;
                    if (GetLockVideoPath(resolved, lo.index) && lo.player) {
                        const char* cmd[] = { "loadfile", resolved.c_str(), "replace", nullptr };
                        g_mpv.command(lo.player, cmd);
                    }
                }
            }
            return Ipc_Ok(id, { { "path", path },{ "monitor", monitor } });
        }

        // Panel-side aliases for the on/off toggle on Home.
        //
        // turn_off now fully quits the engine, matching the tray Exit
        // menu item. The response is sent back on the pipe before the
        // message loop processes WM_QUIT, so the panel sees {state:off}
        // and can transition its UI to "engine offline".
        //
        // turn_on is kept as a no-op success so any stale panel that
        // still sends it gets a clean response; the real "start again"
        // path on the panel side re-launches the process, matching
        // Start Engine.
        if (type == "turn_off") {
            ReleaseAllWallpapers();
            PostQuitMessage(0);
            return Ipc_Ok(id, { { "state", "off" } });
        }
        if (type == "turn_on") {
            return Ipc_Ok(id, { { "state", "on" },
                                { "note", "engine running" } });
        }

        return Ipc_Error(id, "unknown_command", "Unknown type: " + type);
    }
    catch (const std::exception& e) {
        return Ipc_Error(id, "bad_request",
            std::string("Parse error: ") + e.what());
    }
}

// =====================================================================
// main
// =====================================================================
int main() {
    g_wmShowExisting = RegisterWindowMessageW(L"BlueprintEngine_ShowExisting");

    SingleInstanceResult singleResult = AcquireSingleInstance();
    if (singleResult == SingleInstanceResult::SecondInstance) return 0;

#ifndef DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
#define DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 ((HANDLE)-4)
#endif
    typedef BOOL(WINAPI* SetProcessDpiAwarenessContextProc)(HANDLE);
    HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
    if (hUser32) {
        auto pSetDpi = reinterpret_cast<SetProcessDpiAwarenessContextProc>(
            GetProcAddress(hUser32, "SetProcessDpiAwarenessContext"));
        if (pSetDpi) pSetDpi(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
    SetProcessDPIAware();

    srand((unsigned)GetTickCount64());


    bool keepConsole = CommandLineHasFlag(L"--console");
    if (!keepConsole) RedirectOutputAndHideConsole();

    // Diagnostic: identify the exact engine process and executable.
    char exePath[MAX_PATH] = {};
    DWORD pathLength = GetModuleFileNameA(nullptr, exePath, MAX_PATH);

    std::cout << "[PROCESS] PID=" << GetCurrentProcessId() << "\n";
    if (pathLength > 0 && pathLength < MAX_PATH) {
        std::cout << "[PROCESS] EXE=\"" << exePath << "\"\n";
    }

    std::cout << "Starting Blueprint Engine...\n";

    std::cout << "Log file: " << GetLogFilePath() << "\n";
    std::cout << "Single-instance mutex acquired.\n";

    if (!LoadMpvDll()) {
        std::cout << "[FATAL ] Failed to load libmpv-2.dll. "
            "Make sure it is next to the executable.\n";
        MessageBoxW(nullptr,
            L"Failed to load libmpv-2.dll.\n"
            L"Make sure the file is next to Blueprintengine.exe.",
            L"Blueprint Engine", MB_ICONERROR);
        ReleaseSingleInstance();
        return 1;
    }
    std::cout << "libmpv loaded OK.\n";

    WNDCLASSW wc = {};
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WallpaperWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    wc.lpszClassName = L"BlueprintWallpaperClass";
    if (!RegisterClassW(&wc)) {
        std::cout << "RegisterClassW failed. Error=" << GetLastError() << "\n";
        UnloadMpvDll();
        ReleaseSingleInstance();
        return 1;
    }

    // Lock overlay window class
    WNDCLASSW wcLock = {};
    wcLock.style = CS_HREDRAW | CS_VREDRAW;
    wcLock.lpfnWndProc = LockOverlayWndProc;
    wcLock.hInstance = GetModuleHandleW(nullptr);
    wcLock.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    wcLock.lpszClassName = kLockWndClass;
    if (!RegisterClassW(&wcLock)) {
        std::cout << "[WARN  ] Lock class registration failed: "
            << GetLastError() << "\n";
    }

    if (!CreateHiddenWindowAndTray(GetModuleHandleW(nullptr))) {
        std::cout << "[WARN  ] Tray icon failed. Engine will still run.\n";
    }

    PipeServer_Start(g_hHiddenWnd);

    // ── Load configuration ──
    g_idlePauseSeconds = LoadIdlePauseSeconds();
    g_qualityMode = LoadQualityMode();

    {
        char fpsBuf[16] = {};
        GetPrivateProfileStringA("Performance", "TargetFPS", "25",
            fpsBuf, 16, GetSettingsFilePath().c_str());
        g_targetFps = std::atoi(fpsBuf);
        std::cout << "Target FPS      : "
            << (g_targetFps > 0 ? std::to_string(g_targetFps) : "unlimited") << "\n";
        std::cout << "Quality         : " << g_qualityMode << "\n";
    }

    g_slideshow = LoadSlideshowConfig();
    g_dayNight = LoadDayNightConfig();
    g_lockDayNight = LoadLockDayNightConfig();

    {
        char modeBuf[32] = {};
        GetPrivateProfileStringA("Wallpaper", "DisplayMode", "copy",
            modeBuf, 32, GetSettingsFilePath().c_str());
        g_displayMode = modeBuf;
        std::cout << "Display mode    : " << g_displayMode << "\n";
    }

    // Lock config + prayer cache
    LoadLockConfig();
    bool havePrayerCache = LoadPrayerCache();

    // Startup balloon opt-in. Default off.
    {
        char balloonBuf[8] = {};
        GetPrivateProfileStringA("Engine", "ShowStartupBalloon", "0",
            balloonBuf, 8, GetSettingsFilePath().c_str());
        g_showStartupBalloon = (std::atoi(balloonBuf) != 0);
        std::cout << "Startup balloon : "
            << (g_showStartupBalloon ? "enabled" : "disabled") << "\n";
    }

    // Kick off background fetch if we're in prayer mode and the cache is
    // missing or stale. Otherwise leave it alone (it'll be fetched when
    // the user switches modes).
    if (g_dayNight.enabled && g_dayNight.mode == "prayer") {
        PrayerCache cache = GetPrayerCacheSnapshot();
        time_t now = time(nullptr);
        bool stale = !havePrayerCache ||
            ((time_t)cache.fetched_at + (time_t)kPrayerCacheTtlSeconds < now);
        if (stale) {
            std::cout << "[PRAYER] cache is "
                << (havePrayerCache ? "stale" : "missing")
                << ", scheduling background fetch\n";
            FetchPrayerTimesAsync();
        }
    }

    LoadLibraryFolders();
    RescanLibrary();

    std::cout << "Idle-pause      : " << g_idlePauseSeconds << " seconds\n";
    std::cout << "Slideshow       : " << (g_slideshow.enabled ? "enabled" : "disabled")
        << " (" << g_slideshow.playlist.size() << " videos)\n";
    std::cout << "Day/Night       : " << (g_dayNight.enabled ? "enabled" : "disabled")
        << " (mode=" << g_dayNight.mode << ")\n";
    std::cout << "Lock overlay    : " << (g_lockEnabled ? "enabled" : "disabled")
        << " (idle=" << g_lockIdleSeconds << "s, mode=" << g_lockMode << ")\n";
    std::cout << "Auto-start      : " << (IsAutoStartEnabled() ? "enabled" : "disabled") << "\n";

    std::string ini = GetSettingsFilePath();
    char fr[8] = {};
    GetPrivateProfileStringA("Engine", "FirstRunDone", "", fr, 8, ini.c_str());
    if (fr[0] == '\0') {
        if (!IsAutoStartEnabled()) SetAutoStart(true);
        WritePrivateProfileStringA("Engine", "FirstRunDone", "1", ini.c_str());
        std::cout << "[AUTOSTART] First run - enabled auto-start by default.\n";
    }

    if (g_dayNight.enabled) {
        CalculateSunTimes(g_dayNight.latitude, g_dayNight.longitude,
            g_cachedSunrise, g_cachedSunset);
        g_lastSunCalcTick = GetTickCount64();
        g_dayNight.isDay = IsCurrentlyDay();
        std::cout << "Sunrise today   : " << g_cachedSunrise << "h\n";
        std::cout << "Sunset  today   : " << g_cachedSunset << "h\n";
        std::cout << "Currently       : " << (g_dayNight.isDay ? "DAY" : "NIGHT") << "\n";
    }

    bool anyVideoConfigured = false;
    if (!LoadVideoPath().empty()) anyVideoConfigured = true;
    if (!g_slideshow.playlist.empty()) anyVideoConfigured = true;
    if (g_dayNight.enabled &&
        (!g_dayNight.dayVideo.empty() || !g_dayNight.nightVideo.empty()))
        anyVideoConfigured = true;

    if (!anyVideoConfigured) {
        std::cout << "No video configured. Opening file picker...\n";
        std::string picked = PromptForVideoFile();
        if (picked.empty()) {
            std::cout << "No video selected. Exiting.\n";
            StopPrayerFetch();
            DestroyTrayAndHiddenWindow();
            UnloadMpvDll();
            ReleaseSingleInstance();
            return 1;
        }
        SaveVideoPath(picked);
        std::cout << "Saved to settings: " << picked << "\n";
    }

    {
        std::string ini = GetSettingsFilePath();
        for (int i = 0; i < 8; i++) {
            std::string section = "Monitor" + std::to_string(i);
            char buf[MAX_PATH] = {};
            GetPrivateProfileStringA(section.c_str(), "VideoPath", "",
                buf, MAX_PATH, ini.c_str());
            if (buf[0] != '\0') {
                if (FileExists(buf)) {
                    g_monitorForcePaths[i] = buf;
                    std::cout << "[RESTORE] Monitor " << i << " -> " << buf << "\n";
                }
                else {
                    std::cout << "[RESTORE] Monitor " << i
                        << " path missing, skipping: " << buf << "\n";
                }
            }
        }

        // Restore the persisted "Both monitors" assignment so
        // get_settings reports it correctly after a restart.
        if (g_forceVideoPath.empty()) {
            std::string w = LoadVideoPath();
            if (!w.empty() && FileExists(w)) {
                g_forceVideoPath = w;
                std::cout << "[RESTORE] Global assignment -> " << w << "\n";
            }
        }
    }

    std::cout << "Enumerating monitors...\n";
    if (!SetupWallpapers()) std::cout << "No wallpapers created.\n";

    std::cout << "SUCCESS! Engine running. Use the tray icon to control it.\n";

    bool running = true;
    ULONGLONG lastWarmup = 0;
    ULONGLONG lastPause = 0;
    ULONGLONG lastExplorerCheck = 0;
    ULONGLONG lastSlideshowCheck = 0;
    ULONGLONG lastDayNightCheck = 0;
    ULONGLONG lastLockCheck = 0;
    ULONGLONG lastPrayerRefetch = GetTickCount64();

    while (running) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) { running = false; break; }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        ULONGLONG now = GetTickCount64();

        if ((now - lastWarmup) >= 250) {
            for (auto& wp : g_wallpapers) {
                if (now < wp.warmupUntil) UpdateVlcDisplay(wp.hwnd, wp.player);
            }
            lastWarmup = now;
        }

        if (now - lastPause >= kPauseCheckMs) {
            UpdatePauseState();
            lastPause = now;
        }

        if (now - lastSlideshowCheck >= kSlideshowCheckMs) {
            UpdateSlideshow();
            lastSlideshowCheck = now;
        }

        if (now - lastDayNightCheck >= kDayNightCheckMs) {
            UpdateDayNight();
            lastDayNightCheck = now;
        }

        // ---- Lock overlay idle trigger ----
        if (now - lastLockCheck >= kLockCheckMs) {
            lastLockCheck = now;
            if (g_lockEnabled && !g_lockActive) {
                int idleSec = GetIdleSeconds();
                if (idleSec >= g_lockIdleSeconds) {
                    std::cout << "[LOCK ] idle " << idleSec
                        << "s >= " << g_lockIdleSeconds << "s; showing overlay\n";
                    StartLockOverlay();
                }
            }
        }

        // ---- Prayer cache age watchdog (every 15 min) ----
        if (now - lastPrayerRefetch >= 15ULL * 60ULL * 1000ULL) {
            lastPrayerRefetch = now;
            if (g_dayNight.enabled && g_dayNight.mode == "prayer"
                && !g_prayerFetchInProgress.load()) {
                PrayerCache cache = GetPrayerCacheSnapshot();
                time_t t = time(nullptr);
                bool stale = cache.days.empty() ||
                    ((time_t)cache.fetched_at +
                        (time_t)kPrayerCacheTtlSeconds < t);
                if (stale) {
                    std::cout << "[PRAYER] cache stale; re-fetching in background\n";
                    FetchPrayerTimesAsync();
                }
            }
        }

        if (now - lastExplorerCheck >= kExplorerCheckMs) {
            lastExplorerCheck = now;
            bool workerDead = !g_hWorkerW || !IsWindow(g_hWorkerW);
            bool anyChildDead = false;
            for (auto& wp : g_wallpapers) {
                if (!wp.hwnd || !IsWindow(wp.hwnd)) { anyChildDead = true; break; }
            }
            if (workerDead || anyChildDead) {
                std::cout << "[REEMBED] Explorer restarted. Re-embedding (fast path)...\n";
                ReleaseAllWallpapers();
                Sleep(1000);
                // Fast path: single WorkerW attempt, no 200ms retry loop.
                // Explorer is already up (we only got here because it restarted).
                if (SetupWallpapers(/*fastReembed=*/true)) {
                    std::cout << "[REEMBED] Success.\n";
                    lastWarmup = 0; lastPause = 0;
                    UpdatePauseState();
                }
                else {
                    std::cout << "[REEMBED] Fast path failed; full retry next tick.\n";
                    // Set lastExplorerCheck so we retry on next 1s tick, not 12s.
                    lastExplorerCheck = 0;
                }
            }
        }

        {
            static ULONGLONG lastCpCheck = 0;
            if (now - lastCpCheck >= 250) {
                lastCpCheck = now;
                if (g_hPreviewWnd && !IsControlPanelRunning()) {
                    std::cout << "[PREVIEW] control panel closed, stopping preview\n";
                    StopPreview();
                }
            }
        }

        if (keepConsole && (GetAsyncKeyState(VK_RETURN) & 0x8000)) running = false;

        Sleep(10);
    }

    StopLockOverlay();   // also uninstalls the keyboard hook
    StopPreview();
    StopPrayerFetch();
    PipeServer_Stop();
    PipeServer_RejectPendingRequests();
    DestroyTrayAndHiddenWindow();
    ReleaseAllWallpapers();
    UnloadMpvDll();
    ReleaseSingleInstance();
    return 0;
}