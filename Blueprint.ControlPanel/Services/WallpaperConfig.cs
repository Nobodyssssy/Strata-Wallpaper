using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Blueprint.ControlPanel.Services;

/// <summary>
/// Per-wallpaper configuration. Nullable fields mean "inherit from GlobalDefaults".
/// Non-null means "override".
/// </summary>
public sealed class WallpaperConfig
{
    // ── Performance overrides ──────────────────────────────
    public int? FpsOverride { get; set; }              // 15/20/25/30/60, null = inherit
    public string? QualityOverride { get; set; }       // "fast"|"balanced"|"high"|"vsr"
    public string? DisplayModeOverride { get; set; }   // "copy"|"stretch"|"individual"
    public bool? ApplyToAllMonitors { get; set; }

    // ── Day / Night override ───────────────────────────────
    public bool? DayNightEnabled { get; set; }
    public string? DayNightMode { get; set; }          // "manual"|"sunrise"|"prayer"
    public string? DayNightDayVideo { get; set; }
    public string? DayNightNightVideo { get; set; }

    // ── Lock screen usage ──────────────────────────────────
    public string? LockScreenUse { get; set; }         // "global"|"custom"|"disabled"
    public string? LockScreenVideo { get; set; }

    // ── Library flags ──────────────────────────────────────
    public bool IsFavorite { get; set; }

    // ── Engine-provided metadata (cached, not authoritative) ─
    public string? MediaType { get; set; }             // "video"|"live2d"
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public long? FileSizeBytes { get; set; }
    public bool IsMissing { get; set; }
    public DateTime? MetadataFetchedUtc { get; set; }

    // ── Catalogue info (from user input; never inferred) ───
    public List<string> Tags { get; set; } = new();
    public string? SourceUrl { get; set; }
    public string? License { get; set; }
    public string? Author { get; set; }
    public string? Notes { get; set; }

    // ── Usage tracking ─────────────────────────────────────
    public DateTime? LastAppliedUtc { get; set; }
}

/// <summary>
/// Global defaults the Performance page writes to.
/// Per-wallpaper configs inherit any field they don't override.
/// </summary>
public sealed class GlobalWallpaperDefaults
{
    public int Fps { get; set; } = 25;
    public string Quality { get; set; } = "balanced";
    public string DisplayMode { get; set; } = "copy";

    public bool HardwareDecoding { get; set; } = false;
    public bool VSync { get; set; } = false;
    public bool LowLatency { get; set; } = false;

    public bool ApplyToAllMonitors { get; set; } = true;
    public bool ShuffleMode { get; set; } = false;
}

public sealed class RecentWallpaper
{
    public string Path { get; set; } = "";
    public DateTime AppliedUtc { get; set; }
}