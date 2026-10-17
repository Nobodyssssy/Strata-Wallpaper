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

    private const string WindowTitle = "Strata Wallpaper";
    private const string MutexName = "Local\\StrataWallpaper_Panel_SingleInstance";

    public App()
    {
        this.InitializeComponent();

        // ── Apply the persisted accent scheme BEFORE the first window is created.
        // Overrides Application.Resources so every ThemeResource that reads
        // SystemAccentColor* picks up the chosen palette.
        try
        {
            ThemeService.Apply(this, ThemeService.Get(UiSettings.Current.AccentScheme));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Strata] accent apply failed: {ex}");
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
            ActivateExistingWindow();
            Environment.Exit(0);
            return;
        }

        // Apply theme
        ThemeService.ApplyBaseTheme(Application.Current, UiSettings.Current.BaseTheme ?? "Aura");

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