using Blueprint.ControlPanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace Blueprint.ControlPanel;

public sealed partial class LockPage : Page
{
    private readonly EnginePipeClient _client = new();
    private CancellationTokenSource _cts = new();
    private bool _loading = false;

    // Fallback videos
    private string _lockDayVideo = "";
    private string _lockNightVideo = "";

    // Lock Day/Night videos
    private string _lockDnDayVideo = "";
    private string _lockDnNightVideo = "";

    // Lock wallpaper assignment state.
    // _lockGlobalPath drives "Both" (or any monitor without a per-monitor override).
    // _lockMonitorPaths has { 0: "C:\...", 1: "C:\..." } for monitors with
    // their own wallpaper set.
    private string _lockGlobalPath = "";
    private readonly Dictionary<int, string> _lockMonitorPaths = new();
    private string _lockTarget = "all";   // "0" | "1" | "all"
    private int _monitorCount = 1;

    // Cached path actually used by the preview card
    private string _lockWallpaperPath = "";

    // Live preview clock
    private DispatcherTimer? _clockTimer;

    // Fill-mode and FPS maps
    private static readonly string[] FillModes = { "fill", "fit", "stretch", "center" };
    private static readonly int[] FpsOptions = { 15, 20, 30, 60 };

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetPrivateProfileString(string section, string key, string def, System.Text.StringBuilder retVal, int size, string filePath);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CMONITORS = 80;

