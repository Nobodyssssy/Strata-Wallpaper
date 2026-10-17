using Blueprint.ControlPanel.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.ControlPanel;

public sealed partial class MainWindow : Window
{
    private bool _forceClose = false;
    private readonly EnginePipeClient _client = new();

    private CancellationTokenSource _cts = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private bool _statusPollBusy = false;

    public MainWindow()
    {
        InitializeComponent();

        var baseDir = AppContext.BaseDirectory;
        var icoPath = Path.Combine(baseDir, "Strata.ico");
        var pngPath = Path.Combine(baseDir, "Strata.png");

        try
        {
            if (File.Exists(icoPath))
                this.AppWindow.SetIcon(icoPath);
        }
        catch { }

        try
        {
            var logoPath = File.Exists(pngPath) ? pngPath
                         : File.Exists(icoPath) ? icoPath
                         : null;
            if (logoPath != null)
            {
                var bmp = new BitmapImage(new Uri(logoPath));
                TitleIcon.Source = bmp;
            }
        }
        catch { }

        // ── Custom title bar. This is the pattern that works. ──
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Force the title bar to use the theme colors
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.BackgroundColor = Colors.Transparent;
        }

        // Make the caption buttons visibly react to hover / press.
        try
        {
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 130, 130, 130);
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 60, 60, 60);
            tb.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 90, 90, 90);
            tb.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TitleBar] colors: {ex}");
        }

        ApplyThemeToRoot(UiSettings.Current.Theme);
        UpdateThemeFooterLabel(UiSettings.Current.Theme);

        Nav.SelectedItem = Nav.MenuItems[0];

        this.AppWindow.Closing += OnAppWindowClosing;
        this.Closed += OnWindowClosed;

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(5);
        _statusTimer.IsRepeating = true;
        _statusTimer.Tick += async (_, __) => await PollEngineStatusAsync();
        _statusTimer.Start();
        _ = PollEngineStatusAsync();
    }

    // ── Navigation ──
    private void OnNavSelectionChanged(NavigationView sender,
                                       NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string;

        try
        {
            switch (tag)
            {
                case "home": ContentFrame.Navigate(typeof(HomePage)); break;
                case "wallpapers": ContentFrame.Navigate(typeof(LibraryPage)); break;
                case "performance": ContentFrame.Navigate(typeof(PerformancePage)); break;
                case "lock": ContentFrame.Navigate(typeof(LockPage)); break;
                case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Nav] FAILED '{tag}': {ex}");
            _ = new ContentDialog
            {
                Title = $"Navigation failed: {tag}",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
            }.ShowAsync();
        }
    }

    public void NavigateToTag(string tag)
    {
        for (int i = 0; i < Nav.MenuItems.Count; i++)
        {
            if (Nav.MenuItems[i] is NavigationViewItem item
                && (item.Tag as string) == tag)
            {
                Nav.SelectedItem = item;
                return;
            }
        }
    }

    // ── Theme ──
    private void ApplyThemeToRoot(string theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void UpdateThemeFooterLabel(string theme)
    {
        ThemeLabel.Text = theme;
        ThemeIcon.Glyph = theme switch
        {
            "Light" => "\uE706",
            "Dark" => "\uE708",
            _ => "\uE770"
        };
    }

    public void RefreshThemeFooterLabel()
    {
        var theme = UiSettings.Current.Theme;
        ApplyThemeToRoot(theme);
        UpdateThemeFooterLabel(theme);
    }

    private void OnThemeMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem it || it.Tag is not string theme) return;

        var ui = UiSettings.Current;
        ui.Theme = theme;
        ui.Save();

        ApplyThemeToRoot(theme);
        UpdateThemeFooterLabel(theme);
    }

    // ── Engine status poll ──
    private async Task PollEngineStatusAsync()
    {
        if (_statusPollBusy || _cts.IsCancellationRequested) return;
        _statusPollBusy = true;
        try
        {
            var resp = await _client.SendAsync("get_settings", ct: _cts.Token);
            if (_cts.IsCancellationRequested) return;

            if (resp == null)
            {
                EngineDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 107, 114, 128));
                EngineStateText.Text = "Engine offline";
                EngineDetailText.Text = "—";
                return;
            }

            var body = resp["body"];
            int fps = body?["target_fps"]?.GetValue<int>() ?? 30;
            int monitors = body?["monitor_count"]?.GetValue<int>() ?? 0;

            EngineDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 34, 197, 94));
            EngineStateText.Text = "Engine Running";
            EngineDetailText.Text = monitors > 0
                ? $"{monitors} monitor{(monitors == 1 ? "" : "s")} · {fps} FPS"
                : $"{fps} FPS";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StatusPoll] {ex}");
        }
        finally
        {
            _statusPollBusy = false;
        }
    }

    // ── Close / exit ──
    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _forceClose = true;
            await ShutdownEngineAsync();
            this.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Exit] {ex}");
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _cts.Cancel();
        _statusTimer?.Stop();
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose) return;

        var action = UiSettings.Current.CloseAction;

        if (action == "exit")
        {
            args.Cancel = true;
            _forceClose = true;
            await ShutdownEngineAsync();
            this.Close();
            return;
        }
        if (action == "tray")
        {
            args.Cancel = true;
            if (AppWindow.Presenter is OverlappedPresenter p)
                p.Minimize();
            return;
        }

        args.Cancel = true;

        var rememberCheck = new CheckBox { Content = "Don't ask again" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "What would you like Strata to do?",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(rememberCheck);

        var dialog = new ContentDialog
        {
            Title = "Close Strata Wallpaper",
            Content = panel,
            PrimaryButtonText = "Exit",
            SecondaryButtonText = "Minimize to tray",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            if (rememberCheck.IsChecked == true) SaveCloseAction("exit");
            _forceClose = true;
            await ShutdownEngineAsync();
            this.Close();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            if (rememberCheck.IsChecked == true) SaveCloseAction("tray");
            _forceClose = true;
            this.Close();
        }
    }

    private async Task ShutdownEngineAsync()
    {
        try { await _client.SendAsync("shutdown_engine"); }
        catch { }
    }

    private static void SaveCloseAction(string action)
    {
        var s = UiSettings.Current;
        s.CloseAction = action;
        s.Save();
    }
}