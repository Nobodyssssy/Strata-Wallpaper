using Blueprint.ControlPanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace Blueprint.ControlPanel;

public sealed partial class PerformancePage : Page
{
    private readonly EnginePipeClient _client = new();
    private CancellationTokenSource _cts = new();
    private bool _loading = false;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _fpsDebounce;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideshowDebounce;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleDebounce;

    // Preset defaults — kept here so the page is the single source of truth.
    private const int PresetPerformanceFps = 15;
    private const string PresetPerformanceQuality = "low";

    private const int PresetBalancedFps = 30;
    private const string PresetBalancedQuality = "medium";

    private const int PresetQualityFps = 60;
    private const string PresetQualityQuality = "ultra";

    private static readonly Color AccentBorder =
        Color.FromArgb(255, 74, 144, 226);
    private static readonly Color NeutralBorder =
        Color.FromArgb(0xFF, 0x33, 0x33, 0x33);

    public PerformancePage()
    {
        InitializeComponent();

        // Wire events AFTER InitializeComponent so slider / toggles don't
        // fire while sibling controls are still being built (the crash rule
        // carried over from the original code).
        FpsSlider.ValueChanged += OnFpsSliderChanged;
        SlideshowSlider.ValueChanged += OnSlideshowSliderChanged;
        IdleSlider.ValueChanged += OnIdleSliderChanged;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
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
            Debug.WriteLine($"[Strata Perf] {ex}");
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        SafeRun(LoadAsync);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => _cts.Cancel();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            // ── 1. Pull engine-side runtime values (slideshow + idle) ──
            var resp = await _client.SendAsync("get_settings", ct: _cts.Token);
            if (_cts.IsCancellationRequested) return;

            // ── 2. Pull the persisted defaults from UiSettings ──
            var g = UiSettings.Current.GlobalDefaults;

            FpsSlider.Value = Math.Clamp(g.Fps, 15, 60);
            FpsValueText.Text = ((int)FpsSlider.Value).ToString();

            QualityLow.IsChecked = g.Quality == "low";
            QualityMedium.IsChecked = g.Quality == "medium";
            QualityUltra.IsChecked = g.Quality == "ultra";
            if (QualityLow.IsChecked != true
             && QualityMedium.IsChecked != true
             && QualityUltra.IsChecked != true)
                QualityMedium.IsChecked = true;

            HardwareDecodeToggle.IsOn = g.HardwareDecoding;
            VSyncToggle.IsOn = g.VSync;
            LowLatencyToggle.IsOn = g.LowLatency;

            // Slideshow + idle still come from the engine (settings.ini).
            if (resp != null)
            {
                var body = resp["body"];
                SlideshowToggle.IsOn = body?["slideshow_enabled"]?.GetValue<bool>() ?? false;
                SlideshowSlider.Value = Math.Clamp(
                    body?["slideshow_interval"]?.GetValue<int>() ?? 10, 1, 120);
                IdleSlider.Value = Math.Clamp(
                    body?["idle_pause_seconds"]?.GetValue<int>() ?? 0, 0, 600);

                string shuffleMode = body?["shuffle_mode"]?.GetValue<string>() ?? "off";
                if (shuffleMode == "off")
                {
                    ShuffleToggle.IsOn = false;
                    ShuffleScopeCombo.SelectedIndex = 0;
                }
                else if (shuffleMode == "favorites_only")
                {
                    ShuffleToggle.IsOn = true;
                    ShuffleScopeCombo.SelectedIndex = 1;
                }
                else
                {
                    ShuffleToggle.IsOn = true;
                    ShuffleScopeCombo.SelectedIndex = 0;
                }
            }

            UpdateFpsTickLabels();
            UpdateSlideshowLabel();
            UpdateIdleLabel();
            UpdatePresetVisuals();
            UpdateShuffleUI();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateFpsTickLabels()
    {
        FpsValueText.Text = ((int)FpsSlider.Value).ToString();
    }

    private void UpdateSlideshowLabel()
    {
        SlideshowValueText.Text = $"{SlideshowSlider.Value:0} minutes";
    }

    private void UpdateIdleLabel()
    {
        IdleValueText.Text = IdleSlider.Value == 0
            ? "Disabled"
            : $"{IdleSlider.Value:0} seconds";
    }

    // ============================================================
    //  FPS slider (debounced)
    // ============================================================
    private void OnFpsSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateFpsTickLabels();
        UpdatePresetVisuals();
        if (_loading || _cts.IsCancellationRequested) return;

        if (_fpsDebounce == null)
        {
            _fpsDebounce = DispatcherQueue.CreateTimer();
            _fpsDebounce.IsRepeating = false;
            _fpsDebounce.Interval = TimeSpan.FromMilliseconds(400);
            _fpsDebounce.Tick += async (_, __) =>
            {
                if (_loading || _cts.IsCancellationRequested) return;
                await CommitFpsAsync();
            };
        }
        _fpsDebounce.Stop();
        _fpsDebounce.Start();
    }

