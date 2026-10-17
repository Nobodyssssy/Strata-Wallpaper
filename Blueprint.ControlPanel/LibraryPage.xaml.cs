using Blueprint.ControlPanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace Blueprint.ControlPanel;

// ═══════════════════════════════════════════════════════════════
//  Item model
// ═══════════════════════════════════════════════════════════════
public sealed class WallpaperItem : INotifyPropertyChanged
{
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Folder { get; set; } = "";

    // ── Thumbnail / loading ────────────────────────────────
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnChanged(nameof(Thumbnail)); }
    }

    private bool _thumbnailLoaded;
    public bool ThumbnailLoaded
    {
        get => _thumbnailLoaded;
        set
        {
            if (_thumbnailLoaded == value) return;
            _thumbnailLoaded = value;
            OnChanged(nameof(ThumbnailLoaded));
            OnChanged(nameof(LoadingVisibility));
        }
    }

    public Visibility LoadingVisibility
        => (!_thumbnailLoaded && !_isMissing) ? Visibility.Visible : Visibility.Collapsed;

    // ── Favorite ───────────────────────────────────────────
    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnChanged(nameof(IsFavorite));
            OnChanged(nameof(FavoriteGlyph));
            OnChanged(nameof(FavoriteColor));
        }
    }

    // ── Media type ─────────────────────────────────────────
    private string _mediaType = "video";
    public string MediaType
    {
        get => _mediaType;
        set
        {
            if (_mediaType == value) return;
            _mediaType = value;
            OnChanged(nameof(MediaType));
            OnChanged(nameof(Live2DBadgeVisibility));
            OnChanged(nameof(MetaLine));
        }
    }

    // ── Missing ────────────────────────────────────────────
    private bool _isMissing;
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing == value) return;
            _isMissing = value;
            OnChanged(nameof(IsMissing));
            OnChanged(nameof(MissingOverlayVisibility));
            OnChanged(nameof(LoadingVisibility));
        }
    }

    // ── Assignment badges ──────────────────────────────────
    private bool _assignedM1;
    public bool AssignedM1
    {
        get => _assignedM1;
        set
        {
            if (_assignedM1 == value) return; _assignedM1 = value;
            OnChanged(nameof(AssignedM1)); OnChanged(nameof(M1BadgeVisibility));
        }
    }
    private bool _assignedM2;
    public bool AssignedM2
    {
        get => _assignedM2;
        set
        {
            if (_assignedM2 == value) return; _assignedM2 = value;
            OnChanged(nameof(AssignedM2)); OnChanged(nameof(M2BadgeVisibility));
        }
    }
    private bool _assignedBoth;
    public bool AssignedBoth
    {
        get => _assignedBoth;
        set
        {
            if (_assignedBoth == value) return; _assignedBoth = value;
            OnChanged(nameof(AssignedBoth)); OnChanged(nameof(BothBadgeVisibility));
        }
    }
    private bool _assignedLock;
    public bool AssignedLock
    {
        get => _assignedLock;
        set
        {
            if (_assignedLock == value) return; _assignedLock = value;
            OnChanged(nameof(AssignedLock)); OnChanged(nameof(LockBadgeVisibility));
        }
    }

    public bool IsAssignedAnywhere
        => _assignedM1 || _assignedM2 || _assignedBoth || _assignedLock;

    // ── Metadata ───────────────────────────────────────────
    private int? _width;
    public int? Width
    {
        get => _width;
        set
        {
            _width = value; OnChanged(nameof(Width));
            OnChanged(nameof(ResolutionText)); OnChanged(nameof(MetaLine));
        }
    }
    private int? _height;
    public int? Height
    {
        get => _height;
        set
        {
            _height = value; OnChanged(nameof(Height));
            OnChanged(nameof(ResolutionText)); OnChanged(nameof(MetaLine));
        }
    }
    private double? _durationSeconds;
    public double? DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            _durationSeconds = value; OnChanged(nameof(DurationSeconds));
            OnChanged(nameof(DurationText)); OnChanged(nameof(MetaLine));
        }
    }
    private long? _fileSize;
    public long? FileSize
    {
        get => _fileSize;
        set
        {
            _fileSize = value; OnChanged(nameof(FileSize));
            OnChanged(nameof(FileSizeText)); OnChanged(nameof(MetaLine));
        }
    }

    // ── Computed bindings used by XAML ─────────────────────
    public string FavoriteGlyph => _isFavorite ? "\uE735" : "\uE734";

    public Brush FavoriteColor =>
        _isFavorite
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 204, 21))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 220, 220));

    public Visibility Live2DBadgeVisibility
        => _mediaType == "live2d" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MissingOverlayVisibility
        => _isMissing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility M1BadgeVisibility
        => _assignedM1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility M2BadgeVisibility
        => _assignedM2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BothBadgeVisibility
        => _assignedBoth ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LockBadgeVisibility
        => _assignedLock ? Visibility.Visible : Visibility.Collapsed;

    public string ResolutionText
    {
        get
        {
            if (_width is int w && _height is int h && w > 0 && h > 0)
                return $"{w}×{h}";
            return "";
        }
    }

    public string DurationText
    {
        get
        {
            if (_durationSeconds is not double d || d <= 0) return "";
            var ts = TimeSpan.FromSeconds(d);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes}:{ts.Seconds:00}";
        }
    }

    public string FileSizeText
    {
        get
        {
            if (_fileSize is not long b || b <= 0) return "";
            double mb = b / (1024.0 * 1024.0);
            return mb >= 1024 ? $"{mb / 1024:0.##} GB" : $"{mb:0.#} MB";
        }
    }

    public string MetaLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ResolutionText)) parts.Add(ResolutionText);
            if (!string.IsNullOrEmpty(DurationText)) parts.Add(DurationText);
            if (!string.IsNullOrEmpty(FileSizeText)) parts.Add(FileSizeText);
            if (!string.IsNullOrEmpty(_mediaType))
                parts.Add(char.ToUpper(_mediaType[0]) + _mediaType.Substring(1));
            return string.Join(" · ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}


// ═══════════════════════════════════════════════════════════════
//  Page
// ═══════════════════════════════════════════════════════════════
public sealed partial class LibraryPage : Page
{
    private readonly EnginePipeClient _client = new();
    private readonly ObservableCollection<WallpaperItem> _all = new();
    private readonly ObservableCollection<WallpaperItem> _visible = new();

    private CancellationTokenSource _cts = new();
    private WallpaperItem? _selected;

    private string _activeFilter = "all";
    private int _monitorCount = 1;

    // Set by another page (e.g. Home → View All Favorites) before
    // navigating here. Consumed on next Loaded.
    public static string? PendingFilter;

    // Sidebar state. Session-only for now; persistence is a follow-up
    // UiSettings.cs change.
    private string _scope = "desktop";   // "desktop" | "lock"
    private string _target = "all";       // "0" | "1" | "all"

    // Assignment state mirror from get_settings
    private string? _globalAssignmentPath;
    private readonly Dictionary<int, string> _monitorAssignments = new();
    private string? _lockAssignmentPath;

    // Debounced UiSettings save so thumbnail metadata probes don't
    // hammer disk on big libraries.
    private int _savePending;

    private static readonly string[] VideoExtensions =
        { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv" };

    public LibraryPage()
    {
        InitializeComponent();
        WallpaperGrid.ItemsSource = _visible;
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
        catch (Exception ex) { Debug.WriteLine($"[Library] {ex}"); }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        if (SortCombo != null) SortCombo.SelectedIndex = 0;

        SyncScopeButtons();
        SyncTargetButtons();

        // Consume any filter handed to us by another page (Home's
        // "View All" for favorites).
        if (!string.IsNullOrEmpty(PendingFilter))
        {
            _activeFilter = PendingFilter!;
            PendingFilter = null;

            var chips = new[] { ChipAll, ChipVideo, ChipLive2D, ChipFavorites, ChipAssigned };
            foreach (var c in chips)
                c.IsChecked = (c.Tag as string) == _activeFilter;
        }

        SafeRun(BootstrapAsync);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => _cts.Cancel();

    private async Task BootstrapAsync()
    {
        await RefreshAssignmentStateAsync();
        if (_cts.IsCancellationRequested) return;
        await RefreshAsync();
    }

    // ============================================================
    //  Assignment state
    // ============================================================
    private async Task RefreshAssignmentStateAsync()
    {
        var resp = await _client.SendAsync("get_settings", ct: _cts.Token);
        if (_cts.IsCancellationRequested || resp == null) return;

        var body = resp["body"];
        if (body == null) return;

        _monitorCount = body["monitor_count"]?.GetValue<int>() ?? 1;

        _globalAssignmentPath = body["global_assignment_path"]?.GetValue<string>();
        _lockAssignmentPath = body["lock_assignment_path"]?.GetValue<string>();

        _monitorAssignments.Clear();
        if (body["monitor_assignment_paths"] is JsonObject map)
        {
            foreach (var kv in map)
            {
                if (!int.TryParse(kv.Key, out var idx)) continue;
                var p = kv.Value?.GetValue<string>();
                if (!string.IsNullOrEmpty(p)) _monitorAssignments[idx] = p;
            }
        }

        TargetM1.IsEnabled = _monitorCount >= 1;
        TargetM2.IsEnabled = _monitorCount >= 2;
        TargetBoth.IsEnabled = true;
        TargetSpan.IsEnabled = false;   // Phase 1 — "coming soon"

        UpdateApplyButtonLabel();
    }

    // Effective assignment = per-monitor override if set, otherwise the
    // global "Both" assignment. A tile's badges reflect what a monitor
    // actually resolves to right now, not the historical clicks.
    private string? ResolveEffective(int monitorIdx)
    {
        if (_monitorAssignments.TryGetValue(monitorIdx, out var p)
            && !string.IsNullOrEmpty(p))
            return p;
        return _globalAssignmentPath;
    }

    private void ApplyAssignmentBadges()
    {
        string? effM1 = ResolveEffective(0);
        string? effM2 = ResolveEffective(1);

        foreach (var it in _all)
        {
            it.AssignedM1 = false;
            it.AssignedM2 = false;
            it.AssignedBoth = false;
            it.AssignedLock = PathEq(it.Path, _lockAssignmentPath);

            if (PathEq(it.Path, effM1) && PathEq(it.Path, effM2))
            {
                it.AssignedBoth = true;
            }
            else
            {
                it.AssignedM1 = PathEq(it.Path, effM1);
                it.AssignedM2 = PathEq(it.Path, effM2);
            }
        }
    }

    private static bool PathEq(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
           && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ============================================================
    //  Library scan
    // ============================================================
    private async Task RefreshAsync()
    {
        if (_cts.IsCancellationRequested) return;
        StatusText.Text = "Scanning...";
        _all.Clear();

        var folders = ReadFoldersFromIni();
        var items = await Task.Run(() => ScanAllFolders(folders));
        if (_cts.IsCancellationRequested) return;

        foreach (var item in items)
        {
            var cfg = UiSettings.Current.GetConfig(item.Path);
            item.IsFavorite = cfg?.IsFavorite ?? false;
            item.MediaType = cfg?.MediaType ?? GuessMediaType(item.Path);
            item.IsMissing = !File.Exists(item.Path);
            item.Width = cfg?.Width;
            item.Height = cfg?.Height;
            item.DurationSeconds = cfg?.DurationSeconds;
            item.FileSize = cfg?.FileSizeBytes ?? TryGetFileSize(item.Path);
            _all.Add(item);
        }

        ApplyAssignmentBadges();
        ApplyFilter();
        UpdateChipCounts();

        StatusText.Text = _all.Count == 0
            ? "No wallpapers yet"
            : $"{_visible.Count} of {_all.Count} wallpaper(s)";

        foreach (var item in _all)
        {
            if (item.IsMissing) { item.ThumbnailLoaded = true; continue; }
            _ = LoadThumbnailAndMetadataAsync(item);
        }
    }

    private void UpdateChipCounts()
    {
        int favCount = _all.Count(i => i.IsFavorite);
        ChipFavorites.Content = favCount > 0 ? $"Favorites  ({favCount})" : "Favorites";

        int asnCount = _all.Count(i => i.IsAssignedAnywhere);
        ChipAssigned.Content = asnCount > 0 ? $"Assigned  ({asnCount})" : "Assigned";
    }

    private async Task LoadThumbnailAndMetadataAsync(WallpaperItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);

            try
            {
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 320);
                if (thumb != null)
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    item.Thumbnail = bmp;
                }
            }
            catch { }

            try
            {
                var basic = await file.GetBasicPropertiesAsync();
                item.FileSize = (long)basic.Size;

                var video = await file.Properties.GetVideoPropertiesAsync();
                if (video.Width > 0 && video.Height > 0)
                {
                    item.Width = (int)video.Width;
                    item.Height = (int)video.Height;
                }
                if (video.Duration > TimeSpan.Zero)
                    item.DurationSeconds = video.Duration.TotalSeconds;

                // Cache in ui-settings so the details pane and future
                // sorts have it without re-probing.
                var cfg = UiSettings.Current.GetOrCreateConfig(item.Path);
                cfg.Width = item.Width;
                cfg.Height = item.Height;
                cfg.DurationSeconds = item.DurationSeconds;
                cfg.FileSizeBytes = item.FileSize;
                cfg.MetadataFetchedUtc = DateTime.UtcNow;
                ScheduleSave();
            }
            catch { }
        }
        catch { }
        finally
        {
            item.ThumbnailLoaded = true;
        }
    }

    private void ScheduleSave()
    {
        if (Interlocked.CompareExchange(ref _savePending, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Interlocked.Exchange(ref _savePending, 0);
            try { UiSettings.Current.Save(); } catch { }
        });
    }

    private static string GuessMediaType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".model3.json" or ".moc3" or ".motion3.json" => "live2d",
            _ => "video"
        };
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : null;
        }
        catch { return null; }
    }

    // ============================================================
    //  INI helpers (library folders)
    // ============================================================
    private static string GetSettingsPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blueprint");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.ini");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPrivateProfileString(
        string section, string key, string def, StringBuilder retVal,
        int size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WritePrivateProfileString(
        string section, string key, string val, string filePath);

    private static List<string> ReadFoldersFromIni()
    {
        var sb = new StringBuilder(8192);
        GetPrivateProfileString("Library", "Folders", "", sb, sb.Capacity, GetSettingsPath());
        var result = new List<string>();
        foreach (var part in sb.ToString().Split(';'))
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed) && Directory.Exists(trimmed))
                result.Add(trimmed);
        }
        return result;
    }

    private static void WriteFoldersToIni(List<string> folders)
        => WritePrivateProfileString("Library", "Folders",
            string.Join(";", folders), GetSettingsPath());

    // ============================================================
    //  Folder scan
    // ============================================================
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

    private static List<WallpaperItem> ScanAllFolders(List<string> folders)
    {
        var files = new List<string>();
        foreach (var f in folders) ScanFolder(f, files);
        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new WallpaperItem
            {
                Path = p,
                DisplayName = Path.GetFileName(p),
                Folder = Path.GetDirectoryName(p) ?? ""
            })
            .ToList();
    }

    // ============================================================
    //  Filter + sort
    // ============================================================
    private void OnFilterChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        _activeFilter = clicked.Tag as string ?? "all";

        var chips = new[] { ChipAll, ChipVideo, ChipLive2D, ChipFavorites, ChipAssigned };
        foreach (var c in chips) if (c != clicked) c.IsChecked = false;
        clicked.IsChecked = true;

        ApplyFilter();
    }

    private void OnSearchChanged(AutoSuggestBox sender,
                                 AutoSuggestBoxTextChangedEventArgs args)
        => ApplyFilter();

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WallpaperGrid == null) return;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (WallpaperGrid == null) return;
        IEnumerable<WallpaperItem> q = _all;

        switch (_activeFilter)
        {
            case "video": q = q.Where(i => i.MediaType == "video"); break;
            case "live2d": q = q.Where(i => i.MediaType == "live2d"); break;
            case "favorites": q = q.Where(i => i.IsFavorite); break;
            case "assigned": q = q.Where(i => i.IsAssignedAnywhere); break;
        }

        var search = SearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(search))
            q = q.Where(i =>
                i.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.Folder.Contains(search, StringComparison.OrdinalIgnoreCase));

        switch (SortCombo?.SelectedIndex ?? 0)
        {
            case 1:  // Name Z→A
                q = q.OrderByDescending(i => i.DisplayName, StringComparer.OrdinalIgnoreCase);
                break;
            case 2:  // Recently added (file creation time as proxy)
                q = q.OrderByDescending(i => SafeTime(i.Path, creation: true));
                break;
            case 3:  // Recently applied
                q = q.OrderByDescending(i =>
                    UiSettings.Current.GetConfig(i.Path)?.LastAppliedUtc ?? DateTime.MinValue);
                break;
            case 4:  // Favorites first
                q = q.OrderByDescending(i => i.IsFavorite)
                     .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase);
                break;
            case 5:  // Duration (shortest first)
                q = q.OrderBy(i => i.DurationSeconds ?? double.MaxValue);
                break;
            case 6:  // Resolution (smallest first)
                q = q.OrderBy(i =>
                {
                    if (i.Width is int w && i.Height is int h && w > 0 && h > 0)
                        return (long)w * h;
                    return long.MaxValue;
                });
                break;
            case 7:  // File size (smallest first)
                q = q.OrderBy(i => i.FileSize ?? long.MaxValue);
                break;
            case 8:  // Recently modified
                q = q.OrderByDescending(i => SafeTime(i.Path, creation: false));
                break;
            default: // Name A→Z
                q = q.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase);
                break;
        }

        var list = q.ToList();
        _visible.Clear();
        foreach (var item in list) _visible.Add(item);

        bool empty = _visible.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        WallpaperGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        StatusText.Text = _all.Count == 0
            ? "No wallpapers yet"
            : $"{_visible.Count} of {_all.Count}";
    }

    private static DateTime SafeTime(string path, bool creation)
    {
        try
        {
            return creation
                ? File.GetCreationTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
        }
        catch { return DateTime.MinValue; }
    }

    // ============================================================
    //  Selection → sidebar
    // ============================================================
    private void OnWallpaperItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WallpaperItem item) return;
        SelectWallpaper(item);
    }

    private void SelectWallpaper(WallpaperItem item)
    {
        if (_selected != null) _selected.PropertyChanged -= OnSelectedItemChanged;
        _selected = item;
        _selected.PropertyChanged += OnSelectedItemChanged;

        SidebarNoSelection.Visibility = Visibility.Collapsed;
        SidebarContent.Visibility = Visibility.Visible;
        DetailPane.Visibility = Visibility.Collapsed;
        SidebarBorder.Visibility = Visibility.Visible;

        SidebarFileName.Text = item.DisplayName;
        SidebarFileMeta.Text = item.IsMissing
            ? "File missing"
            : string.IsNullOrEmpty(item.MetaLine) ? item.Folder : item.MetaLine;
        SidebarPreview.Source = item.Thumbnail;

        MissingFileWarning.Visibility = item.IsMissing
            ? Visibility.Visible : Visibility.Collapsed;

        ApplyStatusText.Visibility = Visibility.Collapsed;

        UpdateApplyButtonLabel();
    }

    private void OnSelectedItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selected == null) return;

        if (e.PropertyName == nameof(WallpaperItem.Thumbnail))
        {
            var thumb = _selected.Thumbnail;
            DispatcherQueue.TryEnqueue(() => SidebarPreview.Source = thumb);
        }
        else if (e.PropertyName == nameof(WallpaperItem.MetaLine)
              || e.PropertyName == nameof(WallpaperItem.IsMissing))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_selected == null) return;
                SidebarFileMeta.Text = _selected.IsMissing
                    ? "File missing"
                    : string.IsNullOrEmpty(_selected.MetaLine)
                        ? _selected.Folder
                        : _selected.MetaLine;
            });
        }
    }

    // ============================================================
    //  Scope / target segmented controls
    // ============================================================
    private void OnScopeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton t || t.Tag is not string scope) return;
        _scope = scope;
        SyncScopeButtons();
        UpdateApplyButtonLabel();
        ApplyStatusText.Visibility = Visibility.Collapsed;
    }

    private void OnTargetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton t || t.Tag is not string target) return;
        if (target == "span") return;   // disabled in Phase 1

        _target = target;
        SyncTargetButtons();
        UpdateApplyButtonLabel();
        ApplyStatusText.Visibility = Visibility.Collapsed;
    }

    private void SyncScopeButtons()
    {
        ScopeDesktop.IsChecked = _scope == "desktop";
        ScopeLock.IsChecked = _scope == "lock";

        // Same target row serves both scopes; only the label changes.
        MonitorTargetsSection.Visibility = Visibility.Visible;
        TargetSectionLabel.Text = _scope == "lock"
            ? "Lock wallpaper target"
            : "Monitor target";

        PreviewButton.Visibility = Visibility.Visible;
        PreviewButtonText.Text = _scope == "lock"
            ? "Preview Lock Screen"
            : "Preview Desktop";
    }

    private void SyncTargetButtons()
    {
        if (_target == "span") _target = "all";

        TargetM1.IsChecked = _target == "0";
        TargetM2.IsChecked = _target == "1";
        TargetBoth.IsChecked = _target == "all";
        TargetSpan.IsChecked = false;
    }

    private void UpdateApplyButtonLabel()
    {
        if (_scope == "lock")
        {
            ApplyButtonText.Text = _target switch
            {
                "0" => "Apply to Lock — Monitor 1",
                "1" => "Apply to Lock — Monitor 2",
                _ => _monitorCount >= 2 ? "Apply to Lock — Both monitors"
                                          : "Apply to Lock Screen",
            };
            return;
        }
        ApplyButtonText.Text = _target switch
        {
            "0" => _monitorCount >= 2 ? "Apply to Monitor 1" : "Apply to Monitor",
            "1" => "Apply to Monitor 2",
            _ => _monitorCount >= 2 ? "Apply to Both monitors" : "Apply to Monitor",
        };
    }

    // ============================================================
    //  Apply
    // ============================================================
    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        SafeRun(() => ApplySelectedAsync(_selected));
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.IsMissing) return;
        SafeRun(() => ShowPreviewDialogAsync(_selected.Path));
    }

    private async Task ShowPreviewDialogAsync(string path)
    {
        bool isLock = _scope == "lock";

        PreviewOverlayTitle.Text = isLock ? "Lock Screen Preview" : "Desktop Preview";
        PreviewOverlayLabel.Text = DescribeTarget();

        PreviewOverlayClock.Visibility = isLock ? Visibility.Visible : Visibility.Collapsed;
        if (isLock)
        {
            var now = DateTime.Now;
            PreviewOverlayTime.Text = now.ToString("HH:mm");
            PreviewOverlayDate.Text = now.ToString("dddd, MMMM d");
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            PreviewMediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Preview] {ex}");
        }

        PreviewOverlay.Visibility = Visibility.Visible;
    }

    private void OnPreviewOverlayCloseClick(object sender, RoutedEventArgs e)
    {
        PreviewOverlay.Visibility = Visibility.Collapsed;
        PreviewMediaPlayer.Source = null; // Stop playback and free resources
    }

    private async Task ApplySelectedAsync(WallpaperItem item)
    {
        if (_cts.IsCancellationRequested) return;

        if (item.IsMissing)
        {
            SetApplyStatus(
                "Cannot apply: file is missing. Use “Locate file…” first.",
                error: true);
            return;
        }

        JsonNode? resp;
        if (_scope == "lock")
        {
            var body = new JsonObject
            {
                ["path"] = item.Path,
                ["monitor"] = _target
            };
            resp = await _client.SendAsync("set_lock_wallpaper", body, _cts.Token);
        }
        else
        {
            var body = new JsonObject
            {
                ["path"] = item.Path,
                ["monitor"] = _target
            };
            resp = await _client.SendAsync("apply_wallpaper", body, _cts.Token);
        }
        if (_cts.IsCancellationRequested) return;

        bool ok = resp?["status"]?.GetValue<string>() == "ok";
        if (ok)
        {
            UiSettings.Current.RecordApplied(item.Path);

            SetApplyStatus(
                _scope == "lock"
                    ? $"Lock — {DescribeTarget()} — {DateTime.Now:HH:mm:ss}"
                    : $"Desktop — {DescribeTarget()} — {DateTime.Now:HH:mm:ss}",
                error: false);

            await RefreshAssignmentStateAsync();
            ApplyAssignmentBadges();
            UpdateChipCounts();
            if (_activeFilter == "assigned") ApplyFilter();
        }
        else
        {
            SetApplyStatus(
                "Could not apply. Is the engine running?",
                error: true);
        }
    }

    private string DescribeTarget()
    {
        return _target switch
        {
            "0" => "Monitor 1",
            "1" => "Monitor 2",
            _ => _monitorCount >= 2 ? "Both monitors" : "Monitor",
        };
    }

    private void SetApplyStatus(string text, bool error)
    {
        ApplyStatusText.Text = text;
        ApplyStatusText.Foreground = new SolidColorBrush(error
            ? Windows.UI.Color.FromArgb(255, 220, 100, 100)
            : Windows.UI.Color.FromArgb(255, 120, 200, 120));
        ApplyStatusText.Visibility = Visibility.Visible;
    }

    // ============================================================
    //  Locate missing file
    // ============================================================
    private void OnLocateFileClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        SafeRun(() => LocateFileAsync(_selected));
    }

    private async Task LocateFileAsync(WallpaperItem item)
    {
        var picker = new FileOpenPicker();
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

        string oldPath = item.Path;
        string newPath = file.Path;

        var body = new JsonObject
        {
            ["old_path"] = oldPath,
            ["new_path"] = newPath
        };
        var resp = await _client.SendAsync("relocate_library_file", body, _cts.Token);
        if (_cts.IsCancellationRequested) return;

        bool ok = resp?["status"]?.GetValue<string>() == "ok";

        MigrateWallpaperConfig(oldPath, newPath);
        UiSettings.Current.Save();

        if (!ok)
        {
            await ShowDialog(
                "Could not relocate file",
                "The engine did not accept the new path. Make sure it is running, then try again. The library entry was left unchanged.");
        }

        await RefreshAssignmentStateAsync();
        await RefreshAsync();

        var reselected = _all.FirstOrDefault(i => PathEq(i.Path, newPath));
        if (reselected != null) SelectWallpaper(reselected);
    }

    private static void MigrateWallpaperConfig(string oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
        var cfg = UiSettings.Current.GetConfig(oldPath);
        if (cfg == null) return;

        UiSettings.Current.WallpaperOverrides.Remove(oldPath);
        UiSettings.Current.WallpaperOverrides[newPath] = cfg;
    }

    // ============================================================
    //  Favorites
    // ============================================================
    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not WallpaperItem item) return;

        var cfg = UiSettings.Current.GetOrCreateConfig(item.Path);
        cfg.IsFavorite = !cfg.IsFavorite;
        UiSettings.Current.Save();

        item.IsFavorite = cfg.IsFavorite;
        UpdateChipCounts();

        if (_activeFilter == "favorites") ApplyFilter();
    }

    // ============================================================
    //  Details pane
    // ============================================================
    private void OnShowDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        SidebarBorder.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Visible;
        DetailPane.Load(_selected, _monitorCount);
    }

    public void CloseDetailPane()
    {
        DetailPane.Visibility = Visibility.Collapsed;
        SidebarBorder.Visibility = Visibility.Visible;
    }

    // ============================================================
    //  Refresh + Add Folder
    // ============================================================
    private void OnRefreshClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            await _client.SendAsync("rescan_library", ct: _cts.Token);
            if (_cts.IsCancellationRequested) return;
            await RefreshAssignmentStateAsync();
            if (_cts.IsCancellationRequested) return;
            await RefreshAsync();
        });

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
        => SafeRun(async () =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null || _cts.IsCancellationRequested) return;

            var folders = ReadFoldersFromIni();
            if (!folders.Any(f => string.Equals(f, folder.Path,
                    StringComparison.OrdinalIgnoreCase)))
            {
                folders.Add(folder.Path);
                WriteFoldersToIni(folders);
            }

            var body = new JsonObject { ["path"] = folder.Path };
            await _client.SendAsync("add_library_folder", body, _cts.Token);
            if (_cts.IsCancellationRequested) return;

            await RefreshAsync();
        });

    // ============================================================
    //  Dialog helper
    // ============================================================
    private async Task ShowDialog(string title, string content)
    {
        await new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        }.ShowAsync();
    }
}