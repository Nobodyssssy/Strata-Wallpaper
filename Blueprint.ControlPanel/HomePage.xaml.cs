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

namespace Blueprint.ControlPanel;

public sealed partial class HomePage : Page
{
    public static string CurrentDisplayMode { get; private set; } = "copy";

    private readonly EnginePipeClient _client = new();
    private CancellationTokenSource _cts = new();
    private bool _loading = false;

    private bool _isPaused = false;
    private bool _isOff = false;
    private bool _engineOnline = false;
    private bool _autoStart = false;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _wallThumbTimer;
    private int _collectionsTab = 0;   // 0 = favorites, 1 = recent
    private readonly Random _rng = new();

    private int _monitorCount = 1;
    private string? _globalAssignmentPath;
    private readonly Dictionary<int, string> _monitorAssignments = new();
    private string? _lockAssignmentPath;
    private int _currentFps = 25;
    private string _currentQuality = "balanced";

    private static readonly string[] Tips =
    {
        "Use the Performance tab to find the best balance between smooth playback and low CPU/GPU usage.",
        "Right-click any wallpaper tile in the Wallpapers page to favorite it — favorites appear first in the filter chips.",
        "Set a different FPS for a heavy 4K wallpaper and a light one: each wallpaper can override the global default.",
        "The lock screen overlay can follow the desktop Day / Night schedule, or use its own videos and schedule.",
        "Drag folders onto the Wallpapers page to add them to your library instantly.",
    };
    private int _tipIndex = 0;
    private DispatcherTimer? _tipTimer;

