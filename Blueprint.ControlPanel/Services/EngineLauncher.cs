using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;

namespace Blueprint.ControlPanel.Services;

/// <summary>
/// Finds and starts the C++ engine executable.
/// </summary>
public static class EngineLauncher
{
    private const string EngineExeName = "StrataWallpaper.exe";

    /// <summary>
    /// Locates the engine .exe relative to the control panel.  Do not use a
    /// first-match search here: a stale Release binary is commonly found
    /// before the freshly built Debug binary.
    /// </summary>
    public static string? FindEnginePath()
    {
        var candidates = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            foreach (var root in new[] { dir.FullName, Path.Combine(dir.FullName, "Blueprintengine") })
            {
                foreach (var config in new[] { "Debug", "Release" })
                {
                    foreach (var subdir in new[] { $"x64\\{config}", config, $"bin\\x64\\{config}" })
                    {
                        var candidate = Path.GetFullPath(Path.Combine(root, subdir, EngineExeName));
                        if (File.Exists(candidate)) candidates.Add(candidate);
                    }
                }
            }
        }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(p => p.Contains("\\Debug\\", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>
    /// Starts the engine .exe (fire-and-forget).
    /// Returns true if a process was started, false if we couldn't find it.
    /// </summary>
    public static bool StartEngine(string? expectedPath = null)
    {
        var path = expectedPath ?? FindEnginePath();
        if (path == null) return false;

        try
        {
            // The engine is single-instance.  Starting a newly built copy
            // while an older copy is alive otherwise just causes the new
            // process to exit immediately, leaving the old engine.log in use.
            foreach (var process in Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(EngineExeName)))
            {
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    Debug.WriteLine($"[EngineLauncher] running PID={process.Id} EXE={runningPath}");
                    if (!TryGracefulShutdown())
                    {
                        var expectedFullPath = Path.GetFullPath(path);
                        var runningFullPath = runningPath == null ? null : Path.GetFullPath(runningPath);
                        if (runningFullPath != null
                            && string.Equals(runningFullPath, expectedFullPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            // Keep a bounded forceful fallback for the expected
                            // engine when it is unresponsive or does not answer.
                            process.Kill(entireProcessTree: true);
                        }
                        else
                        {
                            Debug.WriteLine($"[EngineLauncher] refusing to kill PID={process.Id}: executable path was not verified");
                        }
                    }
                    process.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EngineLauncher] could not stop PID={process.Id}: {ex.Message}");
                }
                finally { process.Dispose(); }
            }

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Debug.WriteLine($"[EngineLauncher] started PID={started?.Id} EXE={path}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGracefulShutdown()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", "BlueprintEngine", PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(1000);
            pipe.ReadMode = PipeTransmissionMode.Message;
            pipe.ReadTimeout = 2000;

            byte[] request = Encoding.UTF8.GetBytes(
                "{\"version\":1,\"id\":\"launcher-shutdown\",\"type\":\"shutdown_engine\",\"body\":{}}");
            pipe.Write(request, 0, request.Length);
            pipe.Flush();

            var response = new StringBuilder();
            var buffer = new byte[4096];
            do
            {
                int read = pipe.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                response.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            while (!pipe.IsMessageComplete);

            return response.ToString().Contains("\"status\":\"ok\"",
                StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EngineLauncher] graceful shutdown failed: {ex.Message}");
            return false;
        }
    }

    public static bool IsEngineRunning()
    {
        return GetRunningEnginePath() != null;
    }

    /// <summary>Returns the executable path of the running engine, if any.</summary>
    public static string? GetRunningEnginePath()
    {
        try
        {
            using var process = Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(EngineExeName)).FirstOrDefault();
            return process?.MainModule?.FileName;
        }
        catch { return null; }
    }
}