    private async Task CommitFpsAsync()
    {
        var g = UiSettings.Current.GlobalDefaults;
        g.Fps = (int)FpsSlider.Value;
        UiSettings.Current.Save();

        await _client.SetGlobalPerformanceAsync(g, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        FlashSaved();
    }

    // ============================================================
    //  Quality radios
    // ============================================================
    private void OnQualityChanged(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            UpdatePresetVisuals();
            if (_loading || _cts.IsCancellationRequested) return;

            string quality =
                QualityLow.IsChecked == true ? "low" :
                QualityUltra.IsChecked == true ? "ultra" :
                                                  "medium";

            var g = UiSettings.Current.GlobalDefaults;
            g.Quality = quality;
            UiSettings.Current.Save();

            await _client.SetGlobalPerformanceAsync(g, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            FlashSaved();
        });

    // ============================================================
    //  Advanced toggles
    // ============================================================
    private void OnHardwareDecodeToggled(object sender, RoutedEventArgs e)
        => SafeRun(() => CommitAdvancedAsync());

    private void OnVSyncToggled(object sender, RoutedEventArgs e)
        => SafeRun(() => CommitAdvancedAsync());

    private void OnLowLatencyToggled(object sender, RoutedEventArgs e)
        => SafeRun(() => CommitAdvancedAsync());

    private async Task CommitAdvancedAsync()
    {
        if (_loading || _cts.IsCancellationRequested) return;

        var g = UiSettings.Current.GlobalDefaults;
        g.HardwareDecoding = HardwareDecodeToggle.IsOn;
        g.VSync = VSyncToggle.IsOn;
        g.LowLatency = LowLatencyToggle.IsOn;
        UiSettings.Current.Save();

        await _client.SetGlobalPerformanceAsync(g, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        FlashSaved();
    }

    // ============================================================
    //  Expert Options: slideshow + idle (still engine-owned)
    // ============================================================
    private void OnSlideshowToggled(object sender, RoutedEventArgs e)
        => SafeRun(SaveSlideshowAsync);

    private void OnSlideshowSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateSlideshowLabel();
        if (_loading || _cts.IsCancellationRequested) return;

        if (_slideshowDebounce == null)
        {
            _slideshowDebounce = DispatcherQueue.CreateTimer();
            _slideshowDebounce.IsRepeating = false;
            _slideshowDebounce.Interval = TimeSpan.FromMilliseconds(400);
            _slideshowDebounce.Tick += async (_, __) => await SaveSlideshowAsync();
        }
        _slideshowDebounce.Stop();
        _slideshowDebounce.Start();
    }

    private async Task SaveSlideshowAsync()
    {
        if (_loading || _cts.IsCancellationRequested) return;

        var body = new JsonObject
        {
            ["enabled"] = SlideshowToggle.IsOn,
            ["interval_minutes"] = (int)SlideshowSlider.Value
        };
        await _client.SendAsync("set_slideshow", body, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        FlashSaved();
    }

    private void OnIdleSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateIdleLabel();
        if (_loading || _cts.IsCancellationRequested) return;

        if (_idleDebounce == null)
        {
            _idleDebounce = DispatcherQueue.CreateTimer();
            _idleDebounce.IsRepeating = false;
            _idleDebounce.Interval = TimeSpan.FromMilliseconds(400);
            _idleDebounce.Tick += async (_, __) =>
            {
                if (_loading || _cts.IsCancellationRequested) return;
                var body = new JsonObject { ["seconds"] = (int)IdleSlider.Value };
                await _client.SendAsync("set_idle_pause", body, _cts.Token);
                if (_cts.IsCancellationRequested) return;
                FlashSaved();
            };
        }
        _idleDebounce.Stop();
        _idleDebounce.Start();
    }

    // ============================================================
    //  Shuffle
    // ============================================================
    private void OnShuffleToggled(object sender, RoutedEventArgs e)
    {
        UpdateShuffleUI();
        if (_loading || _cts.IsCancellationRequested) return;
        SafeRun(SaveShuffleAsync);
    }

    private void OnShuffleScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateShuffleUI();
        if (_loading || _cts.IsCancellationRequested) return;
        SafeRun(SaveShuffleAsync);
    }

    private void UpdateShuffleUI()
    {
        if (ShuffleScopePanel == null) return;

        ShuffleScopePanel.Visibility = ShuffleToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!ShuffleToggle.IsOn) return;

