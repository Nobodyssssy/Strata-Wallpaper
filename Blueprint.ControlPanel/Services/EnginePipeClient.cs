using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.ControlPanel.Services;

/// <summary>
/// Talks to the C++ engine over the named pipe \\.\pipe\BlueprintEngine.
/// One request → one response. Serialized globally so overlapping UI
/// actions can never open two pipes at once.
/// </summary>
public sealed class EnginePipeClient
{
    private const string PipeName = "BlueprintEngine";
    private static int _nextId = 0;

    private static readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<JsonNode?> SendAsync(string type,
                                           JsonObject? body = null,
                                           CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await pipe.ConnectAsync(800, ct);
            pipe.ReadMode = PipeTransmissionMode.Message;

            var id = Interlocked.Increment(ref _nextId).ToString();
            var request = new JsonObject
            {
                ["version"] = 1,
                ["id"] = id,
                ["type"] = type,
                ["body"] = body ?? new JsonObject()
            };

            var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
            await pipe.WriteAsync(payload, ct);

            using var ms = new System.IO.MemoryStream();
            var buffer = new byte[64 * 1024];
            do
            {
                int n = await pipe.ReadAsync(buffer, ct);
                if (n == 0) break;
                ms.Write(buffer, 0, n);
            } while (!pipe.IsMessageComplete);

            if (ms.Length == 0) return null;

            var text = Encoding.UTF8.GetString(ms.ToArray());
            return JsonNode.Parse(text);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Typed helpers — per-wallpaper settings
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a wallpaper with optional per-wallpaper overrides.
    /// Any override that is null falls back to the engine's global defaults.
    /// </summary>
    public async Task<bool> ApplyWallpaperAsync(string path,
                                                WallpaperConfig? overrides = null,
                                                string monitor = "all",
                                                CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["path"] = path,
            ["monitor"] = monitor
        };

        if (overrides != null)
        {
            var ov = new JsonObject();
            if (overrides.FpsOverride is int f) ov["fps"] = f;
            if (!string.IsNullOrEmpty(overrides.QualityOverride))
                ov["quality"] = overrides.QualityOverride;
            if (!string.IsNullOrEmpty(overrides.DisplayModeOverride))
                ov["display_mode"] = overrides.DisplayModeOverride;
            if (overrides.ApplyToAllMonitors is bool atm)
                ov["apply_all_monitors"] = atm;

            if (overrides.DayNightEnabled is bool dnEn)
            {
                var dn = new JsonObject { ["enabled"] = dnEn };
                if (!string.IsNullOrEmpty(overrides.DayNightMode))
                    dn["mode"] = overrides.DayNightMode;
                if (!string.IsNullOrEmpty(overrides.DayNightDayVideo))
                    dn["day_video"] = overrides.DayNightDayVideo;
                if (!string.IsNullOrEmpty(overrides.DayNightNightVideo))
                    dn["night_video"] = overrides.DayNightNightVideo;
                ov["daynight"] = dn;
            }

            body["overrides"] = ov;
        }

        var resp = await SendAsync("apply_wallpaper", body, ct);
        return resp?["status"]?.GetValue<string>() == "ok";
    }

    /// <summary>Ask the engine for codec / resolution / duration / live2d-detection.</summary>
    public Task<JsonNode?> GetWallpaperMetadataAsync(string path,
                                                     CancellationToken ct = default)
    {
        var body = new JsonObject { ["path"] = path };
        return SendAsync("get_wallpaper_metadata", body, ct);
    }

    /// <summary>Ask the engine to check every library path and report the missing ones.</summary>
    public Task<JsonNode?> ValidateLibraryAsync(CancellationToken ct = default)
        => SendAsync("validate_library", null, ct);

    /// <summary>
    /// Engine-side shuffle control.
    /// <paramref name="mode"/>: "off" | "random" | "weighted" | "favorites_only".
    /// Pass <paramref name="favorites"/> to replace the engine's favorite
    /// set (used by weighted and favorites_only). Null leaves it unchanged.
    /// </summary>
    public async Task<bool> SetShuffleAsync(string mode,
                                            IEnumerable<string>? favorites = null,
                                            CancellationToken ct = default)
    {
        var body = new JsonObject { ["mode"] = mode };
        if (favorites != null)
        {
            var arr = new JsonArray();
            foreach (var p in favorites) arr.Add(p);
            body["favorites"] = arr;
        }
        var resp = await SendAsync("set_shuffle", body, ct);
        return resp?["status"]?.GetValue<string>() == "ok";
    }

    /// <summary>Backward-compat bool overload.</summary>
    public Task<bool> SetShuffleAsync(bool enabled, CancellationToken ct = default)
        => SetShuffleAsync(enabled ? "random" : "off", null, ct);

    /// <summary>Assign a lock wallpaper. monitor = "all" | "0" | "1".</summary>
    public async Task<bool> SetLockWallpaperAsync(string path,
                                                  string monitor = "all",
                                                  CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["path"] = path,
            ["monitor"] = monitor
        };
        var resp = await SendAsync("set_lock_wallpaper", body, ct);
        return resp?["status"]?.GetValue<string>() == "ok";
    }

    /// <summary>Push the global performance defaults. Called from PerformancePage.</summary>
    public async Task<bool> SetGlobalPerformanceAsync(GlobalWallpaperDefaults d,
                                                      CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["fps"] = d.Fps,
            ["quality"] = d.Quality,
            ["display_mode"] = d.DisplayMode,
            ["hardware_decoding"] = d.HardwareDecoding,
            ["vsync"] = d.VSync,
            ["low_latency"] = d.LowLatency
        };
        var resp = await SendAsync("set_performance_defaults", body, ct);
        return resp?["status"]?.GetValue<string>() == "ok";
    }
}