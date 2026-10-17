using Path = System.IO.Path;
using Blueprint.ControlPanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Blueprint.ControlPanel;

public sealed partial class SettingsPage : Page
{
    private readonly EnginePipeClient _client = new();
    private CancellationTokenSource _cts = new();
    private bool _loading = false;

    // Parallel array matching the ComboBox order in XAML.
    private static readonly int[] PrayerMethodIds = { 3, 4, 5, 1, 19, 12 };

    private static readonly HttpClient _http = new();

    // Day/Night video paths (kept in memory, mirrored to the engine).
    private string _dnDayVideo = "";
    private string _dnNightVideo = "";

    // Accent swatch bookkeeping
    private string _currentAccentId = "default";

    public SettingsPage()
    {
        Debug.WriteLine("[SettingsPage] ctor start");
        InitializeComponent();
        Debug.WriteLine("[SettingsPage] ctor after InitializeComponent");

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StrataWallpaper/1.0");

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        Debug.WriteLine("[SettingsPage] ctor done");
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
            Debug.WriteLine($"[Strata Settings] {ex}");
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();

        var ui = UiSettings.Current;

        _loading = true;
        try
        {
            // Load Base Theme
            string baseTheme = ui.BaseTheme ?? "Aura";
            foreach (ComboBoxItem item in BaseThemeCombo.Items)
            {
                if (item.Tag as string == baseTheme) { BaseThemeCombo.SelectedItem = item; break; }
            }
            if (BaseThemeCombo.SelectedIndex < 0) BaseThemeCombo.SelectedIndex = 0;

            // Load Color Scheme
            VariantCombo.SelectedIndex = ui.Theme switch
            {
                "Dark" => 1,
                "Light" => 2,
                _ => 0
            };

            CloseAsk.IsChecked = ui.CloseAction == "ask";
            CloseExit.IsChecked = ui.CloseAction == "exit";
            CloseTray.IsChecked = ui.CloseAction == "tray";

            MinimizeToTrayToggle.IsOn = ui.MinimizeToTray;

            _currentAccentId = ui.AccentScheme;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Blueprint");
            Directory.CreateDirectory(dir);
            SettingsPathText.Text = dir;
            LogPathText.Text = Path.Combine(dir, "engine.log");

            BuildAccentSwatches();
        }
        finally
        {
            _loading = false;
        }

        SafeRun(LoadEngineSettingsAsync);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => _cts.Cancel();

    private async Task LoadEngineSettingsAsync()
    {
        var response = await _client.SendAsync("get_settings", ct: _cts.Token);
        if (_cts.IsCancellationRequested || response == null) return;

        var body = response["body"];
        if (body == null) return;

        _loading = true;
        try
        {
            AutoStartToggle.IsOn = body["auto_start"]?.GetValue<bool>() ?? false;

            // ---- Day/Night ----
            DayNightEnabledToggle.IsOn = body["daynight_enabled"]?.GetValue<bool>() ?? false;

            string mode = body["daynight_mode"]?.GetValue<string>() ?? "sunrise";
            DnModeManual.IsChecked = mode == "manual";
            DnModeSunrise.IsChecked = mode == "sunrise";
            DnModePrayer.IsChecked = mode == "prayer";

            DnManualDayStart.Time = ParseHHMM(
                body["daynight_manual_day_start"]?.GetValue<string>() ?? "07:00");
            DnManualNightStart.Time = ParseHHMM(
                body["daynight_manual_night_start"]?.GetValue<string>() ?? "19:00");

            DnLatBox.Text = (body["daynight_latitude"]?.GetValue<double>() ?? 0.0)
                .ToString("0.######");
            DnLngBox.Text = (body["daynight_longitude"]?.GetValue<double>() ?? 0.0)
                .ToString("0.######");

            int method = body["daynight_prayer_method"]?.GetValue<int>() ?? 3;
            DnMethodCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PrayerMethodIds, method));

            DnDuhaOffsetBox.Value = body["daynight_duha_offset"]?.GetValue<int>() ?? 20;

            _dnDayVideo = body["daynight_day_video"]?.GetValue<string>() ?? "";
            _dnNightVideo = body["daynight_night_video"]?.GetValue<string>() ?? "";
            DnDayVideoText.Text = ShortPath(_dnDayVideo);
            DnNightVideoText.Text = ShortPath(_dnNightVideo);

            string todayDate = body["today_date"]?.GetValue<string>() ?? "";
            string sr = body["today_sunrise"]?.GetValue<string>() ?? "";
            string mg = body["today_maghrib"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(todayDate))
            {
                DnTodayText.Text =
                    $"Today ({todayDate})  ·  Sunrise {sr}  ·  Maghrib {mg}";
            }
            else
            {
                DnTodayText.Text =
                    "Today's prayer times not available yet — will appear after the next successful fetch.";
            }

            UpdateDnPanels();
        }
        finally
        {
            _loading = false;
        }
    }

    // ============================================================
    //  Accent swatches
    // ============================================================
    private void BuildAccentSwatches()
    {
        AccentSwatchPanel.Children.Clear();

        foreach (var scheme in ThemeService.Schemes)
        {
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(
                    scheme.Id == _currentAccentId
                        ? ((SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"]).Color
                        : Color.FromArgb(0, 0, 0, 0)),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            };

            var circle = new Ellipse
            {
                Width = 22,
                Height = 22,
                Fill = new SolidColorBrush(scheme.Accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            ring.Child = circle;

            var btn = new Button
            {
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Content = ring,
                Tag = scheme.Id
            };
            ToolTipService.SetToolTip(btn, scheme.DisplayName);
            btn.Click += OnAccentSwatchClick;

            AccentSwatchPanel.Children.Add(btn);
        }
    }

    private async void OnAccentSwatchClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not Button b || b.Tag is not string id) return;

        _currentAccentId = id;

        var ui = UiSettings.Current;
        ui.AccentScheme = id;
        ui.Save();

        // Apply the new accent color dynamically
        ThemeService.Apply(Application.Current, ThemeService.Get(id));

        BuildAccentSwatches();

        // Give WinUI a moment to process the new resources, then force a redraw
        await Task.Delay(50);
        ForceUIUpdate();
    }

    // ============================================================
    //  Theme / close / tray / auto-start
    // ============================================================
    private void OnBaseThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BaseThemeCombo.SelectedItem is not ComboBoxItem item) return;

        string baseTheme = item.Tag as string ?? "Aura";
        var ui = UiSettings.Current;
        ui.BaseTheme = baseTheme;
        ui.Save();

        // Apply immediately without restarting
        ThemeService.ApplyBaseTheme(Application.Current, baseTheme);
        ThemeService.Apply(Application.Current, ThemeService.Get(UiSettings.Current.AccentScheme));

        ForceUIUpdate();
    }

    private void OnVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        string theme = VariantCombo.SelectedIndex switch
        {
            1 => "Dark",
            2 => "Light",
            _ => "System"
        };

        var ui = UiSettings.Current;
        ui.Theme = theme;
        ui.Save();

        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        if (App.MainWindow is MainWindow mw) mw.RefreshThemeFooterLabel();
    }

    private void OnCloseActionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var ui = UiSettings.Current;
        if (CloseAsk.IsChecked == true) ui.CloseAction = "ask";
        else if (CloseExit.IsChecked == true) ui.CloseAction = "exit";
        else if (CloseTray.IsChecked == true) ui.CloseAction = "tray";
        ui.Save();
    }

    private void OnMinimizeToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var ui = UiSettings.Current;
        ui.MinimizeToTray = MinimizeToTrayToggle.IsOn;
        ui.Save();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            var body = new JsonObject { ["enabled"] = AutoStartToggle.IsOn };
            await _client.SendAsync("set_autostart", body, _cts.Token);
        });

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blueprint");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
        { UseShellExecute = true });
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        var p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blueprint", "engine.log");
        if (File.Exists(p))
            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    // ============================================================
    //  Day / Night — everything below is preserved logic
    // ============================================================
    private void OnDayNightEnabledToggled(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            string mode = GetDnMode();
            var body = new JsonObject
            {
                ["mode"] = mode,
                ["enabled"] = DayNightEnabledToggle.IsOn
            };
            await _client.SendAsync("set_daynight_mode", body, _cts.Token);
        });

    private string GetDnMode()
    {
        if (DnModeManual.IsChecked == true) return "manual";
        if (DnModePrayer.IsChecked == true) return "prayer";
        return "sunrise";
    }

    private void OnDnModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateDnPanels();
        if (_loading) return;

        SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            var body = new JsonObject
            {
                ["mode"] = GetDnMode(),
                ["enabled"] = DayNightEnabledToggle.IsOn
            };
            var resp = await _client.SendAsync("set_daynight_mode", body, _cts.Token);
            if (_cts.IsCancellationRequested) return;

            if (resp == null && GetDnMode() == "prayer")
            {
                DnTodayText.Text =
                    "Engine is not running — start it from the Home page to fetch prayer times.";
                return;
            }

            if (GetDnMode() == "prayer")
                await WaitForPrayerTimesAsync(30);
        });
    }

    private void UpdateDnPanels()
    {
        string mode = GetDnMode();
        DnManualPanel.Visibility = mode == "manual"
            ? Visibility.Visible : Visibility.Collapsed;
        DnSunrisePanel.Visibility = mode == "sunrise"
            ? Visibility.Visible : Visibility.Collapsed;
        DnPrayerPanel.Visibility = mode == "prayer"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDnManualTimeChanged(object sender, TimePickerValueChangedEventArgs args)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            var body = new JsonObject
            {
                ["day_start"] = FormatHHMM(DnManualDayStart.Time),
                ["night_start"] = FormatHHMM(DnManualNightStart.Time)
            };
            await _client.SendAsync("set_daynight_manual", body, _cts.Token);
        });

    private void OnDnLatLngChanged(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            if (!double.TryParse(DnLatBox.Text, out double lat)) return;
            if (!double.TryParse(DnLngBox.Text, out double lng)) return;

            var body = new JsonObject
            {
                ["latitude"] = lat,
                ["longitude"] = lng,
                ["method"] = GetSelectedMethod(),
                ["duha_offset_minutes"] = (int)DnDuhaOffsetBox.Value
            };
            await _client.SendAsync("set_daynight_prayer", body, _cts.Token);
        });

    private void OnDnMethodChanged(object sender, SelectionChangedEventArgs e)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            double.TryParse(DnLatBox.Text, out double lat);
            double.TryParse(DnLngBox.Text, out double lng);

            var body = new JsonObject
            {
                ["latitude"] = lat,
                ["longitude"] = lng,
                ["method"] = GetSelectedMethod(),
                ["duha_offset_minutes"] = (int)DnDuhaOffsetBox.Value
            };
            await _client.SendAsync("set_daynight_prayer", body, _cts.Token);
        });

    private void OnDnDuhaOffsetChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => SafeRun(async () =>
        {
            if (_loading || _cts.IsCancellationRequested) return;
            double.TryParse(DnLatBox.Text, out double lat);
            double.TryParse(DnLngBox.Text, out double lng);

            var body = new JsonObject
            {
                ["latitude"] = lat,
                ["longitude"] = lng,
                ["method"] = GetSelectedMethod(),
                ["duha_offset_minutes"] = (int)DnDuhaOffsetBox.Value
            };
            await _client.SendAsync("set_daynight_prayer", body, _cts.Token);
        });

    private int GetSelectedMethod()
    {
        int idx = DnMethodCombo.SelectedIndex;
        if (idx < 0 || idx >= PrayerMethodIds.Length) idx = 0;
        return PrayerMethodIds[idx];
    }

    private void OnDnDetectCityClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            var city = DnCityBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(city)) return;

            DnDetectButton.IsEnabled = false;
            DnCityResultText.Text = "Looking up...";
            try
            {
                var url = "https://nominatim.openstreetmap.org/search?format=json&limit=1&q="
                    + Uri.EscapeDataString(city);
                var resp = await _http.GetStringAsync(url, _cts.Token);
                using var doc = JsonDocument.Parse(resp);
                var arr = doc.RootElement;
                if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                {
                    DnCityResultText.Text = "City not found.";
                    return;
                }
                var first = arr[0];
                string latS = first.GetProperty("lat").GetString() ?? "0";
                string lonS = first.GetProperty("lon").GetString() ?? "0";

                if (!double.TryParse(latS, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat)) return;
                if (!double.TryParse(lonS, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lng)) return;

                DnLatBox.Text = lat.ToString("0.######");
                DnLngBox.Text = lng.ToString("0.######");

                var body = new JsonObject
                {
                    ["latitude"] = lat,
                    ["longitude"] = lng,
                    ["method"] = GetSelectedMethod(),
                    ["duha_offset_minutes"] = (int)DnDuhaOffsetBox.Value
                };
                var saveResp = await _client.SendAsync("set_daynight_prayer", body, _cts.Token);
                if (_cts.IsCancellationRequested) return;

                if (saveResp == null)
                {
                    DnCityResultText.Text =
                        "Engine is not running — start it from the Home page, then click Detect again.";
                    return;
                }

                string displayName = first.TryGetProperty("display_name", out var dn)
                    ? (dn.GetString() ?? "") : "";
                if (!string.IsNullOrEmpty(displayName))
                    DnCityResultText.Text = $"Detected: {displayName}";
                else
                    DnCityResultText.Text = $"Detected: {lat:0.####}, {lng:0.####}";

                if (GetDnMode() == "prayer")
                {
                    DnCityResultText.Text += "  ·  fetching prayer times…";
                    await WaitForPrayerTimesAsync(30);
                }
            }
            catch (Exception ex)
            {
                DnCityResultText.Text = "Detect failed: " + ex.Message;
            }
            finally
            {
                DnDetectButton.IsEnabled = true;
            }
        });

    // After asking the engine to fetch prayer times, poll for the result.
    private async Task WaitForPrayerTimesAsync(int maxSeconds)
    {
        int waited = 0;
        while (waited < maxSeconds)
        {
            await Task.Delay(2000, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            waited += 2;

            var response = await _client.SendAsync("get_settings", ct: _cts.Token);
            if (_cts.IsCancellationRequested) return;
            if (response == null) continue;

            var body = response["body"];
            string todayDate = body?["today_date"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(todayDate)) continue;

            string sr = body?["today_sunrise"]?.GetValue<string>() ?? "";
            string mg = body?["today_maghrib"]?.GetValue<string>() ?? "";

            DispatcherQueue.TryEnqueue(() =>
            {
                DnTodayText.Text =
                    $"Today ({todayDate})  ·  Sunrise {sr}  ·  Maghrib {mg}";
                DnCityResultText.Text = DnCityResultText.Text
                    .Replace("  ·  fetching prayer times…", "");
            });
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            DnCityResultText.Text = DnCityResultText.Text
                .Replace("  ·  fetching prayer times…",
                         "  ·  fetch timed out (check engine.log)");
        });
    }

    private void OnDnBrowseDayVideoClick(object sender, RoutedEventArgs e)
        => SafeRun(() => BrowseDayNightVideo(true));

    private void OnDnBrowseNightVideoClick(object sender, RoutedEventArgs e)
        => SafeRun(() => BrowseDayNightVideo(false));

    private async Task BrowseDayNightVideo(bool isDay)
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
        if (file == null || _cts.IsCancellationRequested) return;

        if (isDay) _dnDayVideo = file.Path;
        else _dnNightVideo = file.Path;

        DnDayVideoText.Text = ShortPath(_dnDayVideo);
        DnNightVideoText.Text = ShortPath(_dnNightVideo);

        var body = new JsonObject
        {
            ["day_video"] = _dnDayVideo,
            ["night_video"] = _dnNightVideo
        };
        await _client.SendAsync("set_daynight_videos", body, _cts.Token);
    }

    // ============================================================
    //  Small helpers
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

    private void ForceUIUpdate()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            var currentTheme = root.RequestedTheme;
            // Flicker the theme to force WinUI to re-evaluate all resources
            root.RequestedTheme = ElementTheme.Light;
            root.RequestedTheme = ElementTheme.Dark;
            root.RequestedTheme = currentTheme;
        }
    }
}