    public HomePage()
    {
        Debug.WriteLine("[HomePage] ctor");
        InitializeComponent();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private static async void SafeRun(Func<Task> body)
    {
        try { await body(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Strata Home] {ex}");
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        UpdateCardClock();
        SafeRun(RefreshAsync);
        StartTipRotator();
        StartPollTimer();
        StartWallThumbRotator();
        UpdateSettingsCard();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        _tipTimer?.Stop();
        _pollTimer?.Stop();
        _wallThumbTimer?.Stop();
    }

    // ============================================================
    //  Timers
    // ============================================================
    private void StartPollTimer()
    {
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollTimer.Tick += (_, __) => SafeRun(PollEngineStatusAsync);
        _pollTimer.Start();
    }

    private async Task PollEngineStatusAsync()
    {
        if (_cts.IsCancellationRequested) return;
        if (_loading) return;

        // Cheap, always: keep the lock card clock current.
        UpdateCardClock();

        var resp = await _client.SendAsync("ping", ct: _cts.Token);
        if (_cts.IsCancellationRequested) return;

        bool online = resp != null;
        if (online != _engineOnline)
        {
            await RefreshAsync();
        }
    }

    private void StartWallThumbRotator()
    {
        _wallThumbTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _wallThumbTimer.Tick += (_, __) => RefreshWallpaperCardThumbs();
        _wallThumbTimer.Start();
        RefreshWallpaperCardThumbs();
    }

    // Draw the current time / date on the Lock Screen card so the
    // preview matches what the user actually sees on the overlay.
    private void UpdateCardClock()
    {
        if (CardLockClockTime == null || CardLockClockDate == null) return;

        var now = DateTime.Now;
        CardLockClockTime.Text = $"{now.Hour:00}:{now.Minute:00}";
        CardLockClockDate.Text = now.ToString("dddd, MMMM d");
    }

    // ============================================================
    //  Refresh
    // ============================================================
    private async Task RefreshAsync()
    {
        BuildCollections();
        BuildHeroPreviews();

        _loading = true;
        try
        {
            var resp = await _client.SendAsync("get_settings", ct: _cts.Token);
            if (_cts.IsCancellationRequested) return;

            if (resp == null)
            {
                ApplyOffline();
                return;
            }

            var body = resp["body"];
            _isPaused = body?["paused"]?.GetValue<bool>() ?? false;
            _isOff = body?["turned_off"]?.GetValue<bool>() ?? false;
            ApplyOnline(body);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyOffline()
    {
        _engineOnline = false;
        StatusDot.Fill = new SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 107, 114, 128));
        StatusLabel.Text = "Engine offline";
        StatusSubtitle.Text = "Start the engine to display wallpapers.";

        // Monitor count comes from Windows, not the engine — we can
        // show the right number of hero cells even when offline.
        int monitors = GetSystemMetrics(SM_CMONITORS);
        _monitorCount = monitors > 0 ? monitors : 1;
        StatMonitors.Text = _monitorCount.ToString();

        StatFps.Text = "—";
        StatDisplayMode.Text = "—";

        // Read the assignment paths straight out of settings.ini. The
        // engine persists them there on every apply, so the panel can
        // show the correct wallpapers even when the engine is down.
        LoadAssignmentsFromIni();
        BuildHeroPreviews();

        CardWallThumbA.Source = null;
        CardWallThumbB.Source = null;
        CardPerfFps.Text = "—";
        CardPerfQuality.Text = "—";

        // Lock card still shows whatever the INI says was set.
        if (!string.IsNullOrEmpty(_lockAssignmentPath))
            _ = LoadCardThumbAsync(CardLockThumb, _lockAssignmentPath);
        else
            CardLockThumb.Source = null;

        StartEngineButton.Visibility = Visibility.Visible;
        EngineControlRow.Visibility = Visibility.Collapsed;
    }

    // Reads the persisted assignment paths from settings.ini. Used
    // while the engine is offline so the Home hero and Lock card
    // still reflect the user's setup.
    private void LoadAssignmentsFromIni()
    {
        string ini = GetSettingsPath();

        _monitorAssignments.Clear();

        // Global assignment
        {
            var sb = new System.Text.StringBuilder(512);
            GetPrivateProfileString("Wallpaper", "VideoPath", "",
                sb, sb.Capacity, ini);
            string p = sb.ToString();
            _globalAssignmentPath =
                (!string.IsNullOrEmpty(p) && File.Exists(p)) ? p : null;
        }

        // Per-monitor assignments
        for (int i = 0; i < 4; i++)
        {
            var sb = new System.Text.StringBuilder(512);
            GetPrivateProfileString($"Monitor{i}", "VideoPath", "",
                sb, sb.Capacity, ini);
            string p = sb.ToString();
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                _monitorAssignments[i] = p;
        }

        // Lock wallpaper
        {
            var sb = new System.Text.StringBuilder(512);
            GetPrivateProfileString("Lock", "WallpaperOverride", "",
                sb, sb.Capacity, ini);
            string p = sb.ToString();
            _lockAssignmentPath =
                (!string.IsNullOrEmpty(p) && File.Exists(p)) ? p : null;
        }
    }

    private void ApplyOnline(JsonNode? body)
    {
        _engineOnline = true;
        StatusDot.Fill = new SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 34, 197, 94));
        StatusLabel.Text = "Running";
        StatusSubtitle.Text = "Strata Wallpaper Engine is active.";

        _monitorCount = body?["monitor_count"]?.GetValue<int>() ?? 1;
        if (_monitorCount < 1) _monitorCount = 1;

        int fps = body?["target_fps"]?.GetValue<int>() ?? 25;
        _currentFps = fps;
        _currentQuality = body?["quality"]?.GetValue<string>() ?? "balanced";

        StatMonitors.Text = _monitorCount.ToString();
        StatFps.Text = fps > 0 ? fps.ToString() : "—";

        _globalAssignmentPath = SafeString(body?["global_assignment_path"]);
        _lockAssignmentPath = SafeString(body?["lock_assignment_path"]);

        _monitorAssignments.Clear();
        if (body?["monitor_assignment_paths"] is JsonObject map)
        {
            foreach (var kv in map)
            {
                if (!int.TryParse(kv.Key, out var idx)) continue;
                var p = SafeString(kv.Value);
                if (!string.IsNullOrEmpty(p)) _monitorAssignments[idx] = p;
            }
        }

        _autoStart = body?["auto_start"]?.GetValue<bool>() ?? false;

        BuildHeroPreviews();

        string mode = body?["display_mode"]?.GetValue<string>() ?? "copy";
        CurrentDisplayMode = mode;
        StatDisplayMode.Text = mode switch
        {
            "stretch" => "Stretch",
            "individual" => "Indiv.",
            _ => "Copy"
        };

        // Card summaries
        CardPerfFps.Text = fps > 0 ? $"{fps} FPS" : "Unlimited";
        CardPerfQuality.Text = QualityDisplayName(_currentQuality);

        if (!string.IsNullOrEmpty(_lockAssignmentPath))
            _ = LoadCardThumbAsync(CardLockThumb, _lockAssignmentPath);

        UpdateSettingsCard();

        StartEngineButton.Visibility = Visibility.Collapsed;
        EngineControlRow.Visibility = Visibility.Visible;
        UpdatePauseButton();
        UpdatePowerButton();
    }

