using Blueprint.ControlPanel.Services;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Blueprint.ControlPanel;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    private static Mutex? _singleInstanceMutex;
    internal static EventWaitHandle? ShowPanelEvent { get; private set; }

    private const string WindowTitle = "Strata Wallpaper";
    private const string MutexName = "Local\\StrataWallpaper_Panel_SingleInstance";
    internal const string ShowPanelEventName = "Local\\StrataWallpaper_Panel_Show";

    public App()
    {
        this.InitializeComponent();

        try
        {
            // 1. Load the Base Theme dictionary (Aura, Catppuccin, Dracula)
            ThemeService.ApplyBaseTheme(this, UiSettings.Current.BaseTheme ?? "Aura");

            // 2. Apply the Font
            ThemeService.ApplyFont(this, UiSettings.Current.UseThemeFont);

            // 3. Apply the Accent Color
            ThemeService.Apply(this, ThemeService.Get(UiSettings.Current.AccentScheme));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Strata] theme apply failed: {ex}");
        }

        this.UnhandledException += (sender, e) =>
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Strata] Unhandled exception: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            SignalExistingWindow();
            ActivateExistingWindow();
            Environment.Exit(0);
            return;
        }

        ShowPanelEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, ShowPanelEventName);

        // Apply theme
        ThemeService.ApplyBaseTheme(Application.Current, UiSettings.Current.BaseTheme ?? "Aura");
        ThemeService.ApplyFont(this, UiSettings.Current.UseThemeFont);
        ThemeService.Apply(this, ThemeService.Get(UiSettings.Current.AccentScheme));

        // Create window
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>Called from SettingsPage when the user picks a new accent.</summary>
    public static void ApplyAccent(string schemeId)
    {
        if (Current is App app)
            ThemeService.Apply(app, ThemeService.Get(schemeId));
    }

    private static void ActivateExistingWindow()
    {
        var hwnd = FindWindowW(null, WindowTitle);
        if (hwnd != IntPtr.Zero)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
    }

    private static void SignalExistingWindow()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowPanelEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
}