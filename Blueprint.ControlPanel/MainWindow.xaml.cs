using Blueprint.ControlPanel.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.ControlPanel;

public sealed partial class MainWindow : Window
{
    private bool _forceClose = false;
    private readonly EnginePipeClient _client = new();

    private CancellationTokenSource _cts = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _restoreTimer;
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

        // ── Custom title bar. ──
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.BackgroundColor = Colors.Transparent;
        }

        // Force the title bar to update its colors when the theme changes
        if (this.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (s, e) => UpdateTitleBarColors();
        }
        UpdateTitleBarColors();

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

        _restoreTimer = DispatcherQueue.CreateTimer();
        _restoreTimer.Interval = TimeSpan.FromMilliseconds(250);
        _restoreTimer.IsRepeating = true;
        _restoreTimer.Tick += (_, __) =>
        {
            try
            {
                if (App.ShowPanelEvent?.WaitOne(0) == true)
                    RestoreWindow();
            }
            catch { }
        };
        _restoreTimer.Start();
    }

    private void UpdateTitleBarColors()
    {
        try
        {
            var tb = AppWindow.TitleBar;
            bool isDark = (this.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

            // Use hardcoded high-contrast colors based on the active theme
            var foreground = isDark ? Colors.White : Colors.Black;
            var hoverBg = isDark ? Windows.UI.Color.FromArgb(20, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            var pressedBg = isDark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(40, 0, 0, 0);

            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = foreground;
            tb.ButtonInactiveForegroundColor = Colors.Gray;
            tb.ButtonHoverBackgroundColor = hoverBg;
            tb.ButtonHoverForegroundColor = foreground;
            tb.ButtonPressedBackgroundColor = pressedBg;
            tb.ButtonPressedForegroundColor = foreground;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TitleBar] {ex}");
        }
    }

    // Called when the engine tray menu tries to open the panel
    public void RestoreWindow()
    {
        this.AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter p)
            p.Restore();
        this.Activate();
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
                EngineDot.Fill = GetThemeBrush("StatusOfflineBrush");
                EngineStateText.Text = "Engine offline";
                EngineDetailText.Text = "—";
                return;
            }

            var body = resp["body"];
            int fps = body?["target_fps"]?.GetValue<int>() ?? 30;
            int monitors = body?["monitor_count"]?.GetValue<int>() ?? 0;

            EngineDot.Fill = GetThemeBrush("StatusOnlineBrush");
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
        _restoreTimer?.Stop();
        try { App.ShowPanelEvent?.Dispose(); } catch { }
        if (_forceClose)
            Environment.Exit(0);
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
            await HideToTrayOrExitAsync();
            return;
        }

        args.Cancel = true;

        var rememberCheck = new CheckBox
        {
            Content = "Remember my choice and don't ask again",
            Margin = new Thickness(0, 8, 0, 0)
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = "Closing the control panel does not have to stop your live wallpapers.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        string? selectedAction = null;
        ContentDialog? dialog = null;

        void HandleSelection(string selected)
        {
            selectedAction = selected;
            dialog?.Hide();
        }

        panel.Children.Add(CreateCloseChoiceButton("Minimize to system tray", "Close this window but keep your live wallpapers running in the background.", "Recommended for everyday use", "tray", HandleSelection));
        panel.Children.Add(CreateCloseChoiceButton("Exit Strata Wallpaper", "Stop the wallpaper engine and close the control panel. Your wallpapers will no longer animate.", "", "exit", HandleSelection));
        panel.Children.Add(CreateCloseChoiceButton("Keep Strata open", "Return to the control panel without closing anything.", "", "cancel", HandleSelection));

        panel.Children.Add(rememberCheck);

        dialog = new ContentDialog
        {
            Title = "Close Strata Wallpaper?",
            Content = panel,
            MinWidth = 560,
            RequestedTheme = (this.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            XamlRoot = (this.Content as FrameworkElement)?.XamlRoot
        };

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                dialog.Hide();
            }
        };

        await dialog.ShowAsync();

        if (selectedAction == "exit")
        {
            if (rememberCheck.IsChecked == true) SaveCloseAction("exit");
            _forceClose = true;
            await ShutdownEngineAsync();
            this.Close();
        }
        else if (selectedAction == "tray")
        {
            if (rememberCheck.IsChecked == true) SaveCloseAction("tray");
            await HideToTrayOrExitAsync();
        }
        else
        {
            Activate();
        }
    }

    private async Task HideToTrayOrExitAsync()
    {
        if (await _client.SendAsync("get_settings") == null)
        {
            _forceClose = true;
            Close();
            return;
        }

        AppWindow.Hide();
    }

    private static Button CreateCloseChoiceButton(string title, string description, string note, string action, Action<string> onSelected)
    {
        var grid = new Grid
        {
            ColumnSpacing = 16,
            Padding = new Thickness(16)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconGlyph = action switch
        {
            "tray" => "\uE78C",
            "exit" => "\uE7E8",
            _ => "\uE711"
        };
        var icon = new FontIcon
        {
            Glyph = iconGlyph,
            FontSize = 18,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconBox = new Border
        {
            Child = icon,
            CornerRadius = new CornerRadius(8),
            Width = 40,
            Height = 40,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var details = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        });
        details.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        if (!string.IsNullOrWhiteSpace(note))
        {
            details.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Tag = action,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };

        button.Click += (_, _) => onSelected(action);
        return button;
    }

    private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value)
            && value is Microsoft.UI.Xaml.Media.Brush brush)
        {
            return brush;
        }

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
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