    private static string GetSettingsPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueprint");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.ini");
    }

    public LockPage()
    {
        Debug.WriteLine("[LockPage] ctor start");
        InitializeComponent();
        Debug.WriteLine("[LockPage] ctor after InitializeComponent");

        LockIdleSlider.ValueChanged += OnLockIdleChanged;
        LockUseDayNightVideosCheckbox.Checked += OnLockUseDnChanged;
        LockUseDayNightVideosCheckbox.Unchecked += OnLockUseDnChanged;
        LockDnDuhaOffsetBox.ValueChanged += OnLockDnDuhaOffsetChanged;

        LockIdleValueText.Text = $"{(int)LockIdleSlider.Value} min";

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        Debug.WriteLine("[LockPage] ctor done");
    }

    // ============================================================
    //  Lifecycle
    // ============================================================
    private static async void SafeRun(Func<Task> body)
    {
        try { await body(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Strata Lock] {ex}");
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        StartClockTimer();
        SyncTargetButtons();
        SafeRun(LoadAsync);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        _clockTimer?.Stop();
    }

    // ============================================================
    //  Load
    // ============================================================
    private async Task LoadAsync()
    {
        var response = await _client.SendAsync("get_settings", ct: _cts.Token);
        if (_cts.IsCancellationRequested) return;

        if (response != null)
        {
            var body = response["body"];
            if (body != null)
            {
                _loading = true;
                try
                {
                    LockEnabledToggle.IsOn = body["lock_enabled"]?.GetValue<bool>() ?? false;

                    int idleSec = body["lock_idle_seconds"]?.GetValue<int>() ?? 300;
                    int idleMin = Math.Clamp((int)Math.Round(idleSec / 60.0), 1, 60);
                    LockIdleSlider.Value = idleMin;
                    LockIdleValueText.Text = $"{idleMin} min";

                    LockPauseToggle.IsOn = body["lock_pause_on_lock"]?.GetValue<bool>() ?? false;
                    LockDimToggle.IsOn = body["lock_dim_when_locked"]?.GetValue<bool>() ?? true;
                    LockClockToggle.IsOn = body["lock_show_clock"]?.GetValue<bool>() ?? true;

                    string fill = body["lock_fill_mode"]?.GetValue<string>() ?? "fill";
                    LockFillModeCombo.SelectedIndex =
                        Math.Max(0, Array.IndexOf(FillModes, fill));

                    int fps = body["lock_fps"]?.GetValue<int>() ?? 30;
                    LockFpsCombo.SelectedIndex =
                        Math.Max(0, Array.IndexOf(FpsOptions, fps));

                    string lockMode = body["lock_mode"]?.GetValue<string>() ?? "screensaver";
                    LockModeScreensaver.IsChecked = lockMode == "screensaver";
                    LockModePassword.IsChecked = lockMode == "password";

                    bool hasPw = body["lock_has_password"]?.GetValue<bool>() ?? false;
                    LockPasswordStatusText.Text = hasPw
                        ? "Password is set."
                        : "No password saved yet.";
                    LockClearPasswordButton.IsEnabled = hasPw;

                    bool useDn = body["lock_use_daynight_videos"]?.GetValue<bool>() ?? true;
                    LockUseDayNightVideosCheckbox.IsChecked = useDn;

                    _lockDayVideo = body["lock_day_video"]?.GetValue<string>() ?? "";
                    _lockNightVideo = body["lock_night_video"]?.GetValue<string>() ?? "";
                    LockDayVideoText.Text = ShortPath(_lockDayVideo);
                    LockNightVideoText.Text = ShortPath(_lockNightVideo);

                    _monitorCount = body["monitor_count"]?.GetValue<int>() ?? 1;
                    if (_monitorCount < 1) _monitorCount = 1;

                    _lockGlobalPath = body["lock_assignment_path"]?.GetValue<string>() ?? "";
                    _lockMonitorPaths.Clear();
                    if (body["lock_monitor_assignment_paths"] is JsonObject map)
                    {
                        foreach (var kv in map)
                        {
                            if (!int.TryParse(kv.Key, out var idx)) continue;
                            var p = kv.Value?.GetValue<string>();
                            if (!string.IsNullOrEmpty(p))
                                _lockMonitorPaths[idx] = p;
                        }
                    }

                    if (_monitorCount >= 2 && _lockMonitorPaths.Count > 0)
                    {
                        _lockTarget = _lockMonitorPaths.Keys.Min().ToString();
                    }
                    else if (_monitorCount >= 2)
                    {
                        _lockTarget = "all";
                    }
                    else
                    {
                        _lockTarget = "0";
                    }
                }
                finally
                {
                    _loading = false;
                }
            }
        }
        else
        {
            LoadFromIni();
        }

        var dnResp = await _client.SendAsync("get_lock_daynight", ct: _cts.Token);
        if (!_cts.IsCancellationRequested && dnResp != null)
        {
            var dn = dnResp["body"];
            if (dn != null)
            {
                _loading = true;
                try
                {
                    LockDnEnabledToggle.IsOn = dn["enabled"]?.GetValue<bool>() ?? false;

                    string mode = dn["mode"]?.GetValue<string>() ?? "sunrise";
                    LockDnModeManual.IsChecked = mode == "manual";
                    LockDnModeSunrise.IsChecked = mode == "sunrise";
                    LockDnModePrayer.IsChecked = mode == "prayer";

                    LockDnManualDayStart.Time = ParseHHMM(
                        dn["manual_day_start"]?.GetValue<string>() ?? "07:00");
                    LockDnManualNightStart.Time = ParseHHMM(
                        dn["manual_night_start"]?.GetValue<string>() ?? "19:00");

                    LockDnDuhaOffsetBox.Value = dn["duha_offset_minutes"]?.GetValue<int>() ?? 20;

                    _lockDnDayVideo = dn["day_video"]?.GetValue<string>() ?? "";
                    _lockDnNightVideo = dn["night_video"]?.GetValue<string>() ?? "";
                    LockDnDayVideoText.Text = ShortPath(_lockDnDayVideo);
                    LockDnNightVideoText.Text = ShortPath(_lockDnNightVideo);

                    double lat = dn["latitude"]?.GetValue<double>() ?? 0;
                    double lng = dn["longitude"]?.GetValue<double>() ?? 0;
                    LockDnSunriseInfoText.Text =
                        $"Current location: {lat:0.####}, {lng:0.####}";

                    string todayDate = dn["today_date"]?.GetValue<string>() ?? "";
                    string sr = dn["today_sunrise"]?.GetValue<string>() ?? "";
                    string mg = dn["today_maghrib"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(todayDate))
                    {
                        LockDnTodayText.Text =
                            $"Today ({todayDate})  ·  Sunrise {sr}  ·  Maghrib {mg}";
                    }
                    else
                    {
                        LockDnTodayText.Text =
                            "Today's prayer times not yet available. Switch to Prayer mode in Settings to fetch them.";
                    }

                    UpdateLockPanels();
                }
                finally
                {
                    _loading = false;
                }
            }
        }

        SyncTargetButtons();
        UpdatePreviewChrome();
        await RefreshPreviewAsync();
    }

    // ============================================================
    //  Fallback: read everything from settings.ini when the engine
    //  isn't running. Keeps the Lock page useful even offline.
    // ============================================================
    private void LoadFromIni()
    {
        string ini = GetSettingsPath();
        if (!File.Exists(ini)) return;

        _loading = true;
        try
        {
            var sb = new System.Text.StringBuilder(512);
            string GetVal(string sec, string key, string def)
            {
                GetPrivateProfileString(sec, key, def, sb, sb.Capacity, ini);
                return sb.ToString();
            }

            LockEnabledToggle.IsOn = GetVal("Lock", "Enabled", "0") == "1";

            int idleSec = int.TryParse(GetVal("Lock", "IdleSeconds", "300"), out var isec) ? isec : 300;
            int idleMin = Math.Clamp((int)Math.Round(idleSec / 60.0), 1, 60);
            LockIdleSlider.Value = idleMin;
            LockIdleValueText.Text = $"{idleMin} min";

            LockPauseToggle.IsOn = GetVal("Lock", "PauseOnLock", "0") == "1";
            LockDimToggle.IsOn = GetVal("Lock", "DimWhenLocked", "1") == "1";
            LockClockToggle.IsOn = GetVal("Lock", "ShowClock", "1") == "1";

            string fill = GetVal("Lock", "FillMode", "fill");
            LockFillModeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FillModes, fill));

            int fps = int.TryParse(GetVal("Lock", "Fps", "30"), out var f) ? f : 30;
            LockFpsCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FpsOptions, fps));

            string lockMode = GetVal("Lock", "Mode", "screensaver");
            LockModeScreensaver.IsChecked = lockMode == "screensaver";
            LockModePassword.IsChecked = lockMode == "password";

            bool hasPw = !string.IsNullOrEmpty(GetVal("Lock", "PasswordHash", ""));
            LockPasswordStatusText.Text = hasPw ? "Password is set." : "No password saved yet.";
            LockClearPasswordButton.IsEnabled = hasPw;

            bool useDn = GetVal("Lock", "UseDayNightVideos", "1") == "1";
            LockUseDayNightVideosCheckbox.IsChecked = useDn;

            _lockDayVideo = GetVal("Lock", "DayVideo", "");
            _lockNightVideo = GetVal("Lock", "NightVideo", "");
            LockDayVideoText.Text = ShortPath(_lockDayVideo);
            LockNightVideoText.Text = ShortPath(_lockNightVideo);

            _lockGlobalPath = GetVal("Lock", "WallpaperOverride", "");
            _lockMonitorPaths.Clear();
            for (int i = 0; i < 2; i++)
            {
                string p = GetVal($"LockMonitor{i}", "VideoPath", "");
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) _lockMonitorPaths[i] = p;
            }

            _monitorCount = GetSystemMetrics(SM_CMONITORS);
            if (_monitorCount < 1) _monitorCount = 1;

            if (_monitorCount >= 2 && _lockMonitorPaths.Count > 0)
                _lockTarget = _lockMonitorPaths.Keys.Min().ToString();
            else if (_monitorCount >= 2)
                _lockTarget = "all";
            else
                _lockTarget = "0";

            LockDnEnabledToggle.IsOn = GetVal("LockDayNight", "Enabled", "0") == "1";
            string dnMode = GetVal("LockDayNight", "Mode", "sunrise");
            LockDnModeManual.IsChecked = dnMode == "manual";
            LockDnModeSunrise.IsChecked = dnMode == "sunrise";
            LockDnModePrayer.IsChecked = dnMode == "prayer";

            LockDnManualDayStart.Time = ParseHHMM(GetVal("LockDayNight", "ManualDayStart", "07:00"));
            LockDnManualNightStart.Time = ParseHHMM(GetVal("LockDayNight", "ManualNightStart", "19:00"));
            LockDnDuhaOffsetBox.Value = int.TryParse(GetVal("LockDayNight", "DuhaOffsetMinutes", "20"), out var duha) ? duha : 20;

            _lockDnDayVideo = GetVal("LockDayNight", "DayVideo", "");
            _lockDnNightVideo = GetVal("LockDayNight", "NightVideo", "");
            LockDnDayVideoText.Text = ShortPath(_lockDnDayVideo);
            LockDnNightVideoText.Text = ShortPath(_lockDnNightVideo);

            double lat = double.TryParse(GetVal("DayNight", "Latitude", "0"), out var lt) ? lt : 0;
            double lng = double.TryParse(GetVal("DayNight", "Longitude", "0"), out var lg) ? lg : 0;
            LockDnSunriseInfoText.Text = $"Current location: {lat:0.####}, {lng:0.####}";
            LockDnTodayText.Text = "Today's prayer times not yet available.";
        }
        finally
        {
            _loading = false;
        }
    }

    // ============================================================
    //  Target selector
    // ============================================================
    private void OnLockTargetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton t || t.Tag is not string target) return;
        _lockTarget = target;
        SyncTargetButtons();
        SafeRun(RefreshPreviewAsync);
    }

    private void SyncTargetButtons()
    {
        bool m1Avail = _monitorCount >= 1;
        bool m2Avail = _monitorCount >= 2;

        LockTargetM1.IsEnabled = m1Avail;
        LockTargetM2.IsEnabled = m2Avail;
        LockTargetBoth.IsEnabled = _monitorCount >= 2;

        // If a disabled target is selected, fall back to what's valid.
        if (_lockTarget == "1" && !m2Avail) _lockTarget = m1Avail ? "0" : "all";
        if (_lockTarget == "all" && !(_monitorCount >= 2)) _lockTarget = "0";

        LockTargetM1.IsChecked = _lockTarget == "0";
        LockTargetM2.IsChecked = _lockTarget == "1";
        LockTargetBoth.IsChecked = _lockTarget == "all";

        // Single monitor → hide the "Both" and "Monitor 2" buttons
        LockTargetM2.Visibility = m2Avail ? Visibility.Visible : Visibility.Collapsed;
        LockTargetBoth.Visibility = m2Avail ? Visibility.Visible : Visibility.Collapsed;
    }

    // Resolve which path applies to the currently selected target.
    private string ResolveCurrentTargetPath()
    {
        if (_lockTarget == "all")
            return _lockGlobalPath;

        if (int.TryParse(_lockTarget, out var idx))
        {
            if (_lockMonitorPaths.TryGetValue(idx, out var p) && !string.IsNullOrEmpty(p))
                return p;
            // No per-monitor override → fall through to the global one
            return _lockGlobalPath;
        }

        return _lockGlobalPath;
    }

    // ============================================================
    //  Preview card
    // ============================================================
    private async Task RefreshPreviewAsync()
    {
        // ── Decide: single preview or dual (per-monitor) preview ──
        string p0 = _lockGlobalPath;
        string p1 = _lockGlobalPath;

        if (_monitorCount > 1)
        {
            if (_lockMonitorPaths.TryGetValue(0, out var m0) && !string.IsNullOrEmpty(m0))
                p0 = m0;
            if (_lockMonitorPaths.TryGetValue(1, out var m1) && !string.IsNullOrEmpty(m1))
                p1 = m1;
        }

        bool isDual = _lockTarget == "all"
                   && _monitorCount > 1
                   && !string.IsNullOrEmpty(p0)
                   && !string.IsNullOrEmpty(p1)
                   && !string.Equals(p0, p1, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(p0)
                   && File.Exists(p1);

        SinglePreview.Visibility = isDual ? Visibility.Collapsed : Visibility.Visible;
        DualPreview.Visibility = isDual ? Visibility.Visible : Visibility.Collapsed;

        if (isDual)
        {
            LockWallpaperNameText.Text = "Different per monitor";
            LockWallpaperMetaText.Text = "Both monitors · different wallpapers";

            await LoadPreviewImageAsync(LockWallpaperPreviewM1, p0);
            await LoadPreviewImageAsync(LockWallpaperPreviewM2, p1);
            UpdatePreviewChrome();
            return;
        }

        // ── Single preview path ──
        string target = ResolveCurrentTargetPath();
        _lockWallpaperPath = target;

        if (string.IsNullOrEmpty(target) || !File.Exists(target))
        {
            LockWallpaperPreview.Source = null;
            LockWallpaperNameText.Text = "(none)";
            LockWallpaperMetaText.Text = DescribeTarget();
            UpdatePreviewChrome();
            return;
        }

        LockWallpaperNameText.Text = Path.GetFileName(target);
        LockWallpaperMetaText.Text = DescribeTarget();

        await LoadPreviewImageAsync(LockWallpaperPreview, target);
        UpdatePreviewChrome();
    }

    private static async Task LoadPreviewImageAsync(Image target, string path)
    {
        if (target == null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 1920);
            if (thumb != null)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                target.Source = bmp;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LockPreview] {ex}");
        }
    }

    private string DescribeTarget() => _lockTarget switch
    {
        "0" => "Monitor 1",
        "1" => "Monitor 2",
        "all" => _monitorCount > 1 ? "Both monitors" : "Desktop",
        _ => "Lock Screen"
    };

    // ============================================================
    //  Live clock
    // ============================================================
    private void StartClockTimer()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, __) => UpdatePreviewClock();
        UpdatePreviewClock();
        _clockTimer.Start();
    }

    private void UpdatePreviewClock()
    {
        var now = DateTime.Now;
        string time = now.ToString("HH:mm");
        string dateShort = now.ToString("dddd, MMM d");

        LockPreviewClockTime.Text = time;
        LockPreviewClockDate.Text = dateShort;
        LockPreviewClockTimeM1.Text = time;
        LockPreviewClockDateM1.Text = dateShort;
        LockPreviewClockTimeM2.Text = time;
        LockPreviewClockDateM2.Text = dateShort;
    }

    private void UpdatePreviewChrome()
    {
        bool showClock = LockClockToggle.IsOn;
        bool showDim = LockDimToggle.IsOn;

        LockPreviewClockTime.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
        LockPreviewClockDate.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
        LockPreviewClockTimeM1.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
        LockPreviewClockDateM1.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
        LockPreviewClockTimeM2.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;
        LockPreviewClockDateM2.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;

        LockPreviewDimOverlay.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;
        LockPreviewDimOverlayM1.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;
        LockPreviewDimOverlayM2.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================
    //  Behaviour / display handlers
    // ============================================================
    private void OnLockPauseToggled(object sender, RoutedEventArgs e)
        => SafeRun(() => SaveLockConfigAsync("pause_on_lock", LockPauseToggle.IsOn));

    private void OnLockDimToggled(object sender, RoutedEventArgs e)
    {
        UpdatePreviewChrome();
        SafeRun(() => SaveLockConfigAsync("dim_when_locked", LockDimToggle.IsOn));
    }

    private void OnLockClockToggled(object sender, RoutedEventArgs e)
    {
        UpdatePreviewChrome();
        SafeRun(() => SaveLockConfigAsync("show_clock", LockClockToggle.IsOn));
    }

    private void OnLockFillModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _cts.IsCancellationRequested) return;
        if (LockFillModeCombo.SelectedIndex < 0) return;
        string mode = FillModes[LockFillModeCombo.SelectedIndex];
        SafeRun(() => SaveLockConfigAsync("fill_mode", mode));
    }

    private void OnLockFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _cts.IsCancellationRequested) return;
        if (LockFpsCombo.SelectedIndex < 0) return;
        int fps = FpsOptions[LockFpsCombo.SelectedIndex];
        SafeRun(() => SaveLockConfigAsync("fps", fps));
    }

    private async Task SaveLockConfigAsync(string key, object value)
    {
        if (_loading || _cts.IsCancellationRequested) return;

        var body = new JsonObject
        {
            [key] = value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                string s => JsonValue.Create(s),
                _ => null
            }
        };
        await _client.SendAsync("set_lock_config", body, _cts.Token);
    }

    // ============================================================
    //  Helpers
    // ============================================================
    private static TimeSpan ParseHHMM(string s)
    {
        if (TimeSpan.TryParse(s, out var t)) return t;
        return TimeSpan.FromHours(7);
    }

    private static string FormatHHMM(TimeSpan t)
        => $"{t.Hours:D2}:{t.Minutes:D2}";

    private static string ShortPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "(none)";
        try { return Path.GetFileName(p); } catch { return p; }
    }

    // ============================================================
    //  Behaviour
    // ============================================================
    private void OnLockEnabledToggled(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            var body = new JsonObject { ["enabled"] = LockEnabledToggle.IsOn };
            await _client.SendAsync("set_lock_config", body, _cts.Token);
        });

    private void OnLockIdleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        LockIdleValueText.Text = $"{(int)LockIdleSlider.Value} min";
        if (_loading) return;
        SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            var body = new JsonObject { ["idle_seconds"] = (int)LockIdleSlider.Value * 60 };
            await _client.SendAsync("set_lock_config", body, _cts.Token);
        });
    }

    private void OnLockModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateLockPanels();
        if (_loading) return;

        string mode = LockModePassword.IsChecked == true ? "password" : "screensaver";

        // If the user picks Locked but no password is on file, keep the
        // selection so they can see the password card and set one. Do
        // NOT send mode=password to the engine yet — the engine would
        // coerce it back to screensaver anyway, and we want the panel
        // state to stay consistent with the engine's.
        if (mode == "password" && !LockClearPasswordButton.IsEnabled)
        {
            LockPasswordStatusText.Text =
                "Set a password below to enable Locked mode.";
            return;
        }

        SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            var body = new JsonObject { ["mode"] = mode };
            await _client.SendAsync("set_lock_config", body, _cts.Token);
        });
    }

    // ============================================================
    //  Password
    // ============================================================
    private void OnLockSavePasswordClick(object sender, RoutedEventArgs e)
        => SafeRun(CommitPendingPasswordIfAny);

    private void OnLockClearPasswordClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            var body = new JsonObject { ["clear_password"] = true };
            await _client.SendAsync("set_lock_config", body, _cts.Token);
            LockPasswordBox.Password = "";
            LockPasswordConfirmBox.Password = "";
            LockPasswordStatusText.Text = "Password cleared.";
            LockClearPasswordButton.IsEnabled = false;

            // If we were in password mode, drop back to screensaver.
            if (LockModePassword.IsChecked == true)
            {
                LockModeScreensaver.IsChecked = true;
                LockModePassword.IsChecked = false;
                var modeBody = new JsonObject { ["mode"] = "screensaver" };
                await _client.SendAsync("set_lock_config", modeBody, _cts.Token);
            }
        });

    private async Task CommitPendingPasswordIfAny()
    {
        var pw = LockPasswordBox.Password;
        var cf = LockPasswordConfirmBox.Password;
        if (string.IsNullOrEmpty(pw) && string.IsNullOrEmpty(cf)) return;

        if (pw != cf)
        {
            LockPasswordStatusText.Text = "Passwords do not match.";
            return;
        }
        if (pw.Length < 4)
        {
            LockPasswordStatusText.Text = "Password must be at least 4 characters.";
            return;
        }

        var body = new JsonObject
        {
            ["password"] = pw,
            ["mode"] = "password"
        };
        await _client.SendAsync("set_lock_config", body, _cts.Token);
        LockPasswordBox.Password = "";
        LockPasswordConfirmBox.Password = "";
        LockPasswordStatusText.Text = "Password saved and mode set to Locked.";
        LockClearPasswordButton.IsEnabled = true;

        if (LockModePassword.IsChecked != true)
        {
            LockModePassword.IsChecked = true;
            LockModeScreensaver.IsChecked = false;
        }
    }

    // ============================================================
    //  Lock Day / Night
    // ============================================================
    private void OnLockDnEnabledToggled(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            UpdateLockDnPanels();
            if (_loading || _cts.IsCancellationRequested) return;
            var body = new JsonObject { ["enabled"] = LockDnEnabledToggle.IsOn };
            await _client.SendAsync("set_lock_daynight", body, _cts.Token);
        });

    private string GetLockDnMode()
    {
        if (LockDnModeManual.IsChecked == true) return "manual";
        if (LockDnModePrayer.IsChecked == true) return "prayer";
        return "sunrise";
    }

    private void OnLockDnModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateLockDnPanels();
        if (_loading) return;
        SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            var body = new JsonObject { ["mode"] = GetLockDnMode() };
            await _client.SendAsync("set_lock_daynight", body, _cts.Token);
        });
    }

    private void OnLockDnManualTimeChanged(object sender, TimePickerValueChangedEventArgs args)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            var body = new JsonObject
            {
                ["manual_day_start"] = FormatHHMM(LockDnManualDayStart.Time),
                ["manual_night_start"] = FormatHHMM(LockDnManualNightStart.Time)
            };
            await _client.SendAsync("set_lock_daynight", body, _cts.Token);
        });

    private void OnLockDnDuhaOffsetChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            var body = new JsonObject
            {
                ["duha_offset_minutes"] = (int)LockDnDuhaOffsetBox.Value
            };
            await _client.SendAsync("set_lock_daynight", body, _cts.Token);
        });

    private void OnLockDnBrowseDayVideoClick(object sender, RoutedEventArgs e)
        => SafeRun(() => BrowseLockDnVideo(true));

    private void OnLockDnBrowseNightVideoClick(object sender, RoutedEventArgs e)
        => SafeRun(() => BrowseLockDnVideo(false));

    private async Task BrowseLockDnVideo(bool isDay)
    {
        var path = await PickVideoFileAsync();
        if (path == null || _cts.IsCancellationRequested) return;

        if (isDay) _lockDnDayVideo = path;
        else _lockDnNightVideo = path;

        LockDnDayVideoText.Text = ShortPath(_lockDnDayVideo);
        LockDnNightVideoText.Text = ShortPath(_lockDnNightVideo);

        var body = new JsonObject
        {
            ["day_video"] = _lockDnDayVideo,
            ["night_video"] = _lockDnNightVideo
        };
        await _client.SendAsync("set_lock_daynight", body, _cts.Token);
    }

    // ============================================================
    //  Fallback videos
    // ============================================================
    private void OnLockUseDnChanged(object sender, RoutedEventArgs e)
    {
        UpdateLockPanels();
        if (_loading) return;
        SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            var body = new JsonObject
            {
                ["use_daynight_videos"] = LockUseDayNightVideosCheckbox.IsChecked == true
            };
            await _client.SendAsync("set_lock_config", body, _cts.Token);
        });
    }

    private void OnLockBrowseDayVideoClick(object sender, RoutedEventArgs e)
        => SafeRun(() => BrowseLockVideo(true));

    private void OnLockBrowseNightVideoClick(object sender, RoutedEventArgs e)
        => SafeRun(() => BrowseLockVideo(false));

    private async Task BrowseLockVideo(bool isDay)
    {
        var path = await PickVideoFileAsync();
        if (path == null || _cts.IsCancellationRequested) return;

        if (isDay) _lockDayVideo = path;
        else _lockNightVideo = path;

        LockDayVideoText.Text = ShortPath(_lockDayVideo);
        LockNightVideoText.Text = ShortPath(_lockNightVideo);

        var body = new JsonObject
        {
            ["day_video"] = _lockDayVideo,
            ["night_video"] = _lockNightVideo
        };
        await _client.SendAsync("set_lock_config", body, _cts.Token);
    }

    private static async Task<string?> PickVideoFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".wmv");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // ============================================================
    //  Lock wallpaper (new)
    // ============================================================
    private void OnBrowseLockWallpaperClick(object sender, RoutedEventArgs e)
        => SafeRun(BrowseLockWallpaperAsync);

    private async Task BrowseLockWallpaperAsync()
    {
        if (_cts.IsCancellationRequested) return;

        var path = await PickVideoFileAsync();
        if (path == null || _cts.IsCancellationRequested) return;

        var body = new JsonObject
        {
            ["path"] = path,
            ["monitor"] = _lockTarget
        };
        var resp = await _client.SendAsync("set_lock_wallpaper", body, _cts.Token);
        if (_cts.IsCancellationRequested) return;

        if (resp?["status"]?.GetValue<string>() != "ok")
        {
            Debug.WriteLine("[LockPage] set_lock_wallpaper failed");
            return;
        }

        // Mirror the local state so the preview updates without a re-fetch
        if (_lockTarget == "all")
        {
            _lockGlobalPath = path;
            _lockMonitorPaths.Clear();
        }
        else if (int.TryParse(_lockTarget, out var idx))
        {
            _lockMonitorPaths[idx] = path;
        }

        await RefreshPreviewAsync();
    }

    // ============================================================
    //  Preview now
    // ============================================================
    private void OnLockPreviewNowClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            await CommitPendingPasswordIfAny();
            await _client.SendAsync("lock_now", ct: _cts.Token);
        });

    // ============================================================
    //  Panel visibility
    // ============================================================
    private void UpdateLockPanels()
    {
        bool pw = LockModePassword.IsChecked == true;
        LockPasswordCard.Visibility = pw ? Visibility.Visible : Visibility.Collapsed;

        bool useDn = LockUseDayNightVideosCheckbox.IsChecked == true;
        LockSeparateVideosPanel.Visibility = useDn
            ? Visibility.Collapsed : Visibility.Visible;

        UpdateLockDnPanels();
        UpdatePreviewChrome();
    }

    private void UpdateLockDnPanels()
    {
        bool en = LockDnEnabledToggle.IsOn;

        LockDnBody.Visibility = en ? Visibility.Visible : Visibility.Collapsed;
        LockFallbackSection.Visibility = en ? Visibility.Collapsed : Visibility.Visible;

        if (!en) return;

        string mode = GetLockDnMode();
        LockDnManualPanel.Visibility = mode == "manual"
            ? Visibility.Visible : Visibility.Collapsed;
        LockDnSunrisePanel.Visibility = mode == "sunrise"
            ? Visibility.Visible : Visibility.Collapsed;
        LockDnPrayerPanel.Visibility = mode == "prayer"
            ? Visibility.Visible : Visibility.Collapsed;
    }
}