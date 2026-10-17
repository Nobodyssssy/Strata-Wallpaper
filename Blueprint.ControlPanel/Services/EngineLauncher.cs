using System;
using System.Diagnostics;
using System.IO;

namespace Blueprint.ControlPanel.Services;

/// <summary>
/// Finds and starts the C++ engine executable.
/// </summary>
public static class EngineLauncher
{
    private const string EngineExeName = "StrataWallpaper.exe";

    /// <summary>
    /// Tries to locate the engine .exe relative to our own folder.
    /// Both projects build into the same solution tree.
    /// Prefers whichever of Debug/Release was modified most recently.
    /// </summary>
    public static string? FindEnginePath()
    {
        var dir = AppContext.BaseDirectory;
        string? bestPath = null;
        DateTime bestTime = DateTime.MinValue;

        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir, "x64", config, EngineExeName);
                if (File.Exists(candidate))
                {
                    var t = File.GetLastWriteTimeUtc(candidate);
                    if (t > bestTime) { bestTime = t; bestPath = candidate; }
                }
            }

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null) break;
            dir = parent;
        }

        return bestPath;
    }

    /// <summary>
    /// Starts the engine .exe (fire-and-forget).
    /// Returns true if a process was started, false if we couldn't find it.
    /// </summary>
    public static bool StartEngine()
    {
        var path = FindEnginePath();
        if (path == null) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsEngineRunning()
    {
        try
        {
            return Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(EngineExeName)).Length > 0;
        }
        catch { return false; }
    }
}