        int scope = ShuffleScopeCombo.SelectedIndex;
        if (scope == 1) // Favorites Only
        {
            int favCount = UiSettings.Current.WallpaperOverrides.Count(kv => kv.Value.IsFavorite);
            if (favCount == 0)
            {
                ShuffleHint.Text = "No favorites starred. Shuffle will fall back to the whole library.";
                ShuffleHint.Foreground = new SolidColorBrush(Color.FromArgb(255, 250, 204, 21));
                ShuffleHint.Visibility = Visibility.Visible;
            }
            else
            {
                ShuffleHint.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ShuffleHint.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SaveShuffleAsync()
    {
        if (_loading || _cts.IsCancellationRequested) return;

        string mode;
        if (!ShuffleToggle.IsOn)
        {
            mode = "off";
        }
        else if (ShuffleScopeCombo.SelectedIndex == 1)
        {
            mode = "favorites_only";
        }
        else
        {
            mode = "weighted"; // Whole library, but favorites appear more often
        }

        var favs = UiSettings.Current.WallpaperOverrides
            .Where(kv => kv.Value.IsFavorite)
            .Select(kv => kv.Key)
            .ToList();

        await _client.SetShuffleAsync(mode, favs, _cts.Token);
        if (_cts.IsCancellationRequested) return;

        UiSettings.Current.GlobalDefaults.ShuffleMode = (mode != "off");
        UiSettings.Current.Save();

        FlashSaved();
    }

    // ============================================================
    //  Presets
    // ============================================================
    private void OnPresetPerformanceClick(object sender, RoutedEventArgs e)
        => SafeRun(() => ApplyPresetAsync(
            PresetPerformanceFps, PresetPerformanceQuality));

    private void OnPresetBalancedClick(object sender, RoutedEventArgs e)
        => SafeRun(() => ApplyPresetAsync(
            PresetBalancedFps, PresetBalancedQuality));

    private void OnPresetQualityClick(object sender, RoutedEventArgs e)
        => SafeRun(() => ApplyPresetAsync(
            PresetQualityFps, PresetQualityQuality));

    private async Task ApplyPresetAsync(int fps, string quality)
    {
        if (_cts.IsCancellationRequested) return;

        // Flip _loading to avoid intermediate writes from slider/radio events.
        _loading = true;
        try
        {
            FpsSlider.Value = Math.Clamp(fps, 15, 60);
            FpsValueText.Text = ((int)FpsSlider.Value).ToString();

            QualityLow.IsChecked = quality == "low";
            QualityMedium.IsChecked = quality == "medium";
            QualityUltra.IsChecked = quality == "ultra";
        }
        finally
        {
            _loading = false;
        }

        var g = UiSettings.Current.GlobalDefaults;
        g.Fps = (int)FpsSlider.Value;
        g.Quality = quality;
        UiSettings.Current.Save();

        UpdatePresetVisuals();
        await _client.SetGlobalPerformanceAsync(g, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        FlashSaved();
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;

            _loading = true;
            try
            {
                FpsSlider.Value = 30;
                FpsValueText.Text = "30";
                QualityLow.IsChecked = false;
                QualityMedium.IsChecked = true;
                QualityUltra.IsChecked = false;
                HardwareDecodeToggle.IsOn = true;
                VSyncToggle.IsOn = true;
                LowLatencyToggle.IsOn = false;
            }
            finally
            {
                _loading = false;
            }

            var g = UiSettings.Current.GlobalDefaults;
            g.Fps = 30;
            g.Quality = "medium";
            g.HardwareDecoding = true;
            g.VSync = true;
            g.LowLatency = false;
            UiSettings.Current.Save();

            UpdatePresetVisuals();
            await _client.SetGlobalPerformanceAsync(g, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            FlashSaved();
        });

    // ============================================================
    //  Preset visual state
    // ============================================================
    private void UpdatePresetVisuals()
    {
        int fps = (int)FpsSlider.Value;

        string quality =
            QualityLow.IsChecked == true ? "low" :
            QualityUltra.IsChecked == true ? "ultra" :
                                              "medium";

        bool isPerformance = fps == PresetPerformanceFps
                          && quality == PresetPerformanceQuality;
        bool isBalanced = fps == PresetBalancedFps
                          && quality == PresetBalancedQuality;
        bool isQuality = fps == PresetQualityFps
                          && quality == PresetQualityQuality;

        // Performance
        PresetPerformanceButton.Content = isPerformance ? "Selected" : "Select";
        SetCardBorder(PresetPerformanceCard, isPerformance);

        // Balanced
        PresetBalancedButton.Content = isBalanced ? "Selected" : "Select";
        SetCardBorder(PresetBalancedCard, isBalanced);

        // Quality
        PresetQualityButton.Content = isQuality ? "Selected" : "Select";
        SetCardBorder(PresetQualityCard, isQuality);

        // Custom highlights when nothing matches
        bool isCustom = !isPerformance && !isBalanced && !isQuality;
        SetCardBorder(PresetCustomCard, isCustom);
    }

    private static void SetCardBorder(Border card, bool active)
    {
        card.BorderThickness = new Thickness(active ? 2 : 1);
        card.BorderBrush = new SolidColorBrush(active ? AccentBorder : NeutralBorder);
    }

    // ============================================================
    //  Saved feedback
    // ============================================================
    private void FlashSaved()
    {
        if (_cts.IsCancellationRequested) return;
        SaveBar.IsOpen = true;
        _ = Task.Delay(1500, _cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_cts.IsCancellationRequested) SaveBar.IsOpen = false;
            });
        }, TaskScheduler.Default);
    }
}