    private static string QualityDisplayName(string q) => q switch
    {
        "fast" => "Fast",
        "high" => "High",
        "vsr" => "RTX VSR",
        _ => "Balanced"
    };

    private static string? SafeString(JsonNode? n)
    {
        if (n == null) return null;
        try { return n.GetValue<string>(); }
        catch { return null; }
    }

    // ============================================================
    //  Wallpaper card thumbnails — random from library
    // ============================================================
    private void RefreshWallpaperCardThumbs()
    {
        var folders = ReadFoldersFromIni();
        if (folders.Count == 0)
        {
            CardWallThumbA.Source = null;
            CardWallThumbB.Source = null;
            return;
        }

        var pool = new List<string>();
        foreach (var f in folders) ScanFolder(f, pool);

        if (pool.Count == 0)
        {
            CardWallThumbA.Source = null;
            CardWallThumbB.Source = null;
            return;
        }

        // Pick 2 distinct files (or 1 if the library only has one).
        int a = _rng.Next(pool.Count);
        int b = a;
        if (pool.Count > 1)
        {
            while (b == a) b = _rng.Next(pool.Count);
        }

        _ = LoadCardThumbAsync(CardWallThumbA, pool[a]);
        if (pool.Count > 1)
            _ = LoadCardThumbAsync(CardWallThumbB, pool[b]);
        else
            CardWallThumbB.Source = null;
    }

    private static async Task LoadCardThumbAsync(Image target, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 400);
            if (thumb != null)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                target.Source = bmp;
            }
        }
        catch { }
    }

    // ============================================================
    //  Settings card — read from UiSettings + engine auto-start
    // ============================================================
    private void UpdateSettingsCard()
    {
        string theme = UiSettings.Current.Theme switch
        {
            "Dark" => "Dark",
            "Light" => "Light",
            _ => "System"
        };
        CardSettingsTheme.Text = theme;

        CardSettingsAutoStart.Text = _autoStart ? "On" : "Off";

        string close = UiSettings.Current.CloseAction switch
        {
            "exit" => "Exit",
            "tray" => "Tray",
            _ => "Ask"
        };
        CardSettingsClose.Text = close;
    }

    // ============================================================
    //  Hero — per-monitor previews
    // ============================================================
    private void BuildHeroPreviews()
    {
        if (HeroPreviewsHost == null) return;

        HeroPreviewsHost.Children.Clear();
        HeroPreviewsHost.ColumnDefinitions.Clear();

        int count = Math.Max(1, _monitorCount);

        string? firstPath = ResolveMonitorDisplay(0).path;
        bool allSame = true;
        for (int i = 1; i < count; i++)
        {
            if (!string.Equals(ResolveMonitorDisplay(i).path, firstPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                allSame = false;
                break;
            }
        }

        if (count <= 1 || allSame)
        {
            HeroPreviewsHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            string label = count > 1
                ? $"Both Monitors  ·  {_currentFps} FPS"
                : $"Desktop  ·  {_currentFps} FPS";

            var cell = BuildPreviewCell(firstPath, 0, label, 1);
            Grid.SetColumn(cell, 0);
            HeroPreviewsHost.Children.Add(cell);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                HeroPreviewsHost.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }

            for (int i = 0; i < count; i++)
            {
                var (path, _) = ResolveMonitorDisplay(i);
                string label = $"Monitor {i + 1}  ·  {_currentFps} FPS";
                var cell = BuildPreviewCell(path, i, label, count);
                Grid.SetColumn(cell, i);
                HeroPreviewsHost.Children.Add(cell);
            }
        }
    }

    private (string? path, string slot) ResolveMonitorDisplay(int idx)
    {
        if (_monitorAssignments.TryGetValue(idx, out var perMonitor)
            && !string.IsNullOrEmpty(perMonitor))
            return (perMonitor, "Desktop");

        if (!string.IsNullOrEmpty(_globalAssignmentPath))
            return (_globalAssignmentPath, "Desktop");

        if (!string.IsNullOrEmpty(_lockAssignmentPath))
            return (_lockAssignmentPath, "Lock");

        return (null, "Desktop");
    }

    private FrameworkElement BuildPreviewCell(string? path, int monitorIdx,
                                              string infoLabel, int totalCells)
    {
        var host = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0))
        };

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var img = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            host.Children.Add(img);
            _ = LoadHeroThumbAsync(img, path);
        }
        else
        {
            var placeholder = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            placeholder.Children.Add(new FontIcon
            {
                Glyph = "\uE8B7",
                FontSize = 40,
                Foreground = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 90, 90, 90))
            });
            placeholder.Children.Add(new TextBlock
            {
                Text = "No wallpaper assigned",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 140, 140, 140))
            });
            host.Children.Add(placeholder);
        }

        var gradient = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 130,
            IsHitTestVisible = false
        };
        var lgb = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        lgb.GradientStops.Add(new GradientStop
        {
            Offset = 0.0,
            Color = Windows.UI.Color.FromArgb(0, 0, 0, 0)
        });
        lgb.GradientStops.Add(new GradientStop
        {
            Offset = 1.0,
            Color = Windows.UI.Color.FromArgb(210, 0, 0, 0)
        });
        gradient.Background = lgb;
        host.Children.Add(gradient);

        var infoBox = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18, 0, 0, 16),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(90, 0, 0, 0))
        };

        var infoRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        infoRow.Children.Add(new FontIcon
        {
            Glyph = "\uE7F8",
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 255, 255, 255))
        });

        var textStack = new StackPanel { Spacing = 1 };
        textStack.Children.Add(new TextBlock
        {
            Text = "Current Wallpaper",
            FontSize = 10,
            Foreground = new SolidColorBrush(
                Windows.UI.Color.FromArgb(176, 255, 255, 255))
        });

        string filename = !string.IsNullOrEmpty(path)
            ? Path.GetFileName(path)
            : "Nothing assigned";
        textStack.Children.Add(new TextBlock
        {
            Text = filename,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = totalCells > 1 ? 200 : 380
        });

        textStack.Children.Add(new TextBlock
        {
            Text = infoLabel,
            FontSize = 11,
            Foreground = new SolidColorBrush(
                Windows.UI.Color.FromArgb(204, 255, 255, 255))
        });

        infoRow.Children.Add(textStack);
        infoBox.Child = infoRow;
        host.Children.Add(infoBox);

        if (totalCells > 1 && monitorIdx > 0)
        {
            var sep = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(80, 255, 255, 255))
            };
            host.Children.Add(sep);
        }

        return host;
    }

    private static async Task LoadHeroThumbAsync(Image target, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, 1920);
            if (thumb != null)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                target.Source = bmp;
            }
        }
        catch { }
    }

    // ============================================================
    //  Pause / Turn Off
    // ============================================================
    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            string cmd = _isPaused ? "resume" : "pause";
            await _client.SendAsync(cmd, ct: _cts.Token);
            if (_cts.IsCancellationRequested) return;
            _isPaused = !_isPaused;
            UpdatePauseButton();
        });

    private void UpdatePauseButton()
    {
        PauseLabel.Text = _isPaused ? "Resume" : "Pause";
        PauseIcon.Glyph = _isPaused ? "\uE768" : "\uE769";
    }

    private void OnTurnOffOnClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (_cts.IsCancellationRequested) return;

            if (!_engineOnline)
            {
                OnStartEngineClick(sender, e);
                return;
            }

            await _client.SendAsync("turn_off", ct: _cts.Token);

            for (int i = 0; i < 12 && !_cts.IsCancellationRequested; i++)
            {
                await Task.Delay(400, _cts.Token);
                var ping = await _client.SendAsync("ping", ct: _cts.Token);
                if (_cts.IsCancellationRequested) return;
                if (ping == null) break;
            }

            await RefreshAsync();
        });

    private void UpdatePowerButton()
    {
        PowerLabel.Text = _engineOnline ? "Turn Off" : "Turn On";
        PowerIcon.Glyph = "\uE7E8";
    }

    // ============================================================
    //  Navigation
    // ============================================================
    private void OnChangeWallpaperClick(object sender, RoutedEventArgs e)
        => NavigateToTag("wallpapers");

    private void OnQuickActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
            NavigateToTag(tag);
    }

    private static void NavigateToTag(string tag)
    {
        if (App.MainWindow is MainWindow mw) mw.NavigateToTag(tag);
    }

    // ============================================================
    //  Quick links
    // ============================================================
    private void OnOpenWallpaperFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Blueprint");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine($"[OpenFolder] {ex}"); }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Blueprint", "engine.log");
            if (File.Exists(log))
                Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine($"[OpenLogs] {ex}"); }
    }

    // ============================================================
    //  Collections card — favorites / recents toggle
    // ============================================================
    private void OnCollectionsTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton t) return;

        if (t == TabFavorites) _collectionsTab = 0;
        else if (t == TabRecents) _collectionsTab = 1;

        TabFavorites.IsChecked = _collectionsTab == 0;
        TabRecents.IsChecked = _collectionsTab == 1;

        ApplyCollectionsTab();
    }

    private void ApplyCollectionsTab()
    {
        bool fav = _collectionsTab == 0;

        FavoritesList.Visibility = fav ? Visibility.Visible : Visibility.Collapsed;
        FavoritesEmpty.Visibility = (fav && FavoritesList.Children.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;

        RecentList.Visibility = fav ? Visibility.Collapsed : Visibility.Visible;
        RecentEmpty.Visibility = (!fav && RecentList.Children.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildCollections()
    {
        FavoritesList.Children.Clear();
        RecentList.Children.Clear();

        // Favorites
        var favPaths = UiSettings.Current.WallpaperOverrides
            .Where(kv => kv.Value.IsFavorite)
            .Select(kv => kv.Key)
            .ToList();

        int favShown = 0;
        foreach (var path in favPaths)
        {
            if (favShown >= 5) break;
            if (!File.Exists(path)) continue;
            FavoritesList.Children.Add(MakeListTile(
                path, Path.GetFileName(path), "",
                OnFavoriteItemClick, showStar: true));
            favShown++;
        }

        // Recents
        var recent = UiSettings.Current.RecentWallpapers;
        int recentShown = 0;
        foreach (var r in recent)
        {
            if (recentShown >= 5) break;
            if (!File.Exists(r.Path)) continue;
            RecentList.Children.Add(MakeListTile(
                r.Path, Path.GetFileName(r.Path), HumanizeUtc(r.AppliedUtc),
                OnRecentClick, showStar: false));
            recentShown++;
        }

        ApplyCollectionsTab();
    }

    private void OnCollectionsViewAllClick(object sender, RoutedEventArgs e)
    {
        if (_collectionsTab == 0)
        {
            LibraryPage.PendingFilter = "favorites";
            NavigateToTag("wallpapers");
        }
        else
        {
            LibraryPage.PendingFilter = null;
            NavigateToTag("wallpapers");
        }
    }

    private Button MakeListTile(string path,
                                string name,
                                string subtitle,
                                RoutedEventHandler onClick,
                                bool showStar)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6),
            MinHeight = 76,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0)
        };
        btn.Tag = path;
        btn.Click += onClick;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var thumbBorder = new Border
        {
            Width = 132,
            Height = 74,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 36))
        };
        var img = new Image { Stretch = Stretch.UniformToFill };
        thumbBorder.Child = img;
        row.Children.Add(thumbBorder);
        _ = LoadCardThumbAsync(img, path);

        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        if (showStar)
        {
            nameRow.Children.Add(new FontIcon
            {
                Glyph = "\uE735",
                FontSize = 11,
                Foreground = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 250, 204, 21))
            });
        }

        nameRow.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 130,
            VerticalAlignment = VerticalAlignment.Center
        });

        text.Children.Add(nameRow);

        if (!string.IsNullOrEmpty(subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        row.Children.Add(text);
        btn.Content = row;
        return btn;
    }

    private static string HumanizeUtc(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
        return utc.ToLocalTime().ToString("MMM d");
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (sender is not Button b || b.Tag is not string path) return;
            if (_cts.IsCancellationRequested) return;

            var cfg = UiSettings.Current.GetConfig(path);
            bool ok = await _client.ApplyWallpaperAsync(path, cfg, "all", _cts.Token);
            if (_cts.IsCancellationRequested) return;

            if (ok)
            {
                UiSettings.Current.RecordApplied(path);
                await RefreshAsync();
            }
        });

    private void OnFavoriteItemClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            if (sender is not Button b || b.Tag is not string path) return;
            if (_cts.IsCancellationRequested) return;

            var cfg = UiSettings.Current.GetConfig(path);
            bool ok = await _client.ApplyWallpaperAsync(path, cfg, "all", _cts.Token);
            if (_cts.IsCancellationRequested) return;
            if (ok)
            {
                UiSettings.Current.RecordApplied(path);
                await RefreshAsync();
            }
        });

    // ============================================================
    //  Library scan (for the random thumbs)
    // ============================================================
    private static string GetSettingsPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blueprint");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.ini");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetPrivateProfileString(
        string section, string key, string def,
        System.Text.StringBuilder retVal, int size, string filePath);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CMONITORS = 80;

    private static List<string> ReadFoldersFromIni()
    {
        var sb = new System.Text.StringBuilder(8192);
        GetPrivateProfileString("Library", "Folders", "", sb, sb.Capacity,
            GetSettingsPath());
        var result = new List<string>();
        foreach (var part in sb.ToString().Split(';'))
        {
            var t = part.Trim();
            if (!string.IsNullOrEmpty(t) && Directory.Exists(t))
                result.Add(t);
        }
        return result;
    }

    private static readonly string[] VideoExtensions =
        { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv" };

    private static void ScanFolder(string folder, List<string> outFiles)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (VideoExtensions.Contains(ext)) outFiles.Add(file);
            }
            foreach (var sub in Directory.EnumerateDirectories(folder))
                ScanFolder(sub, outFiles);
        }
        catch { }
    }

    // ============================================================
    //  Start Engine
    // ============================================================
    private void OnStartEngineClick(object sender, RoutedEventArgs e)
     => SafeRun(async () =>
     {
         try
         {
             string? enginePath = await Task.Run(() =>
             {
                 string? p = UiSettings.Current.EnginePath;
                 if (string.IsNullOrEmpty(p) || !File.Exists(p))
                     p = FindEnginePath();
                 return p;
             });

             if (enginePath == null)
             {
                 await ShowMessage("Engine not found",
                     "Strata could not locate the engine executable.\n\n" +
                     "Searched from:\n" + AppContext.BaseDirectory + "\n\n" +
                     "walking up 12 parent folders and checking the sibling " +
                     "Blueprintengine / BlueprintEngine / Blueprint project folders " +
                     "for any of:\n" +
                     "  • StrataWallpaper.exe\n" +
                     "  • Blueprintengine.exe\n" +
                     "  • BlueprintEngine.exe\n" +
                     "  • Blueprint.exe\n\n" +
                     "If your engine is named differently, open:\n" +
                     "%LOCALAPPDATA%\\Blueprint\\ui-settings.json\n" +
                     "and set   \"EnginePath\": \"C:\\\\full\\\\path\\\\to\\\\your.exe\"   " +
                     "then click Start Engine again.");
                 return;
             }

             await Task.Run(() =>
             {
                 UiSettings.Current.EnginePath = enginePath;
                 UiSettings.Current.Save();
                 LaunchEngine(enginePath);
             });

             await WaitForEngineAsync();
         }
         catch (Exception ex)
         {
             await ShowMessage("Could not start engine", ex.Message);
         }
     });

    private static void LaunchEngine(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? ""
        });
    }

    private async Task WaitForEngineAsync()
    {
        for (int i = 0; i < 20 && !_cts.IsCancellationRequested; i++)
        {
            await Task.Delay(300, _cts.Token);
            var resp = await _client.SendAsync("ping", ct: _cts.Token);
            if (_cts.IsCancellationRequested) return;
            if (resp != null)
            {
                await RefreshAsync();
                return;
            }
        }
    }

    private static string? FindEnginePath()
    {
        var exeNames = new[]
        {
            "StrataWallpaper.exe",
            "Blueprintengine.exe",
            "BlueprintEngine.exe",
            "Blueprint.exe"
        };

        var subpaths = new[]
        {
            "",
            "x64\\Release",
            "x64\\Debug",
            "Release",
            "Debug",
            "build",
            "bin",
            "bin\\x64\\Release",
            "bin\\x64\\Debug"
        };

        foreach (var name in exeNames)
        {
            var p = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(p)) return p;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            foreach (var name in exeNames)
            {
                var p = Path.Combine(dir.FullName, name);
                if (File.Exists(p)) return p;
            }

            foreach (var sub in subpaths)
            {
                if (string.IsNullOrEmpty(sub)) continue;
                var probe = Path.Combine(dir.FullName, sub);
                if (!Directory.Exists(probe)) continue;

                foreach (var name in exeNames)
                {
                    var p = Path.Combine(probe, name);
                    if (File.Exists(p)) return p;
                }
            }

            foreach (var project in new[] { "Blueprintengine", "BlueprintEngine", "Blueprint" })
            {
                var projectRoot = Path.Combine(dir.FullName, project);
                if (!Directory.Exists(projectRoot)) continue;

                foreach (var sub in subpaths)
                {
                    var probe = string.IsNullOrEmpty(sub)
                        ? projectRoot
                        : Path.Combine(projectRoot, sub);
                    if (!Directory.Exists(probe)) continue;

                    foreach (var name in exeNames)
                    {
                        var p = Path.Combine(probe, name);
                        if (File.Exists(p)) return p;
                    }
                }
            }
        }

        return null;
    }

    private async Task ShowMessage(string title, string content)
    {
        await new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        }.ShowAsync();
    }

    // ============================================================
    //  Tips
    // ============================================================
    private void StartTipRotator()
    {
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _tipTimer.Tick += (_, __) =>
        {
            _tipIndex = (_tipIndex + 1) % Tips.Length;
            TipText.Text = Tips[_tipIndex];
            UpdateTipDots();
        };
        TipText.Text = Tips[0];
        UpdateTipDots();
        _tipTimer.Start();
    }

    private void UpdateTipDots()
    {
        var dots = new[] { TipDot0, TipDot1, TipDot2, TipDot3, TipDot4 };
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = new SolidColorBrush(
                i == _tipIndex
                    ? Windows.UI.Color.FromArgb(255, 74, 144, 226)
                    : Windows.UI.Color.FromArgb(255, 68, 68, 68));
        }
    }
}