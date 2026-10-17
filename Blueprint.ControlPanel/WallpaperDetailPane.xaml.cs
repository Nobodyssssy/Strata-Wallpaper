using Blueprint.ControlPanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Blueprint.ControlPanel;

public sealed partial class WallpaperDetailPane : UserControl
{
    private WallpaperItem? _item;
    private int _monitorCount = 1;
    private bool _loading;

    // Bound to the GridView. Single source of truth for tags while editing.
    public ObservableCollection<string> Tags { get; } = new();

    // Filename-token suggestions. Only offered; never auto-added.
    private static readonly string[] KnownTagTokens =
    {
        "4K", "2160p", "1440p", "1080p", "720p", "HDR", "HDR10",
        "loop", "seamless", "abstract", "nature", "city", "space",
        "anime", "cyberpunk", "minimal", "dark", "light",
        "day", "night", "rain", "snow", "fire", "water",
        "live2d", "short", "long",
    };

    public WallpaperDetailPane()
    {
        InitializeComponent();
        TagsGrid.ItemsSource = Tags;
    }

    // ============================================================
    //  Public API — called by LibraryPage
    // ============================================================
    public void Load(WallpaperItem item, int monitorCount)
    {
        _item = item;
        _monitorCount = monitorCount;

        _loading = true;
        try
        {
            var cfg = UiSettings.Current.GetOrCreateConfig(item.Path);

            DetailFileName.Text = item.DisplayName;
            DetailFolder.Text = item.Folder;
            DetailPreview.Source = item.Thumbnail;

            UpdateFavoriteVisual(item.IsFavorite);
            UpdateAssignmentBadges(item);

            DetailResolution.Text = string.IsNullOrEmpty(item.ResolutionText)
                ? "—" : item.ResolutionText;
            DetailDuration.Text = string.IsNullOrEmpty(item.DurationText)
                ? "—" : item.DurationText;
            DetailSize.Text = string.IsNullOrEmpty(item.FileSizeText)
                ? "—" : item.FileSizeText;
            DetailType.Text = string.IsNullOrEmpty(item.MediaType)
                ? "—"
                : char.ToUpper(item.MediaType[0]) + item.MediaType.Substring(1);

            Tags.Clear();
            foreach (var t in cfg.Tags)
                if (!string.IsNullOrWhiteSpace(t))
                    Tags.Add(t);
            RefreshSuggestions();

            SourceUrlBox.Text = cfg.SourceUrl ?? "";
            AuthorBox.Text = cfg.Author ?? "";
            LicenseBox.Text = cfg.License ?? "";
            NotesBox.Text = cfg.Notes ?? "";

            MissingBanner.Visibility = item.IsMissing
                ? Visibility.Visible : Visibility.Collapsed;

            UpdateOpenSourceEnabled();
        }
        finally
        {
            _loading = false;
        }

        // Keep the preview live if the thumbnail arrives after Load().
        item.PropertyChanged -= OnItemPropertyChanged;
        item.PropertyChanged += OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not WallpaperItem it) return;
        if (e.PropertyName == nameof(WallpaperItem.Thumbnail))
        {
            var thumb = it.Thumbnail;
            DispatcherQueue.TryEnqueue(() => DetailPreview.Source = thumb);
        }
        else if (e.PropertyName == nameof(WallpaperItem.IsFavorite))
        {
            bool fav = it.IsFavorite;
            DispatcherQueue.TryEnqueue(() => UpdateFavoriteVisual(fav));
        }
        else if (e.PropertyName == nameof(WallpaperItem.AssignedM1)
              || e.PropertyName == nameof(WallpaperItem.AssignedM2)
              || e.PropertyName == nameof(WallpaperItem.AssignedBoth)
              || e.PropertyName == nameof(WallpaperItem.AssignedLock))
        {
            DispatcherQueue.TryEnqueue(() => UpdateAssignmentBadges(it));
        }
        else if (e.PropertyName == nameof(WallpaperItem.MetaLine))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                DetailResolution.Text = string.IsNullOrEmpty(it.ResolutionText)
                    ? "—" : it.ResolutionText;
                DetailDuration.Text = string.IsNullOrEmpty(it.DurationText)
                    ? "—" : it.DurationText;
                DetailSize.Text = string.IsNullOrEmpty(it.FileSizeText)
                    ? "—" : it.FileSizeText;
            });
        }
    }

    // ============================================================
    //  Visual updates
    // ============================================================
    private void UpdateFavoriteVisual(bool fav)
    {
        FavoriteIcon.Glyph = fav ? "\uE735" : "\uE734";
        FavoriteIcon.Foreground = fav
            ? (SolidColorBrush)Application.Current.Resources["FavoriteBrush"]
            : (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void UpdateAssignmentBadges(WallpaperItem item)
    {
        BadgeM1.Visibility = item.AssignedM1 ? Visibility.Visible : Visibility.Collapsed;
        BadgeM2.Visibility = item.AssignedM2 ? Visibility.Visible : Visibility.Collapsed;
        BadgeBoth.Visibility = item.AssignedBoth ? Visibility.Visible : Visibility.Collapsed;
        BadgeLock.Visibility = item.AssignedLock ? Visibility.Visible : Visibility.Collapsed;

        bool any = item.AssignedM1 || item.AssignedM2
                || item.AssignedBoth || item.AssignedLock;
        NoAssignmentText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============================================================
    //  Close / back
    // ============================================================
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // Restore the sibling SidebarBorder in the parent grid, then hide.
        if (VisualTreeHelper.GetParent(this) is Panel parent)
        {
            foreach (var child in parent.Children)
            {
                if (child is FrameworkElement fe && fe.Name == "SidebarBorder")
                {
                    fe.Visibility = Visibility.Visible;
                    break;
                }
            }
        }
        Visibility = Visibility.Collapsed;
    }

    // ============================================================
    //  Favorite
    // ============================================================
    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;
        var cfg = UiSettings.Current.GetOrCreateConfig(_item.Path);
        cfg.IsFavorite = !cfg.IsFavorite;
        UiSettings.Current.Save();

        _item.IsFavorite = cfg.IsFavorite;
        UpdateFavoriteVisual(cfg.IsFavorite);
    }

    // ============================================================
    //  Tags
    // ============================================================
    private void OnNewTagKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitNewTag();
            e.Handled = true;
        }
    }

    private void OnAddTagClick(object sender, RoutedEventArgs e) => CommitNewTag();

    private void CommitNewTag()
    {
        if (_item == null) return;

        var raw = NewTagBox.Text?.Trim();
        if (string.IsNullOrEmpty(raw)) return;

        // Split on comma/space so "cyberpunk dark" adds two.
        foreach (var piece in raw.Split(new[] { ',', ';' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = piece.Trim();
            if (tag.Length == 0) continue;
            if (Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                continue;
            Tags.Add(tag);
        }

        NewTagBox.Text = "";
        SyncTagsToConfig();
        RefreshSuggestions();
    }

    private void OnAddSuggestedTagClick(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;
        if (sender is not Button b || b.Tag is not string tag) return;
        if (Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            return;

        Tags.Add(tag);
        SyncTagsToConfig();
        RefreshSuggestions();
    }

    private void OnRemoveTagClick(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;
        if (sender is not Button b || b.Tag is not string tag) return;

        var match = Tags.FirstOrDefault(t =>
            string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;

        Tags.Remove(match);
        SyncTagsToConfig();
        RefreshSuggestions();
    }

    private void SyncTagsToConfig()
    {
        if (_item == null) return;
        var cfg = UiSettings.Current.GetOrCreateConfig(_item.Path);
        cfg.Tags = Tags.ToList();
        UiSettings.Current.Save();
    }

    private void RefreshSuggestions()
    {
        if (_item == null)
        {
            SuggestionsSection.Visibility = Visibility.Collapsed;
            return;
        }

        var filename = Path.GetFileNameWithoutExtension(_item.Path).ToLowerInvariant();
        var existing = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase);

        var suggestions = KnownTagTokens
            .Where(t => !existing.Contains(t)
                        && filename.Contains(t.ToLowerInvariant()))
            .ToList();

        SuggestionsRepeater.ItemsSource = suggestions;
        SuggestionsSection.Visibility = suggestions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================
    //  Source URL
    // ============================================================
    private void OnSourceUrlChanged(object sender, TextChangedEventArgs e)
        => UpdateOpenSourceEnabled();

    private void OnSourceUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _item == null) return;
        var cfg = UiSettings.Current.GetOrCreateConfig(_item.Path);
        cfg.SourceUrl = string.IsNullOrWhiteSpace(SourceUrlBox.Text)
            ? null : SourceUrlBox.Text.Trim();
        UiSettings.Current.Save();
    }

    private void UpdateOpenSourceEnabled()
    {
        OpenSourceButton.IsEnabled = IsValidUrl(SourceUrlBox.Text);
    }

    private static bool IsValidUrl(string? s)
        => !string.IsNullOrWhiteSpace(s)
        && Uri.TryCreate(s, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void OnOpenSourceClick(object sender, RoutedEventArgs e)
    {
        var url = SourceUrlBox.Text?.Trim();
        if (!IsValidUrl(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Details] Open source failed: {ex.Message}");
        }
    }

    // ============================================================
    //  Author / License / Notes
    // ============================================================
    private void OnAuthorLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _item == null) return;
        var cfg = UiSettings.Current.GetOrCreateConfig(_item.Path);
        cfg.Author = string.IsNullOrWhiteSpace(AuthorBox.Text)
            ? null : AuthorBox.Text.Trim();
        UiSettings.Current.Save();
    }

    private void OnLicenseLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _item == null) return;
        var cfg = UiSettings.Current.GetOrCreateConfig(_item.Path);
        cfg.License = string.IsNullOrWhiteSpace(LicenseBox.Text)
            ? null : LicenseBox.Text.Trim();
        UiSettings.Current.Save();
    }

    private void OnNotesLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _item == null) return;
        var cfg = UiSettings.Current.GetOrCreateConfig(_item.Path);
        cfg.Notes = string.IsNullOrWhiteSpace(NotesBox.Text)
            ? null : NotesBox.Text.Trim();
        UiSettings.Current.Save();
    }

    // ============================================================
    //  Locate file — same flow as the sidebar
    // ============================================================
    private async void OnLocateFileClick(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;

        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".wmv");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string oldPath = _item.Path;
            string newPath = file.Path;

            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["old_path"] = oldPath,
                ["new_path"] = newPath
            };

            var client = new EnginePipeClient();
            var resp = await client.SendAsync("relocate_library_file", body);

            // Migrate local ui-settings key regardless of engine response.
            var cfg = UiSettings.Current.GetConfig(oldPath);
            if (cfg != null && !string.Equals(oldPath, newPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                UiSettings.Current.WallpaperOverrides.Remove(oldPath);
                UiSettings.Current.WallpaperOverrides[newPath] = cfg;
                UiSettings.Current.Save();
            }

            // Reflect in the item so the user sees the change immediately.
            _item.Path = newPath;
            _item.DisplayName = Path.GetFileName(newPath);
            _item.Folder = Path.GetDirectoryName(newPath) ?? "";
            _item.IsMissing = false;
            DetailFileName.Text = _item.DisplayName;
            DetailFolder.Text = _item.Folder;
            MissingBanner.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Details] Locate failed: {ex.Message}");
        }
    }
}