using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blueprint.ControlPanel.Services;

public sealed class UiSettings
{
    private static UiSettings? _current;
    public static UiSettings Current => _current ??= Load();

    // ── Appearance ─────────────────────────────────────────
    public string BaseTheme { get; set; } = "Aura";     // "Aura"|"Catppuccin"|"Dracula"
    public string Theme { get; set; } = "System";       // "System"|"Light"|"Dark"
    public string AccentScheme { get; set; } = "default";
    public bool UseThemeFont { get; set; } = true;

    // ── Window behaviour ───────────────────────────────────
    public string CloseAction { get; set; } = "ask";    // "ask"|"exit"|"tray"
    public bool MinimizeToTray { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;

    // ── Engine executable path (remembered once the user picks it) ──
    public string EnginePath { get; set; } = "";

    // ── Global performance defaults (Performance page writes here) ─
    public GlobalWallpaperDefaults GlobalDefaults { get; set; } = new();

    // ── Per-wallpaper config, keyed by absolute path ───────
    public Dictionary<string, WallpaperConfig> WallpaperOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Recently applied wallpapers ────────────────────────
    public List<RecentWallpaper> RecentWallpapers { get; set; } = new();

    // ── Remembered library folders (mirrored to settings.ini for the engine) ─
    public List<string> LibraryFolders { get; set; } = new();

    // ───────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static string GetPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blueprint");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ui-settings.json");
    }

    public static UiSettings Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return new UiSettings();
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<UiSettings>(json, JsonOpts);
            if (loaded == null) return new UiSettings();

            // Restore the case-insensitive comparer that JsonSerializer drops.
            loaded.WallpaperOverrides = new Dictionary<string, WallpaperConfig>(
                loaded.WallpaperOverrides, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UiSettings.Load] {ex}");
            return new UiSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(GetPath(), json);
            Debug.WriteLine($"[UiSettings.Save] theme={Theme} accent={AccentScheme} close={CloseAction}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UiSettings.Save] FAILED: {ex}");
        }
    }

    // ── Helpers ────────────────────────────────────────────
    public WallpaperConfig GetOrCreateConfig(string path)
    {
        if (!WallpaperOverrides.TryGetValue(path, out var cfg))
        {
            cfg = new WallpaperConfig();
            WallpaperOverrides[path] = cfg;
        }
        return cfg;
    }

    public WallpaperConfig? GetConfig(string path)
        => WallpaperOverrides.TryGetValue(path, out var cfg) ? cfg : null;

    public void RecordApplied(string path)
    {
        RecentWallpapers.RemoveAll(r =>
            string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentWallpapers.Insert(0, new RecentWallpaper
        {
            Path = path,
            AppliedUtc = DateTime.UtcNow
        });
        if (RecentWallpapers.Count > 10)
            RecentWallpapers.RemoveRange(10, RecentWallpapers.Count - 10);

        GetOrCreateConfig(path).LastAppliedUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>Resolved FPS for a wallpaper, taking overrides into account.</summary>
    public int ResolveFps(string path)
    {
        var cfg = GetConfig(path);
        return cfg?.FpsOverride ?? GlobalDefaults.Fps;
    }

    public string ResolveQuality(string path)
    {
        var cfg = GetConfig(path);
        return cfg?.QualityOverride ?? GlobalDefaults.Quality;
    }

    public string ResolveDisplayMode(string path)
    {
        var cfg = GetConfig(path);
        return cfg?.DisplayModeOverride ?? GlobalDefaults.DisplayMode;